using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
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
        (NodalMerge Studio) to find recurring patterns worth turning into durable guidance for
        future work. You are given aggregate statistics and a sample of rejection rationales,
        human steering notes, and review comments from past runs.

        Identify up to 5 patterns with clear supporting evidence from the data (cite counts or
        rates from what you were given). Each one is one of two kinds:
        - "KnowledgeGuideline": an engineering-practice guideline — e.g. "prefer X over Y" or
          "always do Z before proposing." Applies broadly, regardless of which stage is running.
        - "PromptImprovement": guidance specific to one pipeline stage's behavior — e.g. "the
          orchestrator should do X" or "workers should always do Y before proposing a merge." When
          using this kind, set targetStage to whichever single stage the guidance is actually
          about: Orchestrate (routing/fan-out decisions), Plan (decomposing goals into slices),
          Execute (writing code, proposing merges), Review (evaluating proposals before a human
          sees them), or Merge (applying approved merges).

        Call report_findings with your suggestions. If the data doesn't support any clear pattern,
        call report_findings with an empty findings array rather than guessing.
        """;

    private static readonly LlmToolDef ReportFindingsTool = new(
        "report_findings",
        "Report durable findings detected in the provided execution history.",
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
                            kind = new { type = "string", @enum = new[] { "KnowledgeGuideline", "PromptImprovement" }, description = "KnowledgeGuideline for broadly-applicable engineering guidance, PromptImprovement for stage-specific behavior guidance" },
                            title = new { type = "string", description = "Short title, e.g. \"Prefer repository abstraction for persistence access\"" },
                            summary = new { type = "string", description = "1-3 sentence narrative citing the supporting counts/rates from the data" },
                            targetStage = new { type = "string", @enum = new[] { "Orchestrate", "Plan", "Execute", "Review", "Merge" }, description = "Required when kind=PromptImprovement; omit for KnowledgeGuideline" },
                        },
                        required = new[] { "kind", "title", "summary" },
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
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary)) { continue; }

                // Defensive parsing: a missing/garbled kind or targetStage degrades to "treat as a
                // plain knowledge guideline" rather than failing the whole scan.
                var kindText = el.TryGetProperty("kind", out var k) ? k.GetString() : null;
                var kind = Enum.TryParse<FindingKind>(kindText, ignoreCase: true, out var parsedKind)
                    ? parsedKind
                    : FindingKind.KnowledgeGuideline;

                PipelineStage? targetStage = null;
                if (kind == FindingKind.PromptImprovement &&
                    el.TryGetProperty("targetStage", out var ts) &&
                    Enum.TryParse<PipelineStage>(ts.GetString(), ignoreCase: true, out var parsedStage))
                {
                    targetStage = parsedStage;
                }

                // PromptImprovement with no resolvable stage has nowhere to apply guidance —
                // fall back to KnowledgeGuideline rather than proposing an unpromote-able Finding.
                if (kind == FindingKind.PromptImprovement && targetStage is null)
                    kind = FindingKind.KnowledgeGuideline;

                results.Add(new LlmFindingSuggestion(title!, summary!, kind, targetStage));
            }
            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
