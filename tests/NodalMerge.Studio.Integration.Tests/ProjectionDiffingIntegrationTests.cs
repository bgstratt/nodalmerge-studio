using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 5 slice 12b originally covered the orchestrator LLM loop's Projection-Diffing stall
/// detector; plans/orchestrator-pure-service.md M2 deleted that loop (the deterministic
/// GoalCoordinator cannot stall), so the "Stall:" dead-letter test went with it. What remains are
/// the two regressions that must stay true under the coordinator: a healthy scripted goal run
/// never dead-letters, and goal start always records a SpawnPlanner decision.
/// </summary>
[Trait("Category", "Integration")]
public class ProjectionDiffingIntegrationTests
{
    [Fact]
    public async Task Orchestration_that_keeps_producing_artifacts_is_not_stall_dead_lettered()
    {
        // ScheduledReinvocationLlmHandler drives a real plan -> enqueue -> worker -> propose flow
        // (same fake as SchedulerReinvocationTests) — every cycle that matters changes the
        // artifact chain, so stall detection must never fire across the whole run.
        var fakeHandler = new ScheduledReinvocationLlmHandler();

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();

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

            MergeProposal? proposal = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var all = await merge.ListAsync();
                proposal = all.FirstOrDefault(p => p.Status == MergeProposalStatus.ReadyForReview);
                if (proposal is not null) break;
                await Task.Delay(100);
            }
            Assert.NotNull(proposal);

            var entry = await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId);
            Assert.Null(entry);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    // 1 read-only call (workunit_get) before enqueueing the planner — the minimal documented
    // flow. Tripped the original 2-cycle stall threshold before either fix.
    [InlineData(1)]
    // 2 read-only calls (workunit_get, then projection_get — both offered by the system prompt
    // as valid ways to understand state) before enqueueing. Still tripped the stall detector
    // even after the "routing decision counts as progress" fix, because the budget was already
    // spent on two consecutive read-only cycles before any routing decision happened — this is
    // the exact path the user hit live after that first fix shipped.
    [InlineData(2)]
    public async Task Orchestrator_enqueueing_planner_after_readonly_calls_is_not_stall_dead_lettered(
        int readOnlyCalls)
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new PlannerEnqueueOnlyLlmHandler(readOnlyCalls)),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();
        var decisionLog     = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var wu = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Goal that should route through the planner",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: wu.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

            OrchestrationEvent? spawnPlanner = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var events = await decisionLog.GetEventsAsync(wu.WorkUnitId);
                spawnPlanner = events.FirstOrDefault(e => e.Action == OrchestrationAction.SpawnPlanner);
                if (spawnPlanner is not null) break;
                await Task.Delay(100);
            }

            Assert.NotNull(spawnPlanner);

            var entry = await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId);
            Assert.Null(entry);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
