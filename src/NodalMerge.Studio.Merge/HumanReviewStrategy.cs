using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

// Phase 10 — terminal strategy: always signals that human review is required. Registered
// last in the chain so it only fires when all automated strategies have failed. The conflict
// stays Open; the UI's merge review panel surfaces it for manual resolution.
public sealed class HumanReviewStrategy : IMergeStrategy
{
    public string Name => "human";

    public Task<MergeStrategyResult> MergeAsync(MergeContext context, CancellationToken ct = default) =>
        Task.FromResult(new MergeStrategyResult(
            false, null, Name, "All automated strategies failed — human review required."));
}
