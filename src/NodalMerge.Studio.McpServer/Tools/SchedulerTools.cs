using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class SchedulerTools(ISchedulerCommandService scheduler, IClarificationCommandService clarifications)
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
        string? sessionId = null,
        string? credentialRef = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var item = await scheduler.EnqueueAsync(workUnitId, profileId, taskId, model, baseUrl, apiKey, provider, sessionId, credentialRef, cancellationToken)
                .ConfigureAwait(false);
            return McpJson.Ok(new { workUnitId = item.WorkUnitId, profileId = item.ProfileId, taskId = item.TaskId, sessionId = item.SessionId, status = "enqueued" });
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

    [McpServerTool(Name = McpToolNames.ClarificationRequest)]
    [Description("Request a human clarification for a work unit. With blocking=true, the scheduler entry is paused awaiting resume. Optionally set timeoutSeconds so the system auto-responds if no human replies in time.")]
    public async Task<string> ClarificationRequestAsync(
        string workUnitId,
        string question,
        string? context = null,
        bool blocking = true,
        string[]? options = null,
        string? requestedByAgentId = null,
        string? sessionId = null,
        [Description("Seconds before auto-responding. Null means wait indefinitely.")] int? timeoutSeconds = null,
        [Description("What to do on timeout: 'auto_continue' (default), 'auto_abandon', or 'use_default'.")] string? timeoutBehavior = null,
        [Description("Response text to use when auto-responding on timeout.")] string? defaultResponse = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await clarifications.RequestAsync(
                workUnitId,
                question,
                context,
                blocking,
                options,
                requestedByAgentId,
                sessionId,
                timeoutSeconds,
                timeoutBehavior,
                defaultResponse,
                cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                requestId = result.RequestId,
                workUnitId = result.WorkUnitId,
                status = result.Status,
                awaitingClarification = result.ParkedAwaitingResponse,
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpToolNames.ClarificationRequest, ex.Message);
        }
    }
}
