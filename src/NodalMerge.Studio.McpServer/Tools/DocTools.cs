using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class DocTools(IDocFetchCommandService docs, WorkspaceOptions options)
{
    [McpServerTool(Name = McpToolNames.DocFetch), Description("Fetch constrained external documentation with provenance metadata and source artifact recording.")]
    public async Task<string> FetchAsync(
        string url,
        string reason,
        string workUnitId,
        CancellationToken cancellationToken = default)
    {
        if (!options.DocFetchTools)
            return McpJson.Error(McpToolNames.DocFetch, "Doc fetch tools are disabled by configuration.");

        try
        {
            var result = await docs.FetchAsync(url, reason, workUnitId, sessionId: null, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(result);
        }
        catch (ArgumentException ex)
        {
            return McpJson.Error(McpToolNames.DocFetch, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpToolNames.DocFetch, ex.Message);
        }
    }
}
