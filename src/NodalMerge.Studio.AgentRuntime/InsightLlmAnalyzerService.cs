using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// Backs IInsightLlmAnalyzerService (Core.Services) — lives here, not in Host or Storage, because
// it needs LlmClient/NmMessage/NmToolUse/LlmToolDef directly, and those are internal to this
// assembly. Gets structured output the same way every agent loop does: a forced tool call, parsed
// from NmToolUse.Input, not free-text JSON parsing.
internal sealed class InsightLlmAnalyzerService(LlmClient llm) : IInsightLlmAnalyzerService
{
    private const string SystemPrompt = """
        You are analyzing historical execution data from an AI agent orchestration system
        (NodalMerge Studio) to find recurring patterns that should become durable engineering
        guidelines for future work. You are given aggregate statistics and a sample of rejection
        rationales, human steering notes, and review comments from past runs.

        Identify up to 5 patterns with clear supporting evidence from the data (cite counts or
        rates from what you were given). Only suggest guidelines about engineering practices,
        architecture, libraries, or process — for example "prefer X over Y" or "always do Z before
        proposing." Do NOT suggest changes to AI prompts, instructions, or agent behavior — that
        capability doesn't exist yet and any such suggestion would have nowhere to go.

        Call report_findings with your suggestions. If the data doesn't support any clear pattern,
        call report_findings with an empty findings array rather than guessing.
        """;

    private static readonly LlmToolDef ReportFindingsTool = new(
        "report_findings",
        "Report durable engineering-guideline findings detected in the provided execution history.",
        new
        {
            type = "object",
            properties = new
            {
                findings = new
                {
                    type = "array",
                    items = new
                    {
                        type = "object",
                        properties = new
                        {
                            title = new { type = "string", description = "Short guideline title, e.g. \"Prefer repository abstraction for persistence access\"" },
                            summary = new { type = "string", description = "1-3 sentence narrative citing the supporting counts/rates from the data" },
                        },
                        required = new[] { "title", "summary" },
                    },
                },
            },
            required = new[] { "findings" },
        });

    public async Task<IReadOnlyList<LlmFindingSuggestion>> AnalyzeAsync(
        InsightLlmScanRequest request, CancellationToken ct = default)
    {
        var messages = new List<NmMessage>
        {
            new("user", [new NmText(request.ContextText)]),
        };

        var response = await llm.SendAsync(
            request.Provider, request.Model, request.BaseUrl, request.ApiKey,
            messages, [ReportFindingsTool], SystemPrompt, ct).ConfigureAwait(false);

        // The model replying with plain text instead of calling the tool is a "no findings this
        // time" outcome for the caller, not an error.
        var toolUse = response.Content.OfType<NmToolUse>().FirstOrDefault(t => t.Name == "report_findings");
        if (toolUse is null) { return []; }

        try
        {
            if (!toolUse.Input.TryGetProperty("findings", out var findingsEl) || findingsEl.ValueKind != JsonValueKind.Array)
                return [];

            var results = new List<LlmFindingSuggestion>();
            foreach (var el in findingsEl.EnumerateArray())
            {
                var title = el.TryGetProperty("title", out var t) ? t.GetString() : null;
                var summary = el.TryGetProperty("summary", out var s) ? s.GetString() : null;
                if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(summary))
                    results.Add(new LlmFindingSuggestion(title!, summary!));
            }
            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
