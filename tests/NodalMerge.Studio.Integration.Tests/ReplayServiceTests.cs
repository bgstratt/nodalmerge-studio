using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 13f — IReplayService was a 100%-stub no-op wired to real MCP tools that silently did
/// nothing. The real ReplayService is built entirely on existing query surfaces
/// (IArtifactLineageService, IOrchestrationDecisionLogService) and 13e's now-real
/// snapshot/restore primitive (IKnownGoodStateService) — no new generic node-store scanner.
/// </summary>
[Trait("Category", "Integration")]
public class ReplayServiceTests
{
    private static async Task<(
        IOrchestratorService Orchestrator,
        IArtifactLineageService Artifacts,
        IOrchestrationDecisionLogService DecisionLog,
        IKnownGoodStateService KnownGood,
        IFileWorkspaceService FileWorkspace,
        IReplayService Replay)> BuildAsync()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        return (
            app.Services.GetRequiredService<IOrchestratorService>(),
            app.Services.GetRequiredService<IArtifactLineageService>(),
            app.Services.GetRequiredService<IOrchestrationDecisionLogService>(),
            app.Services.GetRequiredService<IKnownGoodStateService>(),
            app.Services.GetRequiredService<IFileWorkspaceService>(),
            app.Services.GetRequiredService<IReplayService>());
    }

    [Fact]
    public async Task RangeAsync_returns_artifacts_and_orchestration_events_in_chronological_order_and_can_be_bounded()
    {
        var (orchestrator, artifacts, decisionLog, _, _, replay) = await BuildAsync();
        var unit = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");

        var artifact = await artifacts.RecordAsync(new ArtifactRef(
            $"ART-{Guid.NewGuid():N}", ArtifactType.Task, null, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, unit.WorkUnitId, null));

        var orchestrationEvent = await decisionLog.RecordAsync(
            unit.WorkUnitId, "fanout", PipelineStage.Execute, "{}",
            OrchestrationAction.SpawnWorker, [], "heuristic: always worker");

        var json = await replay.RangeAsync(unit.BranchId);
        using var doc = JsonDocument.Parse(json);
        var entries = doc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        var nodeIds = entries.Select(e => e.GetProperty("nodeId").GetString()).ToList();

        // CreateWorkUnitAsync itself records a Goal artifact, so don't assume an exact count —
        // just that ours showed up, in the order they actually happened.
        Assert.Contains(artifact.ArtifactId, nodeIds);
        Assert.Contains(orchestrationEvent.EventId, nodeIds);
        Assert.True(nodeIds.IndexOf(artifact.ArtifactId) < nodeIds.IndexOf(orchestrationEvent.EventId));

        // Bounded to just the artifact: fromNode == toNode == the artifact's own node id.
        var bounded = await replay.RangeAsync(unit.BranchId, fromNode: artifact.ArtifactId, toNode: artifact.ArtifactId);
        using var boundedDoc = JsonDocument.Parse(bounded);
        var boundedEntries = boundedDoc.RootElement.GetProperty("entries").EnumerateArray().ToList();
        Assert.Single(boundedEntries);
        Assert.Equal(artifact.ArtifactId, boundedEntries[0].GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task InspectAsync_with_no_nodeId_summarizes_the_branch_and_with_a_nodeId_finds_the_matching_record()
    {
        var (orchestrator, artifacts, _, knownGood, _, replay) = await BuildAsync();
        var unit = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");

        var artifact = await artifacts.RecordAsync(new ArtifactRef(
            $"ART-{Guid.NewGuid():N}", ArtifactType.Task, null, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, unit.WorkUnitId, null));

        var state = await knownGood.MarkKnownGoodAsync(new KnownGoodState(
            $"KGS-{Guid.NewGuid():N}", unit.BranchId, "checkpoint", null, DateTimeOffset.UtcNow, "test"));

        var summaryJson = await replay.InspectAsync(unit.BranchId);
        using var summaryDoc = JsonDocument.Parse(summaryJson);
        Assert.Equal(state.StateId, summaryDoc.RootElement.GetProperty("currentKnownGoodStateId").GetString());
        // CreateWorkUnitAsync itself records a Goal artifact, plus the Task artifact recorded above.
        Assert.Equal(2, summaryDoc.RootElement.GetProperty("artifactCount").GetInt32());

        var artifactJson = await replay.InspectAsync(unit.BranchId, artifact.ArtifactId);
        using var artifactDoc = JsonDocument.Parse(artifactJson);
        Assert.Equal("artifact", artifactDoc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(artifact.ArtifactId, artifactDoc.RootElement.GetProperty("artifact").GetProperty("artifactId").GetString());

        var missingJson = await replay.InspectAsync(unit.BranchId, "NOPE-does-not-exist");
        using var missingDoc = JsonDocument.Parse(missingJson);
        Assert.True(missingDoc.RootElement.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task RollbackAsync_restores_known_good_content_and_rejects_a_state_from_another_branch()
    {
        var (orchestrator, _, _, knownGood, fileWorkspace, replay) = await BuildAsync();
        var unit = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.WriteAsync(unit.BranchId, "src/feature.cs", "// known good content");

        var state = await knownGood.MarkKnownGoodAsync(new KnownGoodState(
            $"KGS-{Guid.NewGuid():N}", unit.BranchId, "checkpoint", null, DateTimeOffset.UtcNow, "test"));

        await fileWorkspace.WriteAsync(unit.BranchId, "src/feature.cs", "// a risky edit");

        var otherUnit = await orchestrator.CreateWorkUnitAsync("Unrelated feature", "test");
        var rejected = await replay.RollbackAsync(otherUnit.BranchId, state.StateId);
        using var rejectedDoc = JsonDocument.Parse(rejected);
        Assert.True(rejectedDoc.RootElement.TryGetProperty("error", out _));
        // Rejected without restoring — the risky edit on the real branch must still be there.
        Assert.Equal("// a risky edit", await fileWorkspace.ReadAsync(unit.BranchId, "src/feature.cs"));

        var accepted = await replay.RollbackAsync(unit.BranchId, state.StateId);
        using var acceptedDoc = JsonDocument.Parse(accepted);
        Assert.False(acceptedDoc.RootElement.TryGetProperty("error", out _));
        Assert.Equal("// known good content", await fileWorkspace.ReadAsync(unit.BranchId, "src/feature.cs"));
    }
}
