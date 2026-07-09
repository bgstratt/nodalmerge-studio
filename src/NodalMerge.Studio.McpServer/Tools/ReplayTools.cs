using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class ReplayTools(IReplayService replay)
{
    [McpServerTool(Name = McpToolNames.ReplayRollback), Description("Rollback a branch to a known good state.")]
    public Task<string> RollbackAsync(
        string branchId,
        string knownGoodStateId,
        CancellationToken cancellationToken = default) =>
        replay.RollbackAsync(branchId, knownGoodStateId, cancellationToken);

    [McpServerTool(Name = McpToolNames.ReplayInspect), Description("Inspect replay history with human-friendly summaries.")]
    public Task<string> InspectAsync(
        string branchId,
        string? nodeId = null,
        CancellationToken cancellationToken = default) =>
        replay.InspectAsync(branchId, nodeId, cancellationToken);
}
