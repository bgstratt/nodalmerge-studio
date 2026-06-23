using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// Shared NmContent -> ConversationLogEntry mapping for the four agent loops — every loop calls
// this once per cycle, right where it already builds toolResults, so it's a small addition at an
// existing instrumentation point rather than a restructuring (see ActivityLabeler call sites).
internal static class ConversationLogRecorder
{
    public static async Task RecordTurnAsync(
        IConversationLogService? conversationLog,
        string workUnitId,
        string agentId,
        string agentRole,
        string? taskId,
        int cycleNumber,
        LlmResponse response,
        IReadOnlyList<NmContent> toolResults,
        string? sessionId,
        CancellationToken ct,
        string? provider = null,
        string? model = null)
    {
        if (conversationLog is null)
            return;

        var assistantText = string.Join("\n", response.Content.OfType<NmText>().Select(t => t.Text));
        var toolCalls = response.Content.OfType<NmToolUse>()
            .Select(u => new ConversationToolCall(u.Id, u.Name, u.Input.GetRawText()))
            .ToList();
        var results = toolResults.OfType<NmToolResult>()
            .Select(r => new ConversationToolResult(r.ToolUseId, r.Result, false))
            .ToList();

        await conversationLog.RecordAsync(new ConversationLogEntry(
            LogId: $"conv-{Guid.NewGuid():N}",
            WorkUnitId: workUnitId,
            AgentId: agentId,
            AgentRole: agentRole,
            TaskId: taskId,
            CycleNumber: cycleNumber,
            AssistantText: assistantText.Length == 0 ? null : assistantText,
            ToolCalls: toolCalls,
            ToolResults: results,
            StopReason: response.StopReason,
            OccurredAt: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            InputTokens: response.InputTokens,
            OutputTokens: response.OutputTokens,
            Provider: provider,
            Model: model), ct).ConfigureAwait(false);
    }
}
