using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

// Phase 10 — LLM-assisted merge. Sends base + both versions to the LLM and asks it to
// produce a merged result. Falls through when ILlmMergeProvider is unavailable (no API
// credentials configured) or when the model declines to produce a clean merge.
public sealed class LlmAssistedMergeStrategy(ILlmMergeProvider? llmProvider = null) : IMergeStrategy
{
    public string Name => "llm";

    public async Task<MergeStrategyResult> MergeAsync(MergeContext context, CancellationToken ct = default)
    {
        if (llmProvider is null)
            return new MergeStrategyResult(false, null, Name,
                "LLM merge provider not configured — skipping.");

        var merged = await llmProvider.MergeAsync(context, ct).ConfigureAwait(false);
        return merged is not null
            ? new MergeStrategyResult(true, merged, Name)
            : new MergeStrategyResult(false, null, Name, "LLM could not produce a clean merge.");
    }
}
