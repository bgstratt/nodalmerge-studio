using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class SnapshotTools(ISnapshotService snapshots)
{
    [McpServerTool(Name = McpToolNames.SnapshotGet), Description("Get an execution snapshot for an agent.")]
    public async Task<string> GetAsync(
        string agentId,
        string? workUnitId = null,
        string? branchId = null,
        CancellationToken cancellationToken = default)
    {
        if (workUnitId is null)
        {
            return McpJson.Error(McpToolNames.SnapshotGet, "workUnitId is required until multi-work-unit snapshots are supported.");
        }

        var snapshot = await snapshots.GetAsync(agentId, workUnitId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new
        {
            agentId,
            branchId,
            currentGoal = snapshot.CurrentGoal,
            currentTask = snapshot.CurrentTask,
            failureCount = snapshot.FailureCount,
            rollbackCount = snapshot.RollbackCount,
            nextSuggestedAction = snapshot.NextSuggestedAction
        });
    }

    [McpServerTool(Name = McpToolNames.SnapshotCompare), Description("Compare execution snapshots between agents.")]
    public Task<string> CompareAsync(
        string agentId,
        string otherAgentId,
        string? workUnitId = null,
        CancellationToken cancellationToken = default)
    {
        if (workUnitId is null)
        {
            return Task.FromResult(McpJson.Error(McpToolNames.SnapshotCompare, "workUnitId is required."));
        }

        return snapshots.CompareAsync(agentId, workUnitId, otherAgentId, cancellationToken);
    }
}
