using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime;

// plans/phase-d-implementation.md D2 — "who plans this goal" executor routing. Sibling to
// LlmProfileSelectionService (Slice 12d/14c), applied to the Plan stage instead of fanned-out
// Execute children: a deterministic FileScope-pattern tier (same "every fileScope path covered by
// exactly one profile's declared FileScopePatterns" rule FanOutService.TryMatchFileScopeProfileAsync
// applies, here matched against Plan-stage profiles) checked first, an opt-in LLM tier using
// goal-text heuristics second — no new routing-table data source, per the plan's explicit v1 scope.
// Both tiers live in this one service (unlike the Execute path, where the deterministic tier is the
// caller's — FanOutService's — job) because there is no existing "fan-out"-shaped caller for planner
// routing to split the tiers across; OrchestratorAgentLoop just wants one answer.
//
// Off by default (WorkspaceOptions.UsePlannerExecutorSelection) — when false this returns the
// requested "planner" profile with no provider override immediately, without even listing agent
// profiles, so the Plan-stage enqueue path is a no-op until an operator opts in. Only ever consulted
// by the caller when the role's Agent Topology assignment for PipelineStage.Plan is unset — an
// explicit per-stage Model Profile assignment bypasses this service entirely (see
// OrchestratorAgentLoop.InjectSpawnCredentialsAsync).
internal sealed class PlannerSelectionService(
    LlmClient llm,
    IAgentProfileService profiles,
    IHarnessExecutorResolver executorResolver,
    WorkspaceOptions options) : IPlannerSelectionService
{
    private const string HeuristicProfileId = "planner";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private const string SystemPrompt =
        "You select which agent profile should author the decomposition (Plan stage) for a goal " +
        "in a software engineering pipeline. Respond with only a single-line JSON object — no " +
        "markdown, no extra text: {\"profileId\": \"<id>\", \"explanation\": \"<short reason>\"}.";

    public async Task<ProfileSelectionResult> SelectPlannerAsync(
        WorkUnit goalUnit, GoalDefaultCredentials? credentials, CancellationToken ct = default)
    {
        if (!options.UsePlannerExecutorSelection)
            return Heuristic("Planner executor selection is disabled; using the requested planner profile.");

        var available = (await profiles.ListAsync(ct).ConfigureAwait(false))
            .Where(p => p.Stage == PipelineStage.Plan)
            .ToList();
        if (available.Count == 0)
            return Heuristic("No Plan-stage agent profiles registered; using the requested planner profile.");

        // Deterministic tier — zero or multiple matches fall through to the LLM/heuristic tier
        // unchanged, same "exactly one match routes deterministically" rule Slice 14c established.
        if (goalUnit.FileScope.Count > 0)
        {
            var matches = available
                .Where(p => p.FileScopePatterns.Count > 0)
                .Where(p => goalUnit.FileScope.All(path => p.FileScopePatterns.Any(pattern =>
                    AgentWorkspaceService.MatchesGlob(pattern.Replace('\\', '/'), path.Replace('\\', '/')))))
                .ToList();
            if (matches.Count == 1)
            {
                return ForSelectedProfile(
                    matches[0],
                    $"fileScope matched profile '{matches[0].AgentProfileId}' declared FileScopePatterns",
                    usedLlm: false);
            }
        }

        if (credentials is null)
            return Heuristic("No LLM credentials available for planner selection; using the requested planner profile.");

        string? text;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);

            var prompt = BuildPrompt(goalUnit, available);
            var response = await llm.SendAsync(
                credentials.Provider, credentials.Model, credentials.BaseUrl, credentials.ApiKey,
                [new NmMessage("user", [new NmText(prompt)])],
                [], SystemPrompt, cts.Token).ConfigureAwait(false);

            text = response.Content.OfType<NmText>().Select(c => c.Text).FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return Heuristic($"LLM planner selection call failed ({ex.GetType().Name}); using the requested planner profile.");
        }

        if (text is null || !TryParseSelection(text, out var profileId, out var explanation))
            return Heuristic("LLM response was not a valid planner selection; using the requested planner profile.");

        var selected = available.FirstOrDefault(p => string.Equals(p.AgentProfileId, profileId, StringComparison.Ordinal));
        if (selected is null)
            return Heuristic($"LLM selected unknown profile '{profileId}'; using the requested planner profile.");

        return ForSelectedProfile(selected, $"LLM selected {selected.AgentProfileId}: {explanation}", usedLlm: true);
    }

    // Provider rides the same channel every other executor-routing decision uses: the selected
    // profile's declared Executor, resolved to that executor's ProviderKey (null for native — see
    // ProfileSelectionResult's own doc comment on why null means "no override" there too).
    private ProfileSelectionResult ForSelectedProfile(AgentProfile selected, string reason, bool usedLlm) =>
        new(selected.AgentProfileId, reason, usedLlm, executorResolver.Resolve(selected.Executor).ProviderKey);

    private static ProfileSelectionResult Heuristic(string reason) =>
        new(HeuristicProfileId, reason, UsedLlm: false, Provider: null);

    private static string BuildPrompt(WorkUnit goalUnit, IReadOnlyList<AgentProfile> available)
    {
        var profileLines = available.Select(p => $"- {p.AgentProfileId} (stage: {p.Stage}): {Excerpt(p.SystemPrompt)}");
        var fileScope = goalUnit.FileScope.Count > 0 ? string.Join(", ", goalUnit.FileScope) : "(none specified)";
        return
            $"""
            Goal to plan: {goalUnit.Goal}
            File scope: {fileScope}

            Available profiles:
            {string.Join("\n", profileLines)}

            Reply with ONLY the JSON object described in the system prompt.
            """;
    }

    private static string Excerpt(string systemPrompt) =>
        string.IsNullOrWhiteSpace(systemPrompt)
            ? "(no system prompt)"
            : systemPrompt.Length <= 160 ? systemPrompt : systemPrompt[..160] + "...";

    private static bool TryParseSelection(string text, out string profileId, out string explanation)
    {
        profileId = "";
        explanation = "";

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
            return false;

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            if (!doc.RootElement.TryGetProperty("profileId", out var idProp) || idProp.ValueKind != JsonValueKind.String)
                return false;

            profileId = idProp.GetString() ?? "";
            explanation = doc.RootElement.TryGetProperty("explanation", out var exProp)
                && exProp.ValueKind == JsonValueKind.String
                ? exProp.GetString() ?? ""
                : "";
            return profileId.Length > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
