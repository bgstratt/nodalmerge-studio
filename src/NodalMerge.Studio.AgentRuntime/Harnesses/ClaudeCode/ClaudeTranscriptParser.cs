using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.1 (phase-c-implementation.md C1.a) — the versioned
// stream-json parser extracted out of ClaudeCodeExecutor.RunAsync's inline stdout switch. The
// executor's stdout loop becomes a thin pump: feed each line to Accept, invoke OnActivity when
// Accept says a new assistant activity string arrived, then call BuildSummary once the process
// exits. No plugin framework — see this file's own V1 nested class for why.

// One harness "turn" as reconstructed from the transcript: one assistant message, plus whatever
// tool_result content a later `user` event contributes for the tool_use ids that message emitted.
internal sealed record ClaudeTranscriptTurn(
    int CycleNumber,
    string? AssistantText,
    IReadOnlyList<ConversationToolCall> ToolCalls,
    IReadOnlyList<ConversationToolResult> ToolResults,
    string? Model,
    string StopReason);

// Everything the terminal `result` stream-json event (plus whatever turns were reconstructed along
// the way) yields for one harness run. Field shapes mirror HarnessRunResult's own doc comment
// (grounded in the real `claude -p --output-format stream-json` capture from Phase B2).
internal sealed record TranscriptRunSummary(
    string? ResultText,
    string? Subtype,
    bool IsError,
    int? InputTokens,
    int? OutputTokens,
    double? TotalCostUsd,
    string? SessionId,
    IReadOnlyList<string> PermissionDenials,
    IReadOnlyList<ClaudeTranscriptTurn> Turns);

internal interface IClaudeTranscriptParser
{
    // Feeds one raw stdout line. Never throws — a malformed/partial line, or a line whose shape
    // doesn't match the detected format, is skipped rather than failing the run (same posture the
    // inline parser it replaces already had).
    void Accept(string line);

    TranscriptRunSummary BuildSummary();
}

// Selects the concrete parser for the detected stream-json format. Today there is exactly one
// known format (claude 2.1.177, captured 2026-07-12 per ClaudeCodeExecutor's own header comment),
// so this is a single always-on V1 instance — no version-sniffing buffer, no plugin registry. The
// `V1` naming mirrors the DTO/tool-name versioning already used elsewhere (CapabilitiesV1,
// StudioNodeKind.ConversationLogV1): a version-suffixed class, not a framework. When a second
// format needs supporting, add `V2` here and pick between them from the `system`/`init` event's
// shape, same as this file's own internal degrade check already does for "recognized vs not".
internal static class ClaudeTranscriptParser
{
    public static IClaudeTranscriptParser Create(Action<string?>? onActivity = null) => new V1(onActivity);

    // V1 — claude 2.1.177's stream-json shape. `assistant` events carry one message with
    // text/tool_use content blocks; `user` events carry tool_result blocks addressed back to a
    // tool_use id by `tool_use_id`; `result` is the one terminal line with run-level
    // cost/usage/session/permission-denial fields; `system`/`init` is the version marker.
    //
    // Degrade rule: turn-level reconstruction (assistant/user -> ClaudeTranscriptTurn) only turns
    // on once a recognized `system`/`init` line has been seen — an unrecognized/missing init line
    // means the rest of the stream might not be this format at all, so turns stay empty and only
    // the terminal `result` line's scalars (parsed independently, exactly as before C1) populate
    // the summary. This is "degrade to run-level telemetry", never a thrown exception.
    internal sealed class V1(Action<string?>? onActivity) : IClaudeTranscriptParser
    {
        private readonly List<MutableTurn> _turns = [];
        private readonly Dictionary<string, MutableTurn> _turnByToolUseId = new(StringComparer.Ordinal);
        private readonly List<string> _permissionDenials = [];

        private bool _formatConfirmed;
        private string? _sessionId;
        private string? _resultText;
        private string? _subtype;
        private bool _isError;
        private int? _inputTokens;
        private int? _outputTokens;
        private double? _totalCostUsd;

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

                // session_id appears on every stream-json line (confirmed against the real CLI in
                // B2) — capture it regardless of type, same as the code this replaces.
                if (root.TryGetProperty("session_id", out var sid) && sid.ValueKind == JsonValueKind.String)
                    _sessionId = sid.GetString();

                switch (type)
                {
                    case "system":
                        HandleSystem(root);
                        break;
                    case "assistant":
                        HandleAssistant(root);
                        break;
                    case "user":
                        HandleUser(root);
                        break;
                    case "result":
                        HandleResult(root);
                        break;
                    // "rate_limit_event" and any future/unrecognized type are deliberately
                    // ignored, same as before C1.
                }
            }
        }

        public TranscriptRunSummary BuildSummary() => new(
            _resultText, _subtype, _isError, _inputTokens, _outputTokens, _totalCostUsd,
            _sessionId, _permissionDenials,
            [.. _turns.Select(t => t.ToImmutable())]);

        private void HandleSystem(JsonElement root)
        {
            // The recognized-format marker: a `system` event whose subtype is `init`. Anything
            // else under `type:"system"` (or a stream that never emits this at all) leaves turn
            // reconstruction off — see this class's own doc comment on the degrade rule.
            if (root.TryGetProperty("subtype", out var subtype) && subtype.ValueString() == "init")
                _formatConfirmed = true;
        }

        private void HandleAssistant(JsonElement root)
        {
            var text = ExtractAssistantText(root);
            onActivity?.Invoke(text);

            if (!_formatConfirmed)
                return;

            if (!root.TryGetProperty("message", out var message))
                return;

            var model = message.TryGetProperty("model", out var m) ? m.ValueString() : null;
            var stopReason = message.TryGetProperty("stop_reason", out var sr) ? sr.ValueString() : null;

            var toolCalls = new List<ConversationToolCall>();
            var turn = new MutableTurn(_turns.Count, text, toolCalls, model, stopReason ?? "end_turn");

            if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            {
                foreach (var block in content.EnumerateArray())
                {
                    if (block.TryGetProperty("type", out var t) && t.ValueString() == "tool_use" &&
                        block.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                    {
                        var id = idProp.GetString()!;
                        var name = block.TryGetProperty("name", out var n) ? n.ValueString() ?? "" : "";
                        var inputJson = block.TryGetProperty("input", out var input) ? input.GetRawText() : "{}";
                        toolCalls.Add(new ConversationToolCall(id, name, inputJson));
                        _turnByToolUseId[id] = turn;
                    }
                }
            }

            _turns.Add(turn);
        }

        private void HandleUser(JsonElement root)
        {
            if (!_formatConfirmed)
                return;

            if (!root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                return;

            foreach (var block in content.EnumerateArray())
            {
                if (!block.TryGetProperty("type", out var t) || t.ValueString() != "tool_result")
                    continue;
                if (!block.TryGetProperty("tool_use_id", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                    continue;

                var id = idProp.GetString()!;
                if (!_turnByToolUseId.TryGetValue(id, out var owningTurn))
                    continue; // a tool_result with no matching tool_use in this run — drop it.

                var isError = block.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                var resultText = ExtractToolResultText(block);
                owningTurn.ToolResults.Add(new ConversationToolResult(id, resultText, Truncated: false));
                _ = isError; // ConversationToolResult has no error flag today — content carries it.
            }
        }

        private void HandleResult(JsonElement root)
        {
            _subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
            _isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
            _resultText = root.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString() : null;
            if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                _totalCostUsd = cost.GetDouble();
            if (root.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input_tokens", out var it) && it.TryGetInt32(out var iv))
                    _inputTokens = iv;
                if (usage.TryGetProperty("output_tokens", out var ot) && ot.TryGetInt32(out var ov))
                    _outputTokens = ov;
            }
            if (root.TryGetProperty("permission_denials", out var denials) &&
                denials.ValueKind == JsonValueKind.Array)
            {
                foreach (var denial in denials.EnumerateArray())
                    _permissionDenials.Add(denial.GetRawText());
            }
        }

        private static string? ExtractAssistantText(JsonElement root)
        {
            if (!root.TryGetProperty("message", out var message) ||
                !message.TryGetProperty("content", out var content) ||
                content.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var block in content.EnumerateArray())
            {
                if (block.TryGetProperty("type", out var t) && t.ValueString() == "text" &&
                    block.TryGetProperty("text", out var text))
                    return text.GetString();
            }

            return null;
        }

        // tool_result content can be a plain string or an array of content blocks (the real
        // Anthropic message shape allows both) — prefer a string body, else join any text blocks,
        // else fall back to the raw JSON so nothing is silently dropped.
        private static string ExtractToolResultText(JsonElement toolResultBlock)
        {
            if (!toolResultBlock.TryGetProperty("content", out var content))
                return "";

            if (content.ValueKind == JsonValueKind.String)
                return content.GetString() ?? "";

            if (content.ValueKind == JsonValueKind.Array)
            {
                var texts = content.EnumerateArray()
                    .Where(b => b.TryGetProperty("type", out var t) && t.ValueString() == "text")
                    .Select(b => b.TryGetProperty("text", out var txt) ? txt.GetString() : null)
                    .Where(s => s is not null);
                var joined = string.Join("\n", texts);
                return joined.Length > 0 ? joined : content.GetRawText();
            }

            return content.GetRawText();
        }

        private sealed record MutableTurn(
            int CycleNumber, string? AssistantText, List<ConversationToolCall> ToolCalls,
            string? Model, string StopReason)
        {
            public List<ConversationToolResult> ToolResults { get; } = [];

            public ClaudeTranscriptTurn ToImmutable() => new(
                CycleNumber, AssistantText, ToolCalls, ToolResults, Model, StopReason);
        }
    }
}

file static class JsonElementValueExtensions
{
    public static string? ValueString(this JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() : null;
}
