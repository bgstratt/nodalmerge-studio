using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// Backs IInsightLlmAnalyzerService (Core.Services) — lives here, not in Host or Storage, because
// it needs LlmClient/NmMessage/NmToolUse/LlmToolDef directly, and those are internal to this
// assembly. Gets structured output the same way every agent loop does: a forced tool call, parsed
// from NmToolUse.Input, not free-text JSON parsing.
internal sealed class InsightLlmAnalyzerService(LlmClient llm, IEnumerable<IOneShotCliCompleter> cliCompleters)
    : IInsightLlmAnalyzerService
{
    // Route B (plans/organizational-knowledge-and-workgroup-scope.md) — the CLI has no forced-tool-call
    // channel, so instead of the report_findings tool we instruct the model to emit exactly the JSON
    // that tool's input would have carried, then parse it defensively (same MapFindings as the HTTP
    // path). Kept verbatim-aligned with ReportFindingsTool's schema below.
    private const string CliJsonInstruction = """
        Respond with ONLY a JSON object of this exact shape — no prose, no markdown code fences:
        {"findings":[{"kind":"KnowledgeGuideline"|"PromptImprovement","title":"...","summary":"...","targetStage":"Orchestrate"|"Plan"|"Execute"|"Review"|"Merge"}]}
        Include targetStage only when kind is "PromptImprovement". If the data supports no clear
        pattern, respond with {"findings":[]}.
        """;

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

    public async Task<InsightLlmScanResult> AnalyzeAsync(
        InsightLlmScanRequest request, CancellationToken ct = default)
    {
        // Route B — a CLI provider (claude-cli / codex-cli) has no HTTP baseUrl; dispatch a one-shot
        // CLI completion instead of LlmClient's HTTP call. Pick the completer the same way goals pick
        // an executor: by matching the request's provider to the completer's ProviderKey.
        var cli = cliCompleters.FirstOrDefault(
            c => string.Equals(c.ProviderKey, request.Provider, StringComparison.OrdinalIgnoreCase));
        return cli is not null
            ? await AnalyzeViaCliAsync(cli, request, ct).ConfigureAwait(false)
            : new InsightLlmScanResult(await AnalyzeViaHttpAsync(request, ct).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<LlmFindingSuggestion>> AnalyzeViaHttpAsync(
        InsightLlmScanRequest request, CancellationToken ct)
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
            return MapFindings(toolUse.Input);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private async Task<InsightLlmScanResult> AnalyzeViaCliAsync(
        IOneShotCliCompleter cli, InsightLlmScanRequest request, CancellationToken ct)
    {
        var userPrompt = request.ContextText + "\n\n" + CliJsonInstruction;
        string text;
        try
        {
            text = await cli.CompleteAsync(
                new OneShotCliRequest(request.Model, request.ApiKey, SystemPrompt, userPrompt), ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Spawn/timeout/nonzero-exit — no findings this run. Surface the error text as the raw
            // output so the UI can show why the scan produced nothing.
            return new InsightLlmScanResult([], "CLI scan failed: " + ex.Message);
        }

        // Always carry the model's verbatim response back (RawCliOutput) — that's what lets the UI
        // show the user what the model actually returned when nothing parsed.
        var json = CliProcessRunner.ExtractJsonObject(text);
        if (json is null) { return new InsightLlmScanResult([], text); }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return new InsightLlmScanResult(MapFindings(doc.RootElement), text);
        }
        catch (JsonException)
        {
            return new InsightLlmScanResult([], text);
        }
    }

    // Shared mapping of a { "findings": [ ... ] } object into suggestions — used by both the HTTP
    // forced-tool-call input and the CLI's parsed JSON output. Defensive: a missing/garbled kind or
    // targetStage degrades to a plain knowledge guideline rather than failing the whole scan.
    private static IReadOnlyList<LlmFindingSuggestion> MapFindings(JsonElement root)
    {
        if (!root.TryGetProperty("findings", out var findingsEl) || findingsEl.ValueKind != JsonValueKind.Array)
            return [];

        var results = new List<LlmFindingSuggestion>();
        foreach (var el in findingsEl.EnumerateArray())
        {
            var title = el.TryGetProperty("title", out var t) ? t.GetString() : null;
            var summary = el.TryGetProperty("summary", out var s) ? s.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(summary)) { continue; }

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

            // PromptImprovement with no resolvable stage has nowhere to apply guidance — fall back to
            // KnowledgeGuideline rather than proposing an unpromote-able Finding.
            if (kind == FindingKind.PromptImprovement && targetStage is null)
                kind = FindingKind.KnowledgeGuideline;

            results.Add(new LlmFindingSuggestion(title!, summary!, kind, targetStage));
        }
        return results;
    }
}
