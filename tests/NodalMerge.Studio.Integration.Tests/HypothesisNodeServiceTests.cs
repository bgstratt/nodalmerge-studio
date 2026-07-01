using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class HypothesisNodeServiceTests
{
    private static IHypothesisNodeService Build()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());
        return app.Services.GetRequiredService<IHypothesisNodeService>();
    }

    [Fact]
    public async Task RecordAsync_then_ListByParentWorkUnitIdAsync_round_trips()
    {
        var hypotheses = Build();

        var node = new HypothesisNode(
            HypothesisId: "hyp-1", WorkUnitId: "wu-fork-a", Goal: "Try Redis",
            ForkType: HypothesisForkType.Library, Status: HypothesisStatus.Active,
            ParentWorkUnitId: "wu-parent", BranchedFromProposalId: null, Rationale: null,
            CreatedAt: DateTimeOffset.UtcNow);

        await hypotheses.RecordAsync(node);

        var list = await hypotheses.ListByParentWorkUnitIdAsync("wu-parent");
        Assert.Single(list);
        Assert.Equal("hyp-1", list[0].HypothesisId);
        Assert.Equal(HypothesisStatus.Active, list[0].Status);
    }

    [Fact]
    public async Task ListByParentWorkUnitIdAsync_only_returns_matching_parent()
    {
        var hypotheses = Build();

        await hypotheses.RecordAsync(new HypothesisNode(
            "hyp-1", "wu-a", "Goal A", HypothesisForkType.Model, HypothesisStatus.Active,
            "wu-parent-1", null, null, DateTimeOffset.UtcNow));
        await hypotheses.RecordAsync(new HypothesisNode(
            "hyp-2", "wu-b", "Goal B", HypothesisForkType.Model, HypothesisStatus.Active,
            "wu-parent-2", null, null, DateTimeOffset.UtcNow));

        var list = await hypotheses.ListByParentWorkUnitIdAsync("wu-parent-1");
        Assert.Single(list);
        Assert.Equal("hyp-1", list[0].HypothesisId);
    }

    [Fact]
    public async Task UpdateStatusAsync_transitions_status_and_persists()
    {
        var hypotheses = Build();

        var node = await hypotheses.RecordAsync(new HypothesisNode(
            "hyp-1", "wu-a", "Goal A", HypothesisForkType.Architecture, HypothesisStatus.Active,
            "wu-parent", null, null, DateTimeOffset.UtcNow));

        var updated = await hypotheses.UpdateStatusAsync(node.HypothesisId, HypothesisStatus.Converged);
        Assert.Equal(HypothesisStatus.Converged, updated.Status);

        var list = await hypotheses.ListByParentWorkUnitIdAsync("wu-parent");
        Assert.Equal(HypothesisStatus.Converged, list.Single().Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_throws_for_unknown_hypothesis()
    {
        var hypotheses = Build();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => hypotheses.UpdateStatusAsync("missing", HypothesisStatus.Rejected));
    }
}
