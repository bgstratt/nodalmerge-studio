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
