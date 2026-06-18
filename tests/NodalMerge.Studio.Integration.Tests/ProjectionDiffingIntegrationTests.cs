using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 5 slice 12b — Projection Diffing / stall detection. An orchestrator that keeps calling
/// tools without ever changing the artifact chain gets dead-lettered with a "Stall:" reason well
/// before MaxIterationsExceeded (25 iterations) would fire.
/// </summary>
[Trait("Category", "Integration")]
public class ProjectionDiffingIntegrationTests
{
    [Fact]
    public async Task Orchestrator_with_no_artifact_change_is_stall_dead_lettered()
    {
        // ExhaustingLlmHandler always calls the read-only nm_v1_workunit_get tool and never
        // produces or changes an artifact — same fake used for the MaxIterationsExceeded path in
        // DeadLetterIntegrationTests, but here it should trip the much earlier stall threshold.
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ExhaustingLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var wu = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Goal that never produces an artifact",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: wu.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

            DeadLetterEntry? entry = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                entry = await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId);
                if (entry is not null) break;
                await Task.Delay(100);
            }

            Assert.NotNull(entry);
            Assert.StartsWith("Stall:", entry!.Reason, StringComparison.Ordinal);
            Assert.True(entry.AttemptCount >= 1);

            var unit = await workUnits.GetAsync(wu.WorkUnitId);
            Assert.NotNull(unit);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Orchestration_that_keeps_producing_artifacts_is_not_stall_dead_lettered()
    {
        // ScheduledReinvocationLlmHandler drives a real plan -> enqueue -> worker -> propose flow
        // (same fake as SchedulerReinvocationTests) — every cycle that matters changes the
        // artifact chain, so stall detection must never fire across the whole run.
        var fakeHandler = new ScheduledReinvocationLlmHandler();

        var app = StudioWebApplication.Build(
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
