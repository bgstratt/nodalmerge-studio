using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Proves the scheduler-release convergence chain end-to-end (plans/orchestrator-pure-service.md
/// M2): GoalCoordinator enqueues the planner at spawn; the planner's release fans out the plan
/// through the real scheduler queue; the worker's release triggers
/// WorkSchedulerService.ReleaseAsync → ReinvokeOrchestratorAsync → GoalCoordinator.ConvergeAsync
/// — and the sweeps are idempotent: no duplicate planner enqueues, tasks, or proposals appear no
/// matter how many releases converge the same goal.
/// </summary>
[Trait("Category", "Integration")]
public class SchedulerReinvocationTests
{
    [Fact]
    public async Task SchedulerDrivenRun_ConvergesWithoutDuplication()
    {
        var fakeHandler = new ScheduledReinvocationLlmHandler();

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var tasks           = app.Services.GetRequiredService<ITaskService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var decisionLog     = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        // Drives WorkSchedulerService's poll loop (PollSchedulerAsync) so an enqueued worker
        // actually gets picked up — FullAgentCycleTests never needs this since it only exercises
        // the legacy direct-spawn path, which never goes through the scheduler.
        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var wu = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Build a hello world feature",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: wu.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

            // Poll until the worker raises a ReadyForReview proposal (scheduler polls every 2s).
            MergeProposal? proposal = null;
            var proposalDeadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < proposalDeadline)
            {
                var all = await merge.ListAsync();
                proposal = all.FirstOrDefault(p => p.Status == MergeProposalStatus.ReadyForReview);
                if (proposal is not null) break;
                await Task.Delay(100);
            }
            Assert.NotNull(proposal);

            // The whole run was coordinated by the deterministic GoalCoordinator — every decision
            // in the log carries its stable author id, and exactly one SpawnPlanner decision
            // exists even though the planner's and the worker's releases each ran a full
            // convergence sweep afterward (the ensurePlanner guard makes automatic sweeps unable
            // to re-enqueue planners).
            var decisions = await decisionLog.GetEventsAsync(wu.WorkUnitId);
            Assert.NotEmpty(decisions);
            // Deterministic authors only — the coordinator plus fan-out's own Enqueue records;
            // no LLM orchestrator agent ids appear anywhere.
            Assert.All(decisions, d =>
                Assert.Contains(d.OrchestratorAgentId, new[] { "goal-coordinator", "fanout" }));
            Assert.Single(decisions, d =>
                d.Action == OrchestrationAction.SpawnPlanner && d.OrchestratorAgentId == "goal-coordinator");

            // Convergence without duplication: one fan-out child, one task on it, one proposal.
            var children = await workUnits.GetChildrenAsync(wu.WorkUnitId);
            var child = Assert.Single(children);

            var allTasks = await tasks.ListAsync(child.WorkUnitId);
            Assert.Single(allTasks);
            Assert.Contains(allTasks, t => t.Status == StudioTaskStatus.Completed);

            var allProposals = await merge.ListAsync();
            Assert.Single(allProposals);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
