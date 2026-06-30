using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// Phase 10 — routes conflict merge requests through LlmClient (same path as agent turns).
// Returns null when credentials are absent, the LLM refuses, or the response is unusable.
internal sealed class LlmMergeProvider(LlmClient llm) : ILlmMergeProvider
{
    private const string SystemPrompt =
        "You are a precise software merge assistant. " +
        "The user will provide the BASE version of a file and two diverged versions (VERSION A and VERSION B). " +
        "Output ONLY the merged file content — no commentary, no fences, no explanations. " +
        "If the conflict cannot be cleanly resolved, output the single word: CONFLICT";

    public async Task<string?> MergeAsync(MergeContext context, CancellationToken ct = default)
    {
        var creds = context.LlmCredentials;
        if (creds is null) return null;

        var userContent = BuildPrompt(context);
        var messages = new List<NmMessage>
        {
            new("user", [new NmText(userContent)])
        };

        LlmResponse response;
        try
        {
            response = await llm.SendAsync(
                creds.Provider, creds.Model, creds.BaseUrl, creds.ApiKey,
                messages, [], SystemPrompt, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return null;
        }

        var text = response.Content.OfType<NmText>().FirstOrDefault()?.Text?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Equals("CONFLICT", StringComparison.OrdinalIgnoreCase))
            return null;

        return text;
    }

    private static string BuildPrompt(MergeContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"File: {ctx.Path}");
        sb.AppendLine();
        sb.AppendLine("=== BASE ===");
        sb.AppendLine(ctx.BaseContent ?? "(file did not exist)");
        sb.AppendLine();
        sb.AppendLine("=== VERSION A ===");
        sb.AppendLine(ctx.ContentA ?? "(deleted)");
        sb.AppendLine();
        sb.AppendLine("=== VERSION B ===");
        sb.AppendLine(ctx.ContentB ?? "(deleted)");
        sb.AppendLine();
        sb.AppendLine("Produce the merged file now:");
        return sb.ToString();
    }
}
