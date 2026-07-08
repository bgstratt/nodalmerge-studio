using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

// Reuses the same IMergeStrategy chain ConflictResolutionService orchestrates for the unrelated
// RepositoryConflict/CAS subsystem (ThreeWay → Ast → LlmAssisted → terminal HumanReview), called
// directly against workspace-branch content instead — no RepositoryConflict record, no
// IRepositoryOpService involvement. See CandidateConflictRecord's doc comment for why the two
// subsystems stay separate.
public sealed class CandidateConflictResolutionService(IEnumerable<IMergeStrategy> strategies)
    : ICandidateConflictResolutionService
{
    public async Task<CandidateConflictResolution> TryResolveAsync(
        string path,
        string? baseContent,
        string? candidateContent,
        string? proposalContent,
        CancellationToken ct = default)
    {
        var context = new MergeContext(
            ConflictId: $"candidate-{Guid.NewGuid():N}",
            RepositoryId: string.Empty,
            Path: path,
            BaseContent: baseContent,
            // ContentA = what's already landed on candidate (the "winner" so far); ContentB = the
            // incoming proposal's own content — same base/A/B convention ConflictResolutionService
            // uses, just sourced from workspace branches instead of CAS blobs.
            ContentA: candidateContent,
            ContentB: proposalContent);

        foreach (var strategy in strategies)
        {
            var result = await strategy.MergeAsync(context, ct).ConfigureAwait(false);
            if (result.Success && result.MergedContent is not null)
                return new CandidateConflictResolution(true, result.MergedContent, result.StrategyName);
        }

        return new CandidateConflictResolution(false, null, FailureReason: "No strategy could auto-resolve.");
    }
}
