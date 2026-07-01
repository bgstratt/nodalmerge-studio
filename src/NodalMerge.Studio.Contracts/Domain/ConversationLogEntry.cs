namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// One agent-loop cycle's LLM exchange, stored under studio/conversation-log/v1 — the durable
/// counterpart to the ephemeral CurrentActivity label, giving a human a full audit trail of what
/// an agent tried (assistant text, tool calls, tool results) on the way to a decision.
/// </summary>
public sealed record ConversationLogEntry(
    string LogId,
    string WorkUnitId,
    string AgentId,
    string AgentRole,
    string? TaskId,
    int CycleNumber,
    string? AssistantText,
    IReadOnlyList<ConversationToolCall> ToolCalls,
    IReadOnlyList<ConversationToolResult> ToolResults,
    string StopReason,
    DateTimeOffset OccurredAt,
    string? SessionId = null,
    // Usage tokens from the underlying LlmResponse for this cycle — null when the provider didn't
    // report usage (some OpenAI-compatible endpoints omit it).
    int? InputTokens = null,
    int? OutputTokens = null,
    // Which provider/model actually served this cycle — the loop's own credentials, which may
    // differ per stage when a Goal's Agent Topology configures a distinct profile for Plan/Execute/
    // Review. Lets a human confirm a multi-model run is actually routing to different models.
    string? Provider = null,
    string? Model = null,
    // True when InputTokens/OutputTokens came from a client-side tokenizer estimate (vscode-lm)
    // rather than a real provider-reported usage block — the UI renders these with a "~" prefix.
    bool TokensEstimated = false);

public sealed record ConversationToolCall(string ToolUseId, string Name, string InputJson);

public sealed record ConversationToolResult(string ToolUseId, string Result, bool Truncated);
