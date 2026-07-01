using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ExternalClarificationTools(IClarificationCommandService clarifications)
{
    [McpServerTool(Name = McpServerToolNames.ClarificationRespond)]
    [Description("Respond to a pending agent clarification request. The agent is waiting on a human answer before it can continue. Use nms_v1_goal_status to find pending clarifications and their clarificationIds.")]
    public async Task<string> RespondAsync(
        [Description("The clarificationId from the pending clarification (visible in goal status or the Studio UI).")] string clarificationId,
        [Description("The human response text that the agent will receive.")] string response,
        [Description("If true (default), the agent resumes immediately after receiving this answer. Set false to record the answer without restarting the agent.")] bool resume = true,
        [Description("Optional note for audit trail (e.g. 'reviewed by team lead').")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        // Look up the work unit ID from the active request list
        var active = await clarifications.ListActiveRequestsAsync(cancellationToken).ConfigureAwait(false);
        var item = active.FirstOrDefault(i => i.RequestId.Equals(clarificationId, StringComparison.OrdinalIgnoreCase));

        if (item is null)
            return McpJson.Error(McpServerToolNames.ClarificationRespond,
                $"Clarification '{clarificationId}' not found or already answered. Use nms_v1_goal_status to see pending clarifications.");

        try
        {
            var result = await clarifications.RespondAsync(
                item.WorkUnitId,
                response,
                note: note,
                respondedBy: "external-mcp",
                requestId: clarificationId,
                resume: resume,
                ct: cancellationToken).ConfigureAwait(false);

            return McpJson.Ok(new
            {
                clarificationId,
                workUnitId = item.WorkUnitId,
                status = result.Status,
                resumed = result.Resumed
            });
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpServerToolNames.ClarificationRespond, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpServerToolNames.ClarificationRespond, ex.Message);
        }
    }
}
