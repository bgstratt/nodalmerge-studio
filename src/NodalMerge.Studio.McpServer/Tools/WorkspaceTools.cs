using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class WorkspaceTools(IWorkspaceService workspace)
{
    [McpServerTool(Name = McpToolNames.WorkspaceSummary), Description("Get workspace summary for control tower UIs.")]
    public async Task<string> SummaryAsync(string? branchId = null, CancellationToken cancellationToken = default)
    {
        var summary = await workspace.GetSummaryAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(summary);
    }
}
