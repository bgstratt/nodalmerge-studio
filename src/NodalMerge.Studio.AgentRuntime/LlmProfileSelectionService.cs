using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime;

// Slice 12d. Heuristic routing today is simply "every child fanned out from a plan runs the
// worker profile" — there's no separate function to "replace", so this service's heuristic
// fallback reproduces that exact default. The LLM path is purely additive and off by default
// (WorkspaceOptions.UseLlmProfileSelection).
internal sealed class LlmProfileSelectionService(
    LlmClient llm,
    IAgentProfileService profiles,
    WorkspaceOptions options) : IProfileSelectionService
{
    private const string HeuristicProfileId = "worker";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);

    private const string SystemPrompt =
        "You select which agent profile should execute a child work unit in a software " +
        "engineering pipeline. Respond with only a single-line JSON object — no markdown, no " +
        "extra text: {\"profileId\": \"<id>\", \"explanation\": \"<short reason>\"}.";

    public async Task<ProfileSelectionResult> SelectProfileAsync(
        WorkUnit childUnit, OrchestratorCredentials? credentials, CancellationToken ct = default)
    {
        if (!options.UseLlmProfileSelection)
            return Heuristic("LLM profile selection is disabled; using heuristic default.");

        if (credentials is null)
            return Heuristic("No LLM credentials available for profile selection; using heuristic default.");

        var available = await profiles.ListAsync(ct).ConfigureAwait(false);
        if (available.Count == 0)
            return Heuristic("No agent profiles registered; using heuristic default.");

        string? text;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(CallTimeout);

            var prompt = BuildPrompt(childUnit, available);
            var response = await llm.SendAsync(
                credentials.Provider, credentials.Model, credentials.BaseUrl, credentials.ApiKey,
                [new NmMessage("user", [new NmText(prompt)])],
                [], SystemPrompt, cts.Token).ConfigureAwait(false);

            text = response.Content.OfType<NmText>().Select(c => c.Text).FirstOrDefault();
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        {
            return Heuristic($"LLM profile selection call failed ({ex.GetType().Name}); using heuristic default.");
        }

        if (text is null || !TryParseSelection(text, out var profileId, out var explanation))
            return Heuristic("LLM response was not a valid profile selection; using heuristic default.");

        if (!available.Any(p => string.Equals(p.AgentProfileId, profileId, StringComparison.Ordinal)))
            return Heuristic($"LLM selected unknown profile '{profileId}'; using heuristic default.");

        return new ProfileSelectionResult(profileId, $"LLM selected {profileId}: {explanation}", UsedLlm: true);
    }

    private static ProfileSelectionResult Heuristic(string reason) =>
        new(HeuristicProfileId, reason, UsedLlm: false);

    private static string BuildPrompt(WorkUnit childUnit, IReadOnlyList<AgentProfile> available)
    {
        var profileLines = available.Select(p => $"- {p.AgentProfileId} (stage: {p.Stage}): {Excerpt(p.SystemPrompt)}");
        var fileScope = childUnit.FileScope.Count > 0 ? string.Join(", ", childUnit.FileScope) : "(none specified)";
        return
            $"""
            Child work unit goal: {childUnit.Goal}
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
