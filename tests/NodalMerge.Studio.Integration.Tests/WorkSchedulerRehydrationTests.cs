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
[Collection("Sqlite")]
public class WorkSchedulerRehydrationTests : IAsyncLifetime
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-scheduler-rehydrate-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry. The old
    // synchronous Dispose ran an un-retried Directory.Delete that raced background writes to
    // nodes.db and flaked; ClearAllPools + retrying delete now live in the shared helper.
    public Task DisposeAsync() => TestTeardown.ClearSqlitePoolsAndDeleteAsync(_tempRoot);

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
    public async Task Pending_items_survive_a_restart_leases_are_cleared_and_all_require_approval()
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
        Assert.True(rehydratedLeased.AwaitingResume);

        var rehydratedUnleased = pending2.Single(i => i.WorkUnitId == unleasedWorkUnitId);
        Assert.Null(rehydratedUnleased.LeasedBy);
        Assert.Equal(0, rehydratedUnleased.AttemptCount);
        // Never leased (still queued, not yet started) doesn't mean undisturbed — it's still
        // backlog from a run nobody asked to resume, so it's gated behind approval too.
        Assert.True(rehydratedUnleased.AwaitingResume);

        // Neither item is acquirable until a human approves it.
        Assert.Null(await scheduler2.TryAcquireAsync("agent-2"));

        await scheduler2.ApproveResumeAsync(unleasedWorkUnitId);
        var first = await scheduler2.TryAcquireAsync("agent-2");
        Assert.NotNull(first);
        Assert.Equal(unleasedWorkUnitId, first!.WorkUnitId);

        // The still-unapproved item remains blocked.
        Assert.Null(await scheduler2.TryAcquireAsync("agent-3"));

        await scheduler2.ApproveResumeAsync(leasedWorkUnitId);
        var third = await scheduler2.TryAcquireAsync("agent-3");
        Assert.NotNull(third);
        Assert.Equal(leasedWorkUnitId, third!.WorkUnitId);
    }

    [Fact]
    public async Task ListAwaitingResumeAsync_returns_every_item_that_survived_a_restart()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var scheduler1 = app1.Services.GetRequiredService<IWorkScheduler>();

        var unitA = await orchestrator1.CreateWorkUnitAsync("Task A", "test");
        var unitB = await orchestrator1.CreateWorkUnitAsync("Task B", "test");
        await scheduler1.EnqueueAsync(unitA.WorkUnitId, "worker");
        await scheduler1.EnqueueAsync(unitB.WorkUnitId, "worker");

        // Neither item is acquired before the restart — both are plain, never-leased backlog.
        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);
        var scheduler2 = app2.Services.GetRequiredService<IWorkScheduler>();

        var awaitingResume = await scheduler2.ListAwaitingResumeAsync();
        Assert.Equal(2, awaitingResume.Count);
        Assert.Contains(awaitingResume, i => i.WorkUnitId == unitA.WorkUnitId);
        Assert.Contains(awaitingResume, i => i.WorkUnitId == unitB.WorkUnitId);
    }

    [Fact]
    public async Task A_freshly_enqueued_goal_is_not_gated_by_an_older_session_awaiting_resume()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var scheduler1 = app1.Services.GetRequiredService<IWorkScheduler>();

        var staleUnit = await orchestrator1.CreateWorkUnitAsync("Stale goal from a prior session", "test");
        await scheduler1.EnqueueAsync(staleUnit.WorkUnitId, "worker");

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);
        var orchestrator2 = app2.Services.GetRequiredService<IOrchestratorService>();
        var scheduler2 = app2.Services.GetRequiredService<IWorkScheduler>();

        // The rehydrated backlog is gated — an agent polling right after restart gets nothing.
        Assert.Null(await scheduler2.TryAcquireAsync("agent-1"));

        // A brand-new goal added in this run (EnqueueAsync, not RehydrateAsync) is immediately
        // eligible and runs alongside the still-gated stale session, not blocked behind it.
        var freshUnit = await orchestrator2.CreateWorkUnitAsync("Fresh goal for this session", "test");
        await scheduler2.EnqueueAsync(freshUnit.WorkUnitId, "worker");

        var acquired = await scheduler2.TryAcquireAsync("agent-1");
        Assert.NotNull(acquired);
        Assert.Equal(freshUnit.WorkUnitId, acquired!.WorkUnitId);
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
