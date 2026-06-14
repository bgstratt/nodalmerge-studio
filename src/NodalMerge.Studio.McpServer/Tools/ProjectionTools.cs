using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ProjectionTools(IProjectionManager projections)
{
    [McpServerTool(Name = McpToolNames.ProjectionGet), Description("Get a projection by type and compression level.")]
    public async Task<string> GetAsync(
        string projectionType,
        string projectionLevel = "Normal",
        string? workUnitId = null,
        string? branchId = null,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ProjectionType>(projectionType, ignoreCase: true, out var type) ||
            !Enum.TryParse<ProjectionLevel>(projectionLevel, ignoreCase: true, out var level))
        {
            return McpJson.Error(McpToolNames.ProjectionGet, "Invalid projectionType or projectionLevel.");
        }

        var result = await projections.GetAsync(
            new ProjectionRequest(type, level, workUnitId, branchId, agentId),
            cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            contractVersion = McpContract.Version,
            projectionType = result.Type.ToString(),
            level = result.Level.ToString(),
            data = JsonSerializer.Deserialize<object>(result.DataJson)
        });
    }

    [McpServerTool(Name = McpToolNames.ProjectionList), Description("List available projection types and levels.")]
    public Task<string> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(McpJson.Ok(new
        {
            types = ProjectionCatalog.Types,
            levels = ProjectionCatalog.Levels
        }));
}
