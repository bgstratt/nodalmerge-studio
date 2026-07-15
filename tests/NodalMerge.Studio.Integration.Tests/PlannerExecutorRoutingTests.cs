using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/phase-d-implementation.md D2 — "who plans this goal" executor routing. Drives the real
/// OrchestratorAgentLoop path (PlannerEnqueueOnlyLlmHandler scripts the orchestrator's own
/// nm_v1_scheduler_enqueue(profileId="planner") call) and inspects the resulting queued
/// ScheduledItem — never started via IAgentRuntimeService.StartAsync, so the background scheduler
/// poll loop never runs and the item can't race a dequeue before assertions run.
/// </summary>
[Trait("Category", "Integration")]
public class PlannerExecutorRoutingTests
{
    [Fact]
    public async Task Deterministic_FileScope_tier_selects_a_planner_profile_by_pattern()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new PlannerEnqueueOnlyLlmHandler(readOnlyCalls: 0)),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { UsePlannerExecutorSelection = true });
            });

        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        await profiles.CreateAsync(new AgentProfile(
            "plan-api",
            "API Planner",
            PipelineStage.Plan,
            "Plans API-scoped goals.",
            [],
            15,
            FileScopePatterns: ["src/api/**"],
            Executor: "claude-code"));

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        var parent = await orchestratorSvc.CreateWorkUnitAsync(
            "Build the API", "test", fileScope: ["src/api/foo.py"]);

        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId,
            model: "fake", baseUrl: "http://fake", apiKey: "fake");

        await WaitForSpawnPlannerEventAsync(decisionLog, parent.WorkUnitId);

        var pending = await scheduler.ListPendingAsync();
        var item = Assert.Single(pending, i => i.WorkUnitId == parent.WorkUnitId);
        Assert.Equal("plan-api", item.ProfileId);
        Assert.Equal("claude-cli", item.Provider);
    }

    [Fact]
    public async Task Explicit_topology_assignment_bypasses_the_selector()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new PlannerEnqueueOnlyLlmHandler(readOnlyCalls: 0)),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                // Selection is enabled, and a deterministic-tier match exists for the goal's
                // fileScope — if the topology override weren't bypassing the selector entirely,
                // this profile would win and the assertions below would fail.
                services.AddSingleton(new WorkspaceOptions { UsePlannerExecutorSelection = true });
            });

        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        await profiles.CreateAsync(new AgentProfile(
            "plan-api",
            "API Planner",
            PipelineStage.Plan,
            "Plans API-scoped goals.",
            [],
            15,
            FileScopePatterns: ["src/api/**"],
            Executor: "claude-code"));

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        var parent = await orchestratorSvc.CreateWorkUnitAsync(
            "Build the API", "test", fileScope: ["src/api/foo.py"]);

        var stageCredentials = new Dictionary<PipelineStage, GoalDefaultCredentials>
        {
            [PipelineStage.Plan] = new GoalDefaultCredentials(
                "override-provider", "override-model", "http://override", "override-key", null),
        };

        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId,
            model: "fake", baseUrl: "http://fake", apiKey: "fake",
            stageCredentials: stageCredentials);

        await WaitForSpawnPlannerEventAsync(decisionLog, parent.WorkUnitId);

        var pending = await scheduler.ListPendingAsync();
        var item = Assert.Single(pending, i => i.WorkUnitId == parent.WorkUnitId);
        // profileId is left as the literal "planner" the orchestrator requested — the topology
        // override changes which credentials/provider a Plan-stage run gets, not the profileId —
        // and the override's provider/model win, not the deterministic tier's "plan-api"/"claude-cli".
        Assert.Equal("planner", item.ProfileId);
        Assert.Equal("override-provider", item.Provider);
        Assert.Equal("override-model", item.Model);
    }

    [Fact]
    public async Task Default_off_means_zero_behavior_change_for_the_planner_enqueue()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new PlannerEnqueueOnlyLlmHandler(readOnlyCalls: 0)),
            configureServices: services => services.AddInMemoryStorage());

        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        // A deterministic-tier match exists for the goal's fileScope — proves the selector is
        // never even consulted when the flag is off (default), not just that it happened not to
        // find anything.
        await profiles.CreateAsync(new AgentProfile(
            "plan-api",
            "API Planner",
            PipelineStage.Plan,
            "Plans API-scoped goals.",
            [],
            15,
            FileScopePatterns: ["src/api/**"],
            Executor: "claude-code"));

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        var parent = await orchestratorSvc.CreateWorkUnitAsync(
            "Build the API", "test", fileScope: ["src/api/foo.py"]);

        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId,
            model: "fake", baseUrl: "http://fake", apiKey: "fake", provider: "anthropic");

        await WaitForSpawnPlannerEventAsync(decisionLog, parent.WorkUnitId);

        var pending = await scheduler.ListPendingAsync();
        var item = Assert.Single(pending, i => i.WorkUnitId == parent.WorkUnitId);
        Assert.Equal("planner", item.ProfileId);
        Assert.Equal("anthropic", item.Provider);
        Assert.Equal("fake", item.Model);
    }

    private static async Task<OrchestrationEvent> WaitForSpawnPlannerEventAsync(
        IOrchestrationDecisionLogService decisionLog, string workUnitId)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var events = await decisionLog.GetEventsAsync(workUnitId);
            var match = events.FirstOrDefault(e => e.Action == OrchestrationAction.SpawnPlanner);
            if (match is not null) return match;
            await Task.Delay(50);
        }

        Assert.Fail($"No SpawnPlanner orchestration event recorded for work unit '{workUnitId}' within the deadline.");
        throw new InvalidOperationException("unreachable");
    }
}
