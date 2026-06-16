using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class SchedulerTools(IWorkScheduler scheduler)
{
    [McpServerTool(Name = McpToolNames.SchedulerEnqueue)]
    [Description("Enqueue a work unit for a worker agent to pick up. Use this instead of nm_v1_agent_spawn when the orchestrator wants to delegate worker execution to the scheduler.")]
    public async Task<string> EnqueueAsync(
        string workUnitId,
        string profileId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await scheduler.EnqueueAsync(workUnitId, profileId, taskId, model, baseUrl, apiKey, provider, cancellationToken)
                .ConfigureAwait(false);
            return McpJson.Ok(new { workUnitId, profileId, taskId, status = "enqueued" });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpToolNames.SchedulerEnqueue, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.SchedulerPending)]
    [Description("List pending items in the work scheduler queue.")]
    public async Task<string> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        var items = await scheduler.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { items });
    }
}
