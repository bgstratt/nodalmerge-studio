using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Slice 14c — declarative file-scope/domain worker routing. No orchestrator agent spawned (see
// NonOverlappingFileScopeFanOutTests.cs's comment on why: avoids racing the background loop's own
// post-turn fan-out).
[Trait("Category", "Integration")]
public class FileScopeProfileRoutingTests
{
    [Fact]
    public async Task Slice_with_fileScope_matching_exactly_one_profiles_patterns_routes_deterministically()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut = app.Services.GetRequiredService<IFanOutService>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        await profiles.CreateAsync(new AgentProfile(
            "tsx-worker", "TSX Worker", PipelineStage.Execute, string.Empty, [], 20, ["**/*.tsx"]));
        await profiles.CreateAsync(new AgentProfile(
            "cs-worker", "CS Worker", PipelineStage.Execute, string.Empty, [], 20, ["**/*.cs"]));

        var parent = await orchestrator.CreateWorkUnitAsync("Build UI", "test");

        var planJson = """
            {
              "slices": [
                { "sliceId": "s1", "goal": "Implement Foo.tsx", "fileScope": ["src/Foo.tsx"], "dependsOn": [], "steps": ["edit"] }
              ]
            }
            """;
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", planJson));

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        Assert.Single(result.EnqueuedWorkUnitIds);

        var events = await decisionLog.GetEventsAsync(parent.WorkUnitId);
        var enqueueEvent = events.Single(e => e.Action == OrchestrationAction.Enqueue);
        Assert.Contains("\"matchedPattern\":true", enqueueEvent.InputProjectionSnapshot);
        Assert.Contains("\"selectedProfileId\":\"tsx-worker\"", enqueueEvent.InputProjectionSnapshot);
    }

    [Fact]
    public async Task Slice_with_fileScope_matching_zero_profiles_falls_through_to_heuristic_default()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut = app.Services.GetRequiredService<IFanOutService>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        await profiles.CreateAsync(new AgentProfile(
            "tsx-worker", "TSX Worker", PipelineStage.Execute, string.Empty, [], 20, ["**/*.tsx"]));

        var parent = await orchestrator.CreateWorkUnitAsync("Build backend", "test");

        var planJson = """
            {
              "slices": [
                { "sliceId": "s1", "goal": "Implement Foo.cs", "fileScope": ["src/Foo.cs"], "dependsOn": [], "steps": ["edit"] }
              ]
            }
            """;
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", planJson));

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        Assert.Single(result.EnqueuedWorkUnitIds);

        var events = await decisionLog.GetEventsAsync(parent.WorkUnitId);
        var enqueueEvent = events.Single(e => e.Action == OrchestrationAction.Enqueue);
        Assert.Contains("\"matchedPattern\":false", enqueueEvent.InputProjectionSnapshot);
        Assert.Contains("\"selectedProfileId\":\"worker\"", enqueueEvent.InputProjectionSnapshot);
    }

    [Fact]
    public async Task Slice_with_one_fileScope_path_unmatched_by_a_profiles_patterns_falls_through()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut = app.Services.GetRequiredService<IFanOutService>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        await profiles.CreateAsync(new AgentProfile(
            "tsx-worker", "TSX Worker", PipelineStage.Execute, string.Empty, [], 20, ["**/*.tsx"]));

        var parent = await orchestrator.CreateWorkUnitAsync("Build mixed slice", "test");

        // Every path must match for the deterministic tier to engage — Foo.cs doesn't match
        // tsx-worker's pattern, so this falls through despite Foo.tsx matching.
        var planJson = """
            {
              "slices": [
                { "sliceId": "s1", "goal": "Implement Foo", "fileScope": ["src/Foo.tsx", "src/Foo.cs"], "dependsOn": [], "steps": ["edit"] }
              ]
            }
            """;
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", planJson));

        await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        var events = await decisionLog.GetEventsAsync(parent.WorkUnitId);
        var enqueueEvent = events.Single(e => e.Action == OrchestrationAction.Enqueue);
        Assert.Contains("\"matchedPattern\":false", enqueueEvent.InputProjectionSnapshot);
        Assert.Contains("\"selectedProfileId\":\"worker\"", enqueueEvent.InputProjectionSnapshot);
    }

    [Fact]
    public async Task Matched_profile_with_bound_Model_Profile_enqueues_child_on_that_model()
    {
        // plans/scoped-execute-workers-per-profile-models.md — when a file-scope match routes a slice
        // to a profile whose bound Model Profile was resolved into profileCredentials at spawn, the
        // child enqueues on *that* model, not the Execute-stage default.
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut = app.Services.GetRequiredService<IFanOutService>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        await profiles.CreateAsync(new AgentProfile(
            "frontend-worker", "Frontend Worker", PipelineStage.Execute, string.Empty, [], 20,
            ["**/*.tsx"], ModelProfileId: "sonnet-frontend"));

        var parent = await orchestrator.CreateWorkUnitAsync("Build UI", "test");

        // Register the goal's credentials: an Execute-stage default plus the frontend-worker's bound
        // Model Profile. The host is never started here, so the poll loop never runs — the manual
        // fan-out below is the only enqueue path (same race-avoidance the other tests rely on).
        var stageCredentials = new Dictionary<PipelineStage, GoalDefaultCredentials>
        {
            [PipelineStage.Execute] = new("anthropic", "stage-model", "http://stage", "stage-key", null),
        };
        var profileCredentials = new Dictionary<string, GoalDefaultCredentials>
        {
            ["frontend-worker"] = new("anthropic", "sonnet-frontend", "http://frontend", "fe-key", "frontend-worker"),
        };
        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId, model: "m", baseUrl: "http://fake-llm", apiKey: "k",
            stageCredentials: stageCredentials, profileCredentials: profileCredentials);

        var planJson = """
            {
              "slices": [
                { "sliceId": "s1", "goal": "Implement Foo.tsx", "fileScope": ["src/Foo.tsx"], "dependsOn": [], "steps": ["edit"] }
              ]
            }
            """;
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", planJson));

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);
        var childId = Assert.Single(result.EnqueuedWorkUnitIds);

        var pending = await scheduler.ListPendingAsync();
        var childItem = pending.Single(i => i.WorkUnitId == childId);
        Assert.Equal("frontend-worker", childItem.ProfileId);
        // The bound model wins over the Execute-stage default.
        Assert.Equal("sonnet-frontend", childItem.Model);
        Assert.Equal("http://frontend", childItem.BaseUrl);
    }

    [Fact]
    public async Task Matched_profile_without_a_bound_model_inherits_the_stage_default()
    {
        // Back-compat: a file-scope-matched profile that declared no Model Profile binding runs on
        // the Execute-stage default — the enqueue is identical to before this feature existed.
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut = app.Services.GetRequiredService<IFanOutService>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        await profiles.CreateAsync(new AgentProfile(
            "frontend-worker", "Frontend Worker", PipelineStage.Execute, string.Empty, [], 20,
            ["**/*.tsx"]));

        var parent = await orchestrator.CreateWorkUnitAsync("Build UI", "test");

        var stageCredentials = new Dictionary<PipelineStage, GoalDefaultCredentials>
        {
            [PipelineStage.Execute] = new("anthropic", "stage-model", "http://stage", "stage-key", null),
        };
        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId, model: "m", baseUrl: "http://fake-llm", apiKey: "k",
            stageCredentials: stageCredentials);

        var planJson = """
            {
              "slices": [
                { "sliceId": "s1", "goal": "Implement Foo.tsx", "fileScope": ["src/Foo.tsx"], "dependsOn": [], "steps": ["edit"] }
              ]
            }
            """;
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", planJson));

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);
        var childId = Assert.Single(result.EnqueuedWorkUnitIds);

        var pending = await scheduler.ListPendingAsync();
        var childItem = pending.Single(i => i.WorkUnitId == childId);
        Assert.Equal("frontend-worker", childItem.ProfileId);
        Assert.Equal("stage-model", childItem.Model);
    }

    [Fact]
    public async Task Slice_with_fileScope_matching_multiple_profiles_falls_through_to_heuristic_default()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut = app.Services.GetRequiredService<IFanOutService>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        var decisionLog = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        await profiles.CreateAsync(new AgentProfile(
            "frontend-worker", "Frontend Worker", PipelineStage.Execute, string.Empty, [], 20, ["src/**/*"]));
        await profiles.CreateAsync(new AgentProfile(
            "src-worker", "Src Worker", PipelineStage.Execute, string.Empty, [], 20, ["src/**/*"]));

        var parent = await orchestrator.CreateWorkUnitAsync("Build UI", "test");

        var planJson = """
            {
              "slices": [
                { "sliceId": "s1", "goal": "Implement Foo.tsx", "fileScope": ["src/Foo.tsx"], "dependsOn": [], "steps": ["edit"] }
              ]
            }
            """;
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", planJson));

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        Assert.Single(result.EnqueuedWorkUnitIds);

        var events = await decisionLog.GetEventsAsync(parent.WorkUnitId);
        var enqueueEvent = events.Single(e => e.Action == OrchestrationAction.Enqueue);
        Assert.Contains("\"matchedPattern\":false", enqueueEvent.InputProjectionSnapshot);
        Assert.Contains("\"selectedProfileId\":\"worker\"", enqueueEvent.InputProjectionSnapshot);
    }
}
