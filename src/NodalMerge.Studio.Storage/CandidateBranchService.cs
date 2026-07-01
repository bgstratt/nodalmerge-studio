using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 21a — ensures the candidate branch exists whenever UsePromotionBranch is on.
// Runs as IRehydratable so it fires after RuntimeSettingsService restores the toggle.
public sealed class CandidateBranchService(IBranchService branches, WorkspaceOptions options) : IRehydratable
{
    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        if (!options.UsePromotionBranch)
            return;

        await EnsureAsync(ct).ConfigureAwait(false);
    }

    // Called by the REST handler when UsePromotionBranch is toggled on at runtime.
    public async Task EnsureAsync(CancellationToken ct = default)
    {
        var existing = await branches.ListBranchesAsync(ct).ConfigureAwait(false);
        if (!existing.Contains(options.CandidateBranchId, StringComparer.Ordinal))
            await branches.CreateBranchAsync(options.CandidateBranchId, cancellationToken: ct).ConfigureAwait(false);
    }
}
