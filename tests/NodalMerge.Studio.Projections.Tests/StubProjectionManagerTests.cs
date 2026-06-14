using NodalMerge.Studio.Contracts.Projections;

namespace NodalMerge.Studio.Projections.Tests;

public class StubProjectionManagerTests
{
    [Fact]
    public async Task GetAsync_returns_requested_type_and_level()
    {
        var manager = new StubProjectionManager();
        var result = await manager.GetAsync(
            new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Compact));

        Assert.Equal(ProjectionType.WorkUnit, result.Type);
        Assert.Equal(ProjectionLevel.Compact, result.Level);
        Assert.Contains("WorkUnit", result.DataJson, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProjectionType.Task, ProjectionLevel.Full)]
    [InlineData(ProjectionType.MergeProposal, ProjectionLevel.Emergency)]
    public async Task GetAsync_routes_all_projection_types(ProjectionType type, ProjectionLevel level)
    {
        var manager = new StubProjectionManager();
        var result = await manager.GetAsync(new ProjectionRequest(type, level));
        Assert.Equal(type, result.Type);
        Assert.Equal(level, result.Level);
    }
}
