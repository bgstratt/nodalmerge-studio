using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Proves the orchestrator re-invocation fix (Phase 4 prerequisite, see
/// plans/phase-3-foundations.md's Deferred Work Tracker): unlike FullAgentCycleTests, the
/// orchestrator here drives the worker through the real scheduler queue
/// (nm_v1_scheduler_enqueue), not the legacy direct nm_v1_agent_spawn — so this is the only
/// test exercising WorkSchedulerService.ReleaseAsync's new ReinvokeOrchestratorAsync call.
/// </summary>
[Trait("Category", "Integration")]
public class SchedulerReinvocationTests
{
    [Fact]
    public async Task SchedulerDrivenRun_ReinvokesOrchestrator_AndConvergesWithoutDuplication()
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

            // Poll for re-invocation: a second, distinct orchestrator agent making a decision for
            // the same work unit proves ReleaseAsync's ReinvokeOrchestratorAsync call fired and a
            // fresh OrchestratorAgentLoop actually ran (not just that the method was called).
            IReadOnlyList<OrchestrationEvent> decisions = [];
            var reinvokeDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < reinvokeDeadline)
            {
                decisions = await decisionLog.GetEventsAsync(wu.WorkUnitId);
                if (decisions.Select(d => d.OrchestratorAgentId).Distinct().Count() >= 2) break;
                await Task.Delay(100);
            }

            Assert.True(
                decisions.Select(d => d.OrchestratorAgentId).Distinct().Count() >= 2,
                "Expected a second orchestrator agent to have recorded a decision after re-invocation.");

            // Convergence: the re-invoked orchestrator recognized the work was already done and
            // did not re-enqueue — exactly one Enqueue decision, one task, one proposal total.
            Assert.Single(decisions, d => d.Action == OrchestrationAction.Enqueue);

            var allTasks = await tasks.ListAsync(wu.WorkUnitId);
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
