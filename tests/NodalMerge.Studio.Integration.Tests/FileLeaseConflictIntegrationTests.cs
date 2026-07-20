using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// End-to-end proof of Phase 12's merge-gated file lease: two independent work units' Workers
/// both target the same file path. The second ("waiter") collides with the first ("holder")'s
/// lease, is queued and parked with zero extra LLM turns (the headline "don't burn context on a
/// blocked worker" requirement), then is automatically resumed — with the holder's just-merged
/// content already copied into its branch — once the holder's proposal is actually applied.
/// </summary>
[Trait("Category", "Integration")]
public class FileLeaseConflictIntegrationTests
{
    [Fact]
    public async Task ConflictingWaiter_IsQueuedWithNoExtraLlmTurn_ThenResumesAfterHolderMerges()
    {
        const string path = "src/Shared.cs";
        var fakeHandler = new FileLeaseConflictLlmHandler { Path = path };

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler        = app.Services.GetRequiredService<IWorkScheduler>();
        var tasks             = app.Services.GetRequiredService<ITaskService>();
        var merge             = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace     = app.Services.GetRequiredService<IFileWorkspaceService>();
        var agentRuntime      = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();

        // Drives WorkSchedulerService's poll loop so enqueued workers actually get picked up —
        // same pattern SchedulerReinvocationTests uses, since this scenario only matters on the
        // scheduler-driven path (FanOutService's), not the legacy direct nm_v1_agent_spawn one.
        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            // File leases are scoped per root goal — an unrelated top-level goal must never
            // lease-block this one, so holder/waiter need a shared parent to actually contend
            // (the scenario this test exists to exercise: two siblings fanned out from one goal).
            var parentGoal = await orchestratorSvc.CreateWorkUnitAsync("Shared.cs work", "test");
            var holder = await orchestratorSvc.CreateWorkUnitAsync(
                "Introduce Shared.cs", "test", parentWorkUnitId: parentGoal.WorkUnitId);
            var waiter = await orchestratorSvc.CreateWorkUnitAsync(
                "Also touch Shared.cs", "test", parentWorkUnitId: parentGoal.WorkUnitId);

            var holderTask = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), holder.WorkUnitId, holder.Goal, "Execute", StudioTaskStatus.Open, null, 0));
            var waiterTask = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), waiter.WorkUnitId, waiter.Goal, "Execute", StudioTaskStatus.Open, null, 0));

            fakeHandler.HolderWorkUnitId = holder.WorkUnitId;
            fakeHandler.HolderBranchId = holder.BranchId;
            fakeHandler.WaiterWorkUnitId = waiter.WorkUnitId;
            fakeHandler.WaiterBranchId = waiter.BranchId;

            // Enqueue the holder alone first and wait for its write to land — that's exactly when
            // it acquires the lease — before the waiter ever gets a chance to collide with it.
            await scheduler.EnqueueAsync(
                holder.WorkUnitId, "worker", holderTask.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic");

            var writeDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < writeDeadline
                   && await fileWorkspace.ReadAsync(holder.BranchId, path) is null)
                await Task.Delay(50);
            Assert.NotNull(await fileWorkspace.ReadAsync(holder.BranchId, path));

            // Now enqueue the waiter — its write must collide with the still-unmerged holder lease.
            await scheduler.EnqueueAsync(
                waiter.WorkUnitId, "worker", waiterTask.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic");

            // Wait for the waiter to be parked (AwaitingFileLease), not removed or dead-lettered.
            var parkDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            var parked = false;
            while (DateTimeOffset.UtcNow < parkDeadline)
            {
                var pending = await scheduler.ListPendingAsync();
                if (pending.Any(i => i.WorkUnitId == waiter.WorkUnitId && i.AwaitingFileLease))
                {
                    parked = true;
                    break;
                }
                await Task.Delay(50);
            }
            Assert.True(parked, "Expected the waiter to be parked awaiting the file lease.");

            // The headline assertion: the waiter's conflicted run made exactly the calls needed to
            // discover the conflict (get, read, write→conflict) and stopped — no further LLM turn
            // was sent asking it to "please wait." Burning a model call on a wait that resolves
            // entirely server-side is exactly what this mechanism exists to avoid.
            var waiterCalls = fakeHandler.CallsFor(waiter.WorkUnitId);
            Assert.Single(waiterCalls);
            Assert.Equal([0, 1, 2], waiterCalls[0]);

            // Drive the holder's proposal to Merged — this must release the lease, copy the merged
            // file into the waiter's branch, and clear the waiter's parked flag so it resumes.
            MergeProposal? proposal = null;
            var proposalDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < proposalDeadline)
            {
                var all = await merge.ListAsync();
                proposal = all.FirstOrDefault(p => p.WorkUnitId == holder.WorkUnitId
                    && p.Status == MergeProposalStatus.ReadyForReview);
                if (proposal is not null) break;
                await Task.Delay(50);
            }
            Assert.NotNull(proposal);

            var approved = await merge.ReviewAsync(proposal!.ProposalId, MergeProposalStatus.Approved);
            var merged = await merge.ApplyAsync(approved.ProposalId);
            Assert.Equal(MergeProposalStatus.Merged, merged.Status);

            // The waiter must resume on its own and complete its own write — final content is
            // "from-waiter", proving the lease was actually freed and the second write succeeded
            // (not just that the holder's content alone ended up there).
            var resumeDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            string? finalContent = null;
            while (DateTimeOffset.UtcNow < resumeDeadline)
            {
                finalContent = await fileWorkspace.ReadAsync(waiter.BranchId, path);
                if (finalContent == "from-waiter") break;
                await Task.Delay(50);
            }
            Assert.Equal("from-waiter", finalContent);

            // The waiter ran a second, distinct conversation to get there (not a continuation of
            // the conflicted one) — proving this went through the real resume pathway.
            var waiterCallsAfterResume = fakeHandler.CallsFor(waiter.WorkUnitId);
            Assert.Equal(2, waiterCallsAfterResume.Count);
            Assert.Contains(2, waiterCallsAfterResume[1]); // the successful write step ran.
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Phase 14 — same collision as the first test, but with an explicit sessionId on both
    /// EnqueueAsync calls so the waiter's collision actually gets attributed to a session: proves
    /// McpToolDispatcher.CheckFileLeaseAsync records a FileLeaseContended event on conflict, and
    /// that WorkspaceUsageMetricsService surfaces it as a lease-contention hot spot.
    /// </summary>
    [Fact]
    public async Task ConflictingWaiter_RecordsFileLeaseContendedEvent()
    {
        const string path = "src/Shared.cs";
        const string sessionId = "SES-lease-contention-1";
        var fakeHandler = new FileLeaseConflictLlmHandler { Path = path };

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler        = app.Services.GetRequiredService<IWorkScheduler>();
        var tasks             = app.Services.GetRequiredService<ITaskService>();
        var fileWorkspace     = app.Services.GetRequiredService<IFileWorkspaceService>();
        var events            = app.Services.GetRequiredService<IExecutionEventStream>();
        var usageMetrics      = app.Services.GetRequiredService<IWorkspaceUsageMetricsService>();
        var agentRuntime      = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            // File leases are scoped per root goal — an unrelated top-level goal must never
            // lease-block this one, so holder/waiter need a shared parent to actually contend
            // (the scenario this test exists to exercise: two siblings fanned out from one goal).
            var parentGoal = await orchestratorSvc.CreateWorkUnitAsync("Shared.cs work", "test");
            var holder = await orchestratorSvc.CreateWorkUnitAsync(
                "Introduce Shared.cs", "test", parentWorkUnitId: parentGoal.WorkUnitId);
            var waiter = await orchestratorSvc.CreateWorkUnitAsync(
                "Also touch Shared.cs", "test", parentWorkUnitId: parentGoal.WorkUnitId);

            var holderTask = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), holder.WorkUnitId, holder.Goal, "Execute", StudioTaskStatus.Open, null, 0));
            var waiterTask = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), waiter.WorkUnitId, waiter.Goal, "Execute", StudioTaskStatus.Open, null, 0));

            fakeHandler.HolderWorkUnitId = holder.WorkUnitId;
            fakeHandler.HolderBranchId = holder.BranchId;
            fakeHandler.WaiterWorkUnitId = waiter.WorkUnitId;
            fakeHandler.WaiterBranchId = waiter.BranchId;

            await scheduler.EnqueueAsync(
                holder.WorkUnitId, "worker", holderTask.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic",
                sessionId: sessionId);

            var writeDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < writeDeadline
                   && await fileWorkspace.ReadAsync(holder.BranchId, path) is null)
                await Task.Delay(50);
            Assert.NotNull(await fileWorkspace.ReadAsync(holder.BranchId, path));

            await scheduler.EnqueueAsync(
                waiter.WorkUnitId, "worker", waiterTask.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic",
                sessionId: sessionId);

            IReadOnlyList<ExecutionEvent> contendedEvents = [];
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                contendedEvents = await events.GetEventsByKindAsync([ExecutionEventKind.FileLeaseContended]);
                if (contendedEvents.Count > 0) break;
                await Task.Delay(50);
            }

            Assert.Single(contendedEvents);
            Assert.Equal(sessionId, contendedEvents[0].SessionId);
            Assert.Equal(waiter.WorkUnitId, contendedEvents[0].WorkUnitId);

            var hotSpots = await usageMetrics.GetLeaseContentionHotSpotsAsync();
            var hotSpot = Assert.Single(hotSpots, h => h.Path == path);
            Assert.Equal(1, hotSpot.ContentionCount);
            Assert.Contains(waiter.WorkUnitId, hotSpot.ContendingWorkUnitIds);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Same collision as above, but instead of the holder's proposal merging, a human rejects it
    /// outright — the realistic "this isn't right, abandon it" case, and the one a human actually
    /// hits often (unlike racing to IAgentControlService.StopAsync a worker mid-network-call,
    /// which an instant fake LLM handler can't reliably exercise: the holder's whole scripted run
    /// — write, propose, validate, end_turn — completes before a test could ever intervene).
    /// Proves InMemoryMergeService.ReviewAsync's Rejected branch releases the lease AND clears the
    /// promoted waiter's scheduler-level AwaitingFileLease flag — the fix for a bug
    /// ForceReleaseAllForWorkUnitAsync's other caller (InMemoryDeadLetterService) also had:
    /// advancing the lease's internal holder without telling the scheduler left a promoted waiter
    /// parked forever despite already holding the lease it was waiting on.
    /// </summary>
    [Fact]
    public async Task ConflictingWaiter_ResumesAfterHolderProposalIsRejected()
    {
        const string path = "src/Shared.cs";
        var fakeHandler = new FileLeaseConflictLlmHandler { Path = path };

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler        = app.Services.GetRequiredService<IWorkScheduler>();
        var tasks             = app.Services.GetRequiredService<ITaskService>();
        var merge             = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace     = app.Services.GetRequiredService<IFileWorkspaceService>();
        var agentRuntime      = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            // File leases are scoped per root goal — an unrelated top-level goal must never
            // lease-block this one, so holder/waiter need a shared parent to actually contend
            // (the scenario this test exists to exercise: two siblings fanned out from one goal).
            var parentGoal = await orchestratorSvc.CreateWorkUnitAsync("Shared.cs work", "test");
            var holder = await orchestratorSvc.CreateWorkUnitAsync(
                "Introduce Shared.cs", "test", parentWorkUnitId: parentGoal.WorkUnitId);
            var waiter = await orchestratorSvc.CreateWorkUnitAsync(
                "Also touch Shared.cs", "test", parentWorkUnitId: parentGoal.WorkUnitId);

            var holderTask = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), holder.WorkUnitId, holder.Goal, "Execute", StudioTaskStatus.Open, null, 0));
            var waiterTask = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), waiter.WorkUnitId, waiter.Goal, "Execute", StudioTaskStatus.Open, null, 0));

            fakeHandler.HolderWorkUnitId = holder.WorkUnitId;
            fakeHandler.HolderBranchId = holder.BranchId;
            fakeHandler.WaiterWorkUnitId = waiter.WorkUnitId;
            fakeHandler.WaiterBranchId = waiter.BranchId;

            await scheduler.EnqueueAsync(
                holder.WorkUnitId, "worker", holderTask.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic");

            var writeDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < writeDeadline
                   && await fileWorkspace.ReadAsync(holder.BranchId, path) is null)
                await Task.Delay(50);
            Assert.NotNull(await fileWorkspace.ReadAsync(holder.BranchId, path));

            await scheduler.EnqueueAsync(
                waiter.WorkUnitId, "worker", waiterTask.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic");

            var parkDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            var parked = false;
            while (DateTimeOffset.UtcNow < parkDeadline)
            {
                var pending = await scheduler.ListPendingAsync();
                if (pending.Any(i => i.WorkUnitId == waiter.WorkUnitId && i.AwaitingFileLease))
                {
                    parked = true;
                    break;
                }
                await Task.Delay(50);
            }
            Assert.True(parked, "Expected the waiter to be parked awaiting the file lease.");

            // Reject the holder's proposal instead of approving it — nothing will ever merge it.
            MergeProposal? proposal = null;
            var proposalDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < proposalDeadline)
            {
                var all = await merge.ListAsync();
                proposal = all.FirstOrDefault(p => p.WorkUnitId == holder.WorkUnitId
                    && p.Status == MergeProposalStatus.ReadyForReview);
                if (proposal is not null) break;
                await Task.Delay(50);
            }
            Assert.NotNull(proposal);

            var rejected = await merge.ReviewAsync(proposal!.ProposalId, MergeProposalStatus.Rejected);
            Assert.Equal(MergeProposalStatus.Rejected, rejected.Status);

            // The waiter must be released from its parked state — without anything ever merging —
            // and go on to successfully complete its own write.
            var resumeDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            string? finalContent = null;
            while (DateTimeOffset.UtcNow < resumeDeadline)
            {
                finalContent = await fileWorkspace.ReadAsync(waiter.BranchId, path);
                if (finalContent == "from-waiter") break;
                await Task.Delay(50);
            }
            Assert.Equal("from-waiter", finalContent);

            var stillParked = (await scheduler.ListPendingAsync())
                .Any(i => i.WorkUnitId == waiter.WorkUnitId && i.AwaitingFileLease);
            Assert.False(stillParked, "Waiter must no longer be parked once it has resumed and completed.");
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
