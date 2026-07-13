using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — maps a parsed
// CodexTranscriptRunSummary to the ConversationLogEntry rows CodexCliExecutor records. Mirrors
// ClaudeConversationLogMapper's shape (a small mapper next to the parser, not forced through
// ConversationLogRecorder) but adapted to codex's own summary type — no per-turn Model field (codex's
// item.completed events never carry one, unlike claude's assistant message), no StopReason (codex has
// no equivalent field either), and Provider is "openai" rather than "anthropic" (matches the
// OPENAI_API_KEY env-injection convention CodexCliExecutor uses, mirroring claude-cli's rule — see
// that file's own doc comment on why this is unverified-but-consistent, not a confirmed codex
// behavior).
internal static class CodexConversationLogMapper
{
    // One entry per reconstructed turn (CycleNumber 0..N-1, LogId "CLE-turn-...") plus one terminal
    // run-level entry (CycleNumber N, LogId "CLE-..." — same prefix convention as claude's mapper)
    // carrying the run's tokens/final text. Tokens go only on the terminal entry: turn.completed
    // reports usage once per run in every capture seen, not per turn — splitting it across turns
    // would fabricate data, same reasoning as claude's mapper. When the transcript degraded to
    // run-level-only (no turns reconstructed), this returns exactly one entry.
    public static IReadOnlyList<ConversationLogEntry> BuildEntries(
        CodexTranscriptRunSummary summary, string workUnitId, string agentId, string? taskId,
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
                StopReason: "end_turn",
                OccurredAt: occurredAt,
                SessionId: summary.ThreadId,
                InputTokens: null,
                OutputTokens: null,
                Provider: "openai",
                Model: null));
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
            SessionId: summary.ThreadId,
            InputTokens: summary.InputTokens,
            OutputTokens: summary.OutputTokens,
            Provider: "openai",
            Model: executorName));

        return entries;
    }
}
