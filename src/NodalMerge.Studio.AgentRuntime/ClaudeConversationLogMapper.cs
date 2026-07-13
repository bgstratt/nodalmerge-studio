using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.1 (phase-c-implementation.md C1.b) — maps a parsed
// TranscriptRunSummary to the ConversationLogEntry rows ClaudeCodeExecutor records. Kept as a small
// mapper next to the parser rather than forced through ConversationLogRecorder (native's mapper):
// that one is worker-loop-shaped, taking an LlmResponse per cycle from inside the loop itself,
// which doesn't fit an external harness whose entire transcript arrives after the fact.
internal static class ClaudeConversationLogMapper
{
    // One entry per reconstructed turn (CycleNumber 0..N-1, LogId "CLE-turn-...") plus one terminal
    // run-level entry (CycleNumber N, LogId "CLE-..." — unchanged prefix from pre-C1) carrying the
    // result event's tokens/cost and final text. Tokens go only on the terminal entry: the CLI
    // reports usage once per run, not per turn, so splitting it across turns would fabricate data.
    // When the transcript degraded to run-level-only (no turns reconstructed), this returns exactly
    // one entry — identical in shape to the pre-C1 single-entry behavior.
    public static IReadOnlyList<ConversationLogEntry> BuildEntries(
        TranscriptRunSummary summary, string workUnitId, string agentId, string? taskId,
        string executorName)
    {
        var entries = new List<ConversationLogEntry>(summary.Turns.Count + 1);
        var occurredAt = DateTimeOffset.UtcNow;

        foreach (var turn in summary.Turns)
        {
            entries.Add(new ConversationLogEntry(
                LogId: $"CLE-turn-{Guid.NewGuid():N}",
                WorkUnitId: workUnitId,
                AgentId: agentId,
                AgentRole: "worker",
                TaskId: taskId,
                CycleNumber: turn.CycleNumber,
                AssistantText: turn.AssistantText,
                ToolCalls: turn.ToolCalls,
                ToolResults: turn.ToolResults,
                StopReason: turn.StopReason,
                OccurredAt: occurredAt,
                SessionId: summary.SessionId,
                InputTokens: null,
                OutputTokens: null,
                Provider: "anthropic",
                Model: turn.Model));
        }

        entries.Add(new ConversationLogEntry(
            LogId: $"CLE-{Guid.NewGuid():N}",
            WorkUnitId: workUnitId,
            AgentId: agentId,
            AgentRole: "worker",
            TaskId: taskId,
            CycleNumber: summary.Turns.Count,
            AssistantText: summary.ResultText,
            ToolCalls: [],
            ToolResults: [],
            StopReason: "end_turn",
            OccurredAt: occurredAt,
            SessionId: summary.SessionId,
            InputTokens: summary.InputTokens,
            OutputTokens: summary.OutputTokens,
            Provider: "anthropic",
            Model: executorName));

        return entries;
    }
}
