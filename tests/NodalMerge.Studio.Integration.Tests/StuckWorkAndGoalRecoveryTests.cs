using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers three related fixes for a live-observed "goal looks stuck forever" scenario:
/// a cancelled sibling never released its file leases, a scheduler item's AwaitingFileLease flag
/// can end up orphaned (set with nothing in IFileLeaseService actually blocking it anymore), and
/// goal-level Pause/Resume 404'd for any goal whose GoalNode was never created (e.g. spawned via
/// direct work-unit creation rather than the /studio/goals create endpoint).
/// </summary>
[Trait("Category", "Integration")]
public class StuckWorkAndGoalRecoveryTests
{
    [Fact]
    public async Task Cancelling_a_work_unit_releases_its_file_leases_and_unblocks_the_waiter()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var fileLease = app.Services.GetRequiredService<IFileLeaseService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        var parent = await orchestrator.CreateWorkUnitAsync("Shared.cs work", "test");
        var holder = await orchestrator.CreateWorkUnitAsync(
            "Fix the bug", "test", parentWorkUnitId: parent.WorkUnitId);
        var waiter = await orchestrator.CreateWorkUnitAsync(
            "Also touch Shared.cs", "test", parentWorkUnitId: parent.WorkUnitId);

        var (holderGranted, _) = await fileLease.TryAcquireOrEnqueueAsync(holder.WorkUnitId, "Shared.cs");
        Assert.True(holderGranted);
        var (waiterGranted, waiterHolderId) = await fileLease.TryAcquireOrEnqueueAsync(waiter.WorkUnitId, "Shared.cs");
        Assert.False(waiterGranted);
        Assert.Equal(holder.WorkUnitId, waiterHolderId);

        await scheduler.EnqueueAsync(waiter.WorkUnitId, "worker");
        await scheduler.MarkAwaitingFileLeaseAsync(waiter.WorkUnitId);

        // The holder never merges — it just gets cancelled (e.g. superseded by a re-plan).
        await workUnitCommands.CancelAsync(holder.WorkUnitId);

        var leases = await fileLease.ListAsync();
        Assert.DoesNotContain(leases, l => l.HolderWorkUnitId == holder.WorkUnitId);

        var pending = await scheduler.ListPendingAsync();
        Assert.False(pending.Single(i => i.WorkUnitId == waiter.WorkUnitId).AwaitingFileLease);
    }

    [Fact]
    public async Task ForceResumeAsync_clears_an_orphaned_AwaitingFileLease_flag_even_with_no_matching_wait_queue_entry()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        await scheduler.EnqueueAsync(unit.WorkUnitId, "worker");
        await scheduler.MarkAwaitingFileLeaseAsync(unit.WorkUnitId);

        // Nothing in IFileLeaseService is actually tracking this item as a waiter (it never called
        // TryAcquireOrEnqueueAsync in this test) — simulating the orphaned-flag scenario where the
        // flag and the lease's own state have drifted out of sync. The normal
        // ClearAwaitingFileLeaseAsync path (driven by the lease release-and-advance hook) would
        // never fire for it. ForceResumeAsync must clear it anyway.
        Assert.Null(await scheduler.TryAcquireAsync("agent-1"));

        await scheduler.ForceResumeAsync(unit.WorkUnitId);

        var resumed = await scheduler.TryAcquireAsync("agent-1");
        Assert.NotNull(resumed);
        Assert.Equal(unit.WorkUnitId, resumed!.WorkUnitId);
    }

    [Fact]
    public async Task PauseAsync_synthesizes_a_GoalNode_instead_of_404ing_when_none_was_ever_recorded()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var goalControl = app.Services.GetRequiredService<IGoalControlService>();
        var goalNodes = app.Services.GetRequiredService<IGoalNodeService>();

        // Created via IOrchestratorService directly (mirrors a harness posting straight to
        // /studio/workunits + /studio/agents/spawn) — never goes through goalNodes.RecordAsync.
        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        Assert.Null(await goalNodes.GetAsync(unit.WorkUnitId));

        var paused = await goalControl.PauseAsync(unit.WorkUnitId, reason: "testing");

        Assert.Equal(GoalStatus.Paused, paused.Status);
        Assert.NotNull(await goalNodes.GetAsync(unit.WorkUnitId));
    }
}
