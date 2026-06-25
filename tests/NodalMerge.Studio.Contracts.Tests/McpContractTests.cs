using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.Contracts.Tests;

public class McpToolNamesTests
{
    [Fact]
    public void All_tools_use_nm_v1_prefix()
    {
        Assert.All(McpToolNames.All, name =>
        {
            Assert.StartsWith($"{McpContract.NamespacePrefix}_", name, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void All_tools_are_unique()
    {
        Assert.Equal(McpToolNames.All.Count, McpToolNames.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(McpToolNames.ProjectionGet)]
    [InlineData(McpToolNames.WorkUnitCreate)]
    [InlineData(McpToolNames.WorkspaceSummary)]
    [InlineData(McpToolNames.WorkspaceStatus)]
    public void Frozen_tool_names_match_contract(string toolName)
    {
        Assert.Contains(toolName, McpToolNames.All);
    }
}

public class ProjectionCatalogTests
{
    [Fact]
    public void Catalog_lists_all_projection_types()
    {
        Assert.Equal(Enum.GetValues<ProjectionType>().Length, ProjectionCatalog.Types.Count);
        Assert.Contains("WorkUnit", ProjectionCatalog.Types);
        Assert.Contains("ExecutionSnapshot", ProjectionCatalog.Types);
    }
}
