using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class FullAgentCycleTests
{
    /// <summary>
    /// Exercises the full pipeline end-to-end using a scripted fake LLM
    /// (plans/orchestrator-pure-service.md M2 — no orchestrator LLM turn exists anymore):
    ///   1. Seed a work unit; "spawn the orchestrator" = register the goal's Default-profile
    ///      credentials and let GoalCoordinator enqueue the planner.
    ///   2. Scripted planner records a single-slice plan; fan-out creates the child work unit,
    ///      its task, and enqueues the scripted worker through the real scheduler.
    ///   3. Worker marks the task Completed and raises a merge proposal.
    ///   4. Test acts as the human reviewer: Approve + Apply the merge.
    ///   5. Assert DAG nodes written, artifact lineage on both parent and child, and the
    ///      coordinator's decision log.
    /// </summary>
    [Fact]
    public async Task FullAgentCycle_ProducesAndApprovesMergeProposal()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        // AutonomousReviewLlmHandler carries the planner/worker/reviewer scripts; the reviewer
        // branch is never reached here (both policies default to HumanRequired — this test IS the
        // human reviewer).
        var fakeHandler = new AutonomousReviewLlmHandler("Approved", "unused");

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            // Override DAG-backed storage with fully in-memory implementations so the
            // test doesn't require the NodalMerge runtime to be running.
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var tasks           = app.Services.GetRequiredService<ITaskService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var nodeStore       = app.Services.GetRequiredService<IStudioNodeStore>();
        var artifacts       = app.Services.GetRequiredService<IArtifactLineageService>();
        var decisionLog     = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        // The planner and worker both run through the real scheduler queue now — the poll loop
        // that picks enqueued items up must be running.
        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            // Seed: create a work unit (which auto-creates a branch).
            var wu = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Build a hello world feature",
                owner: "integration-test");

            // ── Act ───────────────────────────────────────────────────────────
            // Registers the goal's Default-profile credentials and starts the goal: the
            // GoalCoordinator enqueues the scripted planner, whose plan fans out the worker.
            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: wu.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

            // Poll until the worker raises a ReadyForReview merge proposal.
            MergeProposal? proposal = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var all = await merge.ListAsync();
                proposal = all.FirstOrDefault(p => p.Status == MergeProposalStatus.ReadyForReview);
                if (proposal is not null) break;
                await Task.Delay(50);
            }

            // ── Approve the child's proposal (human, TaskReviewPolicy HumanRequired) ──
            Assert.NotNull(proposal);
            await merge.ReviewAsync(proposal.ProposalId, MergeProposalStatus.Approved);

            // Approving the child triggers merge reconciliation on the parent: the child proposal
            // is consumed (Superseded) and a reconciled workspace proposal appears on the parent
            // work unit targeting main — that's the one the human applies.
            MergeProposal? reconciled = null;
            var reconcileDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < reconcileDeadline)
            {
                var all = await merge.ListAsync();
                reconciled = all.FirstOrDefault(p =>
                    p.WorkUnitId == wu.WorkUnitId && p.Status == MergeProposalStatus.ReadyForReview);
                if (reconciled is not null) break;
                await Task.Delay(50);
            }
            Assert.NotNull(reconciled);

            var approved = await merge.ReviewAsync(reconciled!.ProposalId, MergeProposalStatus.Approved);
            var merged   = await merge.ApplyAsync(approved.ProposalId);

            // ── Assert: merge lifecycle ───────────────────────────────────────
            Assert.Equal(MergeProposalStatus.Merged, merged.Status);

            // ── Assert: fan-out topology + task driven to Completed ──────────
            var children = await workUnits.GetChildrenAsync(wu.WorkUnitId);
            var child = Assert.Single(children);
            Assert.Equal(child.WorkUnitId, proposal.WorkUnitId);

            var allTasks = await tasks.ListAsync(child.WorkUnitId);
            Assert.NotEmpty(allTasks);
            Assert.Contains(allTasks, t => t.Status == StudioTaskStatus.Completed);

            // ── Assert: DAG nodes written for WorkUnit, Task, MergeProposal ───
            var wuNode = await nodeStore.ReadNodeAsync(StudioNodeKind.WorkUnitV1, wu.WorkUnitId);
            Assert.NotNull(wuNode);

            var completedTask = allTasks.First(t => t.Status == StudioTaskStatus.Completed);
            var taskNode = await nodeStore.ReadNodeAsync(StudioNodeKind.TaskV1, completedTask.TaskId);
            Assert.NotNull(taskNode);

            var proposalNode = await nodeStore.ReadNodeAsync(StudioNodeKind.MergeProposalV1, merged.ProposalId);
            Assert.NotNull(proposalNode);

            // ── Assert: artifact lineage ──────────────────────────────────────
            // Parent chain: Goal (root) + the planner's Plan artifact.
            var parentChain = await artifacts.GetChainAsync(wu.WorkUnitId);
            var goalArtifact = parentChain.Single(a => a.Type == ArtifactType.Goal);
            Assert.Equal(wu.WorkUnitId, goalArtifact.ArtifactId);
            Assert.Null(goalArtifact.ParentArtifactId);
            Assert.Contains(parentChain, a => a.Type == ArtifactType.Plan);

            // Child chain: the worker's Research note and its (now superseded-by-reconciliation)
            // MergeProposal artifact.
            var childChain = await artifacts.GetChainAsync(child.WorkUnitId);
            var researchArtifact = childChain.Single(a => a.Type == ArtifactType.Research);
            Assert.Equal("Stack", researchArtifact.Title);
            Assert.Equal("Codebase uses .NET 8; no Redis present.", researchArtifact.Body);
            Assert.Contains(childChain, a => a.Type == ArtifactType.MergeProposal);

            // ── Assert: orchestration decision log ────────────────────────────
            // The deterministic coordinator records the planner enqueue under its stable id.
            var orchestrationEvents = await decisionLog.GetEventsAsync(wu.WorkUnitId);
            Assert.Contains(orchestrationEvents, e =>
                e.Action == OrchestrationAction.SpawnPlanner && e.OrchestratorAgentId == "goal-coordinator");
            Assert.All(orchestrationEvents, e => Assert.False(string.IsNullOrEmpty(e.InputProjectionSnapshot)));
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
