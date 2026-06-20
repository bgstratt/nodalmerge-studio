using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 13b — WorkSchedulerService writes every queue mutation to IStudioNodeStore but never
/// read itself back on startup, so a host restart silently dropped pending and in-flight work.
/// Reuses 13a's dual-StudioWebApplication-instance harness (same temp Sqlite db + workspace
/// root, second instance rehydrated via IRehydratable rather than a full host start) against the
/// production storage path.
/// </summary>
[Trait("Category", "Integration")]
public class WorkSchedulerRehydrationTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-scheduler-rehydrate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildApp()
    {
        var sqliteDbPath = Path.Combine(_tempRoot, "nodes.db");
        var blobsRootPath = Path.Combine(_tempRoot, "blobs");
        var workspaceRootPath = Path.Combine(_tempRoot, "workspace");

        return StudioWebApplication.Build(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = sqliteDbPath,
                ["NodalMerge:Storage:FileBlobs:RootPath"] = blobsRootPath,
                ["Workspace:RootPath"] = workspaceRootPath,
            }));
    }

    private static async Task RehydrateAsync(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        foreach (var rehydratable in app.Services.GetServices<IRehydratable>())
            await rehydratable.RehydrateAsync();
    }

    [Fact]
    public async Task Pending_and_leased_items_survive_a_restart_and_leases_are_cleared()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var scheduler1 = app1.Services.GetRequiredService<IWorkScheduler>();

        var unitA = await orchestrator1.CreateWorkUnitAsync("Task A", "test");
        var unitB = await orchestrator1.CreateWorkUnitAsync("Task B", "test");

        await scheduler1.EnqueueAsync(unitA.WorkUnitId, "worker");
        await scheduler1.EnqueueAsync(unitB.WorkUnitId, "worker");

        // TryAcquireAsync iterates a ConcurrentDictionary, so which of the two items it returns
        // first isn't guaranteed — label "leased" / "unleased" by what actually happened rather
        // than presupposing it, instead of trying to force a specific item to be the one leased.
        var acquired = await scheduler1.TryAcquireAsync("agent-1");
        Assert.NotNull(acquired);
        Assert.Equal(1, acquired!.AttemptCount);

        var leasedWorkUnitId = acquired.WorkUnitId;
        var unleasedWorkUnitId = leasedWorkUnitId == unitA.WorkUnitId ? unitB.WorkUnitId : unitA.WorkUnitId;

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);

        var scheduler2 = app2.Services.GetRequiredService<IWorkScheduler>();
        var pending2 = await scheduler2.ListPendingAsync();

        Assert.Equal(2, pending2.Count);

        var rehydratedLeased = pending2.Single(i => i.WorkUnitId == leasedWorkUnitId);
        Assert.Null(rehydratedLeased.LeasedBy);
        Assert.Null(rehydratedLeased.LeasedAt);
        Assert.Equal(1, rehydratedLeased.AttemptCount);
        // Phase 8c — a held lease means a worker was actively executing this when the Host died,
        // so rehydrate flags it for human approval instead of leaving it silently acquirable.
        Assert.True(rehydratedLeased.AwaitingResume);

        var rehydratedUnleased = pending2.Single(i => i.WorkUnitId == unleasedWorkUnitId);
        Assert.Null(rehydratedUnleased.LeasedBy);
        Assert.Equal(0, rehydratedUnleased.AttemptCount);
        // Never leased (still queued, not yet started) — nothing was interrupted, so it's
        // untouched and stays immediately acquirable.
        Assert.False(rehydratedUnleased.AwaitingResume);

        var first = await scheduler2.TryAcquireAsync("agent-2");
        Assert.NotNull(first);
        Assert.Equal(unleasedWorkUnitId, first!.WorkUnitId);

        // The AwaitingResume item is NOT acquirable until a human approves it.
        var second = await scheduler2.TryAcquireAsync("agent-3");
        Assert.Null(second);

        await scheduler2.ApproveResumeAsync(leasedWorkUnitId);
        var third = await scheduler2.TryAcquireAsync("agent-3");
        Assert.NotNull(third);
        Assert.Equal(leasedWorkUnitId, third!.WorkUnitId);
    }

    [Fact]
    public async Task ListAwaitingResumeAsync_returns_only_items_interrupted_by_restart()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var scheduler1 = app1.Services.GetRequiredService<IWorkScheduler>();

        var unitA = await orchestrator1.CreateWorkUnitAsync("Task A", "test");
        var unitB = await orchestrator1.CreateWorkUnitAsync("Task B", "test");
        await scheduler1.EnqueueAsync(unitA.WorkUnitId, "worker");
        await scheduler1.EnqueueAsync(unitB.WorkUnitId, "worker");

        var acquired = await scheduler1.TryAcquireAsync("agent-1");
        Assert.NotNull(acquired);
        var leasedWorkUnitId = acquired!.WorkUnitId;

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);
        var scheduler2 = app2.Services.GetRequiredService<IWorkScheduler>();

        var awaitingResume = await scheduler2.ListAwaitingResumeAsync();
        Assert.Single(awaitingResume);
        Assert.Equal(leasedWorkUnitId, awaitingResume[0].WorkUnitId);
    }

    [Fact]
    public async Task ApproveResumeAllAsync_clears_every_flagged_item_and_returns_the_count()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var scheduler1 = app1.Services.GetRequiredService<IWorkScheduler>();

        var unitA = await orchestrator1.CreateWorkUnitAsync("Task A", "test");
        var unitB = await orchestrator1.CreateWorkUnitAsync("Task B", "test");
        await scheduler1.EnqueueAsync(unitA.WorkUnitId, "worker");
        await scheduler1.EnqueueAsync(unitB.WorkUnitId, "worker");

        await scheduler1.TryAcquireAsync("agent-1");
        await scheduler1.TryAcquireAsync("agent-2");

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);
        var scheduler2 = app2.Services.GetRequiredService<IWorkScheduler>();

        Assert.Equal(2, (await scheduler2.ListAwaitingResumeAsync()).Count);

        var resumedCount = await scheduler2.ApproveResumeAllAsync();
        Assert.Equal(2, resumedCount);
        Assert.Empty(await scheduler2.ListAwaitingResumeAsync());

        var first = await scheduler2.TryAcquireAsync("agent-3");
        var second = await scheduler2.TryAcquireAsync("agent-4");
        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task TryAcquireAsync_skips_items_whose_session_is_paused()
    {
        var app = BuildApp();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var sessions = app.Services.GetRequiredService<IExecutionSessionService>();

        var unit = await orchestrator.CreateWorkUnitAsync("Task A", "test");
        var session = await sessions.CreateAsync(unit.WorkUnitId, "{}", ["worker"]);

        await scheduler.EnqueueAsync(unit.WorkUnitId, "worker", sessionId: session.SessionId);

        await sessions.SetStatusAsync(session.SessionId, ExecutionSessionStatus.Paused);
        Assert.Null(await scheduler.TryAcquireAsync("agent-1"));

        await sessions.SetStatusAsync(session.SessionId, ExecutionSessionStatus.Active);
        var acquired = await scheduler.TryAcquireAsync("agent-1");
        Assert.NotNull(acquired);
        Assert.Equal(unit.WorkUnitId, acquired!.WorkUnitId);
    }
}
