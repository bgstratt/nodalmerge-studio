using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Comparison engine — ProjectionType.HypothesisComparison. Deterministic, evidence-based
/// aggregation across sibling forks; a pure read that recommends but never decides.
/// </summary>
[Trait("Category", "Integration")]
public class HypothesisComparisonProjectionTests
{
    private static async Task<(
        IExperimentService Experiments,
        IMergeCommandService MergeCommands,
        IEvidenceNodeService Evidence,
        IProjectionManager Projections,
        IFileWorkspaceService FileWorkspace)> BuildAsync()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        await fileWorkspace.InitBranchAsync("main");

        return (
            app.Services.GetRequiredService<IExperimentService>(),
            app.Services.GetRequiredService<IMergeCommandService>(),
            app.Services.GetRequiredService<IEvidenceNodeService>(),
            app.Services.GetRequiredService<IProjectionManager>(),
            fileWorkspace);
    }

    [Fact]
    public async Task GetAsync_recommends_the_sibling_with_the_higher_score()
    {
        var (experiments, mergeCommands, evidence, projections, fileWorkspace) = await BuildAsync();

        var result = await experiments.CreateAsync(new ExperimentSpec(
            "Pick a caching layer", "test", HypothesisForkType.Library,
            [
                new ExperimentForkSpec(null, "Redis"),
                new ExperimentForkSpec(null, "Memcached"),
            ]));

        var strongForkId = result.ForkWorkUnitIds[0];
        var weakForkId = result.ForkWorkUnitIds[1];

        await fileWorkspace.InitBranchAsync(strongForkId);
        await fileWorkspace.InitBranchAsync(weakForkId);

        await mergeCommands.ProposeAsync(
            sourceBranch: strongForkId, targetBranch: "main", summary: "Use Redis", workUnitId: strongForkId);
        await mergeCommands.ProposeAsync(
            sourceBranch: weakForkId, targetBranch: "main", summary: "Use Memcached", workUnitId: weakForkId);

        await evidence.RecordAsync(new EvidenceNode(
            "ev-1", strongForkId, null, EvidenceKind.AutomatedReview, "Approved — all tests pass", null, DateTimeOffset.UtcNow));
        await evidence.RecordAsync(new EvidenceNode(
            "ev-2", weakForkId, null, EvidenceKind.AutomatedReview, "Rejected — missing tests", null, DateTimeOffset.UtcNow));

        var projectionResult = await projections.GetAsync(
            new ProjectionRequest(ProjectionType.HypothesisComparison, ProjectionLevel.Normal, WorkUnitId: result.ParentWorkUnitId));

        var payload = JsonSerializer.Deserialize<HypothesisComparisonProjectionPayload>(
            projectionResult.DataJson, JsonSerializerOptions.Web);

        Assert.NotNull(payload);
        Assert.Equal(2, payload!.Siblings.Count);
        Assert.Equal(strongForkId, payload.RecommendedWorkUnitId);

        var strong = payload.Siblings.Single(s => s.WorkUnitId == strongForkId);
        var weak = payload.Siblings.Single(s => s.WorkUnitId == weakForkId);
        Assert.True(strong.Score > weak.Score);
    }

    [Fact]
    public async Task GetAsync_returns_error_payload_when_parent_has_no_children()
    {
        var (_, _, _, projections, _) = await BuildAsync();

        var result = await projections.GetAsync(
            new ProjectionRequest(ProjectionType.HypothesisComparison, ProjectionLevel.Normal, WorkUnitId: "no-such-parent"));

        Assert.Contains("error", result.DataJson);
    }
}
