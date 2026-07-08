using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

// Shared by IReconciliationAgentService and both its adapters (candidate + task). A proposal that
// gets superseded by a reconciliation result never goes through ReviewAsync's Rejected path (the
// one place that already force-releases file leases, per InMemoryMergeService.ReviewAsync's own
// Phase 12 comment) or ApplyAsync's own release-and-resume-on-success path — SupersedeAsync is a
// pure status transition with no lease awareness at all. Without this, the losing (and, harmlessly,
// the already-released winning) goal's file leases on the conflicting path(s) stay held forever,
// blocking the reconciliation work unit itself — or any other future waiter — from ever writing to
// that path. Mirrors InMemoryMergeService.ReviewAsync's exact release-and-clear-waiters pattern.
internal static class ReconciliationFileLeaseRelease
{
    public static async Task ReleaseForWorkUnitsAsync(
        IEnumerable<string?> workUnitIds,
        IFileLeaseService? fileLease,
        IServiceProvider? serviceProvider,
        CancellationToken ct)
    {
        if (fileLease is null)
            return;

        var scheduler = serviceProvider?.GetService(typeof(IWorkScheduler)) as IWorkScheduler;
        foreach (var workUnitId in workUnitIds.Where(id => id is not null).Distinct())
        {
            var promoted = await fileLease.ForceReleaseAllForWorkUnitAsync(workUnitId!, ct).ConfigureAwait(false);
            if (scheduler is null)
                continue;

            foreach (var promotedWorkUnitId in promoted)
                await scheduler.ClearAwaitingFileLeaseAsync(promotedWorkUnitId, ct).ConfigureAwait(false);
        }
    }
}
