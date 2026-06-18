using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ArtifactTools(IArtifactCommandService artifacts)
{
    [McpServerTool(Name = McpToolNames.ArtifactRecord), Description("Record a durable knowledge note (Research, Decision, or Constraint) so future work units don't have to rediscover it.")]
    public async Task<string> RecordAsync(
        string workUnitId,
        string type,
        string title,
        string body,
        string? parentArtifactId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var recorded = await artifacts.RecordAsync(workUnitId, type, title, body, parentArtifactId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { artifactId = recorded.ArtifactId });
        }
        catch (ArgumentException ex)
        {
            return McpJson.Error(McpToolNames.ArtifactRecord, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.ArtifactQuery), Description("Search knowledge artifacts for a work unit and its ancestors by type and/or keyword.")]
    public async Task<string> QueryAsync(
        string workUnitId,
        string? type = null,
        string? keywords = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var filtered = await artifacts.QueryAsync(workUnitId, type, keywords, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(filtered);
        }
        catch (ArgumentException ex)
        {
            return McpJson.Error(McpToolNames.ArtifactQuery, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.ArtifactList), Description("List the full artifact chain for a work unit, including ancestors' artifacts by default.")]
    public async Task<string> ListAsync(
        string workUnitId,
        bool includeAncestors = true,
        CancellationToken cancellationToken = default)
    {
        var list = await artifacts.ListAsync(workUnitId, includeAncestors, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(list);
    }
}