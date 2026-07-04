using Microsoft.Extensions.Logging;

namespace NodalMerge.Studio.AgentRuntime;

// Phase 3.2 — keeps a work unit's resent message history from growing unbounded across a long
// agent-loop run. This is the root cause behind the harness-comparison-eval's 1.59M-input-token
// session: messages is a single list that only ever grows, and every SendAsync call resends it in
// full. Two mechanisms, applied every iteration before the next SendAsync:
//   1. Tool-result elision (always on, no LLM call) — old, superseded tool-result bodies are
//      replaced with a short placeholder instead of being resent verbatim every cycle.
//   2. Rolling summary (fixed turn-count trigger, one extra LLM call when it fires) — once
//      history crosses a threshold, everything older than the trailing window is condensed into a
//      single recap message.
// See plans/orchestrator-reliability-and-observability.md Phase 3 item 2 for the design tradeoffs.
internal static class ConversationCompactor
{
    private const int ElideAfterMessagesOld = 8; // keep the last 8 messages' tool results verbatim
    private const int ElideMinChars = 400;       // don't bother eliding short results
    private const int SummaryTriggerCount = 20;  // messages.Count threshold that fires a summary
    private const int SummaryTailKeep = 6;       // messages kept verbatim after summarizing

    private const string SummarizationSystemPrompt =
        "Summarize this in-progress coding agent session for continuation. Cover: the task/goal, " +
        "files read or modified and their current state, decisions or constraints discovered, and " +
        "what remains to be done. Do not include full file contents or tool call arguments verbatim " +
        "— paraphrase. Be concise (under 300 words). Respond with plain text only, no tool calls.";

    // Replaces tool-result bodies older than the trailing window with a short placeholder,
    // in place. Assistant turns (the model's own reasoning/tool-call decisions) are never touched
    // — only the raw tool-result inputs that fed them, which is where a long session's resent
    // token volume actually lives. logger/agentId/workUnitId are purely for observability (see
    // plans/orchestrator-reliability-and-observability.md — confirming this actually fires on a
    // real run previously required inferring it from token-count curves instead of a log line);
    // both are optional and no-op when omitted (e.g. call sites/tests that don't wire a logger).
    public static void ElideStaleToolResults(
        List<NmMessage> messages, ILogger? logger = null, string? agentId = null, string? workUnitId = null)
    {
        if (messages.Count <= ElideAfterMessagesOld + 1)
            return;

        var toolNameById = new Dictionary<string, string>();
        var cutoff = messages.Count - ElideAfterMessagesOld;
        var elidedCount = 0;
        var elidedChars = 0;

        for (var i = 0; i < cutoff; i++)
        {
            var message = messages[i];

            foreach (var block in message.Content)
                if (block is NmToolUse toolUse)
                    toolNameById[toolUse.Id] = toolUse.Name;

            if (message.Role != "user")
                continue;

            List<NmContent>? rewritten = null;
            for (var b = 0; b < message.Content.Count; b++)
            {
                if (message.Content[b] is not NmToolResult toolResult) continue;
                if (toolResult.Result.Length < ElideMinChars) continue;
                if (toolResult.Result.StartsWith("[elided", StringComparison.Ordinal)) continue;

                rewritten ??= [.. message.Content];
                var name = toolNameById.GetValueOrDefault(toolResult.ToolUseId, "tool");
                elidedCount++;
                elidedChars += toolResult.Result.Length;
                rewritten[b] = toolResult with
                {
                    Result = $"[elided — {name} result, {toolResult.Result.Length} chars, superseded by later turns]"
                };
            }

            if (rewritten is not null)
                messages[i] = message with { Content = rewritten };
        }

        if (elidedCount > 0)
        {
            logger?.LogInformation(
                "[Compactor] Elided {ElidedCount} stale tool result(s) ({ElidedChars} chars) for " +
                "agent {AgentId} / work unit {WorkUnitId} — {MessageCount} messages in history.",
                elidedCount, elidedChars, agentId, workUnitId, messages.Count);
        }
    }

    // Once history crosses SummaryTriggerCount messages, condenses everything after the kickoff
    // message and before the trailing window into one recap, via a dedicated (tool-free) LLM
    // call. Mutates messages in place; no-ops (including on a failed/empty summarization response)
    // rather than risk silently losing history. logger/agentId/workUnitId are observability-only —
    // see ElideStaleToolResults' comment; both optional and no-op when omitted.
    public static async Task ApplyRollingSummaryIfDueAsync(
        List<NmMessage> messages, IAgentToolClient client, CancellationToken ct,
        ILogger? logger = null, string? agentId = null, string? workUnitId = null)
    {
        if (messages.Count <= SummaryTriggerCount)
            return;

        // messages[0] is always the kickoff message (role "user"). After that, roles strictly
        // alternate assistant/user, so the tail must start on an assistant message to preserve
        // alternation once it directly follows the (also "user"-role) kickoff.
        var tailStart = messages.Count - SummaryTailKeep;
        while (tailStart > 1 && messages[tailStart].Role != "assistant")
            tailStart--;

        if (tailStart <= 1)
        {
            logger?.LogInformation(
                "[Compactor] Rolling summary due ({MessageCount} messages) for agent {AgentId} / " +
                "work unit {WorkUnitId} but skipped — no assistant-aligned boundary before the " +
                "trailing window, nothing meaningful to summarize yet.",
                messages.Count, agentId, workUnitId);
            return; // nothing meaningful to summarize
        }

        var toSummarize = messages.Skip(1).Take(tailStart - 1).ToList();
        var tail = messages.Skip(tailStart).ToList();

        var startedAt = DateTimeOffset.UtcNow;
        var summaryResponse = await client.SendAsync(
            toSummarize, [], SummarizationSystemPrompt, ct).ConfigureAwait(false);
        var elapsedMs = (DateTimeOffset.UtcNow - startedAt).TotalMilliseconds;

        var summaryText = string.Concat(summaryResponse.Content.OfType<NmText>().Select(t => t.Text));
        if (string.IsNullOrWhiteSpace(summaryText))
        {
            logger?.LogWarning(
                "[Compactor] Rolling summary call for agent {AgentId} / work unit {WorkUnitId} " +
                "returned no usable text after summarizing {SummarizedCount} turns ({ElapsedMs}ms) " +
                "— keeping full history rather than losing it.",
                agentId, workUnitId, toSummarize.Count, elapsedMs);
            return; // summarization call produced nothing usable — keep full history rather than lose it
        }

        // Folded into the kickoff message itself (same fold-into-single-NmText convention as
        // OrchestratorAgentLoop's AppendDeltaToOutgoingMessage) rather than inserted as its own
        // "user" message — a standalone recap message would sit directly after the also-"user"
        // kickoff and break strict user/assistant alternation.
        var recapText = $"[Compacted summary of {toSummarize.Count} earlier turns, replacing full " +
            $"history to keep this session's context bounded]\n{summaryText}";
        var kickoff = messages[0];
        IReadOnlyList<NmContent> newKickoffContent = kickoff.Content is [NmText only]
            ? [new NmText($"{only.Text}\n\n{recapText}")]
            : [.. kickoff.Content, new NmText(recapText)];

        messages.Clear();
        messages.Add(kickoff with { Content = newKickoffContent });
        messages.AddRange(tail);

        logger?.LogInformation(
            "[Compactor] Applied rolling summary for agent {AgentId} / work unit {WorkUnitId}: " +
            "condensed {SummarizedCount} turns ({SummaryChars} chars) into the kickoff message, " +
            "kept {TailCount} trailing messages verbatim ({ElapsedMs}ms LLM call).",
            agentId, workUnitId, toSummarize.Count, summaryText.Length, tail.Count, elapsedMs);
    }
}
