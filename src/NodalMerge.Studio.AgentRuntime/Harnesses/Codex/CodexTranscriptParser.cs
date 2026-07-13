using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — the versioned
// `codex exec --json` parser, following ClaudeTranscriptParser.V1's own convention (a static outer
// class + version-suffixed nested implementation, selected by Create()). Field shapes are grounded
// in real `codex exec --json --skip-git-repo-check` captures against codex-cli 0.144.1 (2026-07-12,
// codex-probe/capture-2..5) — not training-data assumptions. Notably different from claude's
// stream-json: codex has no terminal "result"/subtype/is_error event and reports no cost (ChatGPT-
// seat auth) — this parser produces no such fields at all, rather than always-null placeholders for
// them, so CodexCliExecutor can't accidentally treat "not reported" as "false"/"success".

// One reconstructed codex "turn": codex's own JSON separates a turn into several `item.completed`
// events (agent_message / file_change / command_execution) rather than claude's single assistant
// message carrying both text and tool_use blocks — this turn boundary mirrors claude's anyway: an
// agent_message item commits a turn, and any file_change/command_execution items that arrived since
// the previous commit become that turn's ToolCalls/ToolResults (the real captures show tool items
// arriving *before* the agent_message that reports on them, i.e. mid-turn, not after).
internal sealed record CodexTranscriptTurn(
    int CycleNumber,
    string? AssistantText,
    IReadOnlyList<ConversationToolCall> ToolCalls,
    IReadOnlyList<ConversationToolResult> ToolResults);

// Everything one codex run yields. No Subtype/IsError/TotalCostUsd fields (codex's JSON has none of
// these) — CodexCliExecutor judges run success from the process exit code alone, matching the task
// brief's verified finding "no permission-denial event type — a sandbox denial manifests as
// agent_message prose only."
internal sealed record CodexTranscriptRunSummary(
    string? ResultText,
    int? InputTokens,
    int? OutputTokens,
    string? ThreadId,
    IReadOnlyList<CodexTranscriptTurn> Turns);

internal interface ICodexTranscriptParser
{
    // Feeds one raw stdout line. Never throws — a malformed/partial line, or a line whose shape
    // doesn't match the detected format, is skipped rather than failing the run.
    void Accept(string line);

    CodexTranscriptRunSummary BuildSummary();
}

internal static class CodexTranscriptParser
{
    public static ICodexTranscriptParser Create(Action<string?>? onActivity = null) => new V1(onActivity);

    // V1 — codex-cli 0.144.1's `--json` shape. `thread.started` (once, at the very start) carries
    // thread_id — the resumable session identity, NOT repeated on every line the way claude's
    // session_id is (verified across all five codex-probe captures). `item.completed` carries one
    // of agent_message/file_change/command_execution (and possibly other item types this version
    // ignores). `turn.completed` carries usage tokens and no other terminal scalar.
    //
    // Degrade rule, mirroring ClaudeTranscriptParser.V1: turn-level reconstruction only turns on
    // once a `thread.started` line has been seen. An unrecognized/missing marker leaves turns
    // empty, but the last-seen agent_message text and turn.completed's usage tokens are still
    // captured independently (same "terminal scalar parsing is unconditional" rule) — "degrade to
    // run-level telemetry", never a thrown exception.
    internal sealed class V1(Action<string?>? onActivity) : ICodexTranscriptParser
    {
        private readonly List<MutableTurn> _turns = [];
        private readonly List<ConversationToolCall> _pendingToolCalls = [];
        private readonly List<ConversationToolResult> _pendingToolResults = [];

        private bool _formatConfirmed;
        private string? _threadId;
        private string? _lastAssistantText;
        private int? _inputTokens;
        private int? _outputTokens;

        public void Accept(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // A partial/malformed line is not fatal to the run — skip it and keep reading.
                return;
            }

            using (doc)
            {
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeProp))
                    return;
                var type = typeProp.GetString();

                switch (type)
                {
                    case "thread.started":
                        _formatConfirmed = true;
                        if (root.TryGetProperty("thread_id", out var tid) && tid.ValueKind == JsonValueKind.String)
                            _threadId = tid.GetString();
                        break;
                    case "item.completed":
                        HandleItemCompleted(root);
                        break;
                    case "turn.completed":
                        HandleTurnCompleted(root);
                        break;
                    // "turn.started", "item.started", and any future/unrecognized type are
                    // deliberately ignored — only the completed item/turn carries final content.
                }
            }
        }

        public CodexTranscriptRunSummary BuildSummary() => new(
            _lastAssistantText, _inputTokens, _outputTokens, _threadId,
            [.. _turns.Select(t => t.ToImmutable())]);

        private void HandleItemCompleted(JsonElement root)
        {
            if (!root.TryGetProperty("item", out var item))
                return;
            var itemType = item.TryGetProperty("type", out var t) ? t.ValueString() : null;
            var itemId = item.TryGetProperty("id", out var idProp) ? idProp.ValueString() ?? "" : "";

            switch (itemType)
            {
                case "agent_message":
                    var text = item.TryGetProperty("text", out var txt) ? txt.GetString() : null;
                    _lastAssistantText = text;
                    onActivity?.Invoke(text);

                    if (!_formatConfirmed)
                        return;

                    _turns.Add(new MutableTurn(_turns.Count, text, [.. _pendingToolCalls], [.. _pendingToolResults]));
                    _pendingToolCalls.Clear();
                    _pendingToolResults.Clear();
                    break;

                case "file_change":
                    if (!_formatConfirmed)
                        return;

                    var changesJson = item.TryGetProperty("changes", out var changes) ? changes.GetRawText() : "[]";
                    var status = item.TryGetProperty("status", out var st) ? st.ValueString() ?? "" : "";
                    _pendingToolCalls.Add(new ConversationToolCall(itemId, "file_change", changesJson));
                    _pendingToolResults.Add(new ConversationToolResult(itemId, status, Truncated: false));
                    break;

                case "command_execution":
                    if (!_formatConfirmed)
                        return;

                    var command = item.TryGetProperty("command", out var cmd) ? cmd.GetString() ?? "" : "";
                    var output = item.TryGetProperty("aggregated_output", out var outp) ? outp.GetString() ?? "" : "";
                    _pendingToolCalls.Add(new ConversationToolCall(itemId, "command_execution", JsonSerializer.Serialize(command)));
                    _pendingToolResults.Add(new ConversationToolResult(itemId, output, Truncated: false));
                    break;

                // "reasoning" and any other item type this version doesn't know about are ignored.
            }
        }

        private void HandleTurnCompleted(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage))
                return;
            if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var iv))
                _inputTokens = iv;
            if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var ov))
                _outputTokens = ov;
        }

        private sealed record MutableTurn(
            int CycleNumber, string? AssistantText,
            List<ConversationToolCall> ToolCalls, List<ConversationToolResult> ToolResults)
        {
            public CodexTranscriptTurn ToImmutable() => new(CycleNumber, AssistantText, ToolCalls, ToolResults);
        }
    }
}

file static class CodexJsonElementValueExtensions
{
    public static string? ValueString(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
