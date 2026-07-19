using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge;

public sealed class TaskReconciliationTrigger(
    ITaskConflictService taskConflicts,
    IReconciliationAgentService reconciliation,
    IMergeService merge,
    IFileWorkspaceService fileWorkspace,
    IFileLeaseService? fileLease = null,
    IServiceProvider? serviceProvider = null) : ITaskReconciliationTrigger
{
    public async Task<WorkUnit?> TryTriggerAsync(
        string conflictId, string? steeringNotes = null, GoalDefaultCredentials? credentials = null, CancellationToken ct = default)
    {
        // Atomic Open -> Reconciling transition — returns null (no-op) if another caller already
        // started reconciling this conflict, or it's already Resolved.
        var started = await taskConflicts.TryStartReconcilingAsync(conflictId, ct).ConfigureAwait(false);
        if (started is null)
            return null;

        var proposalIds = ResolveProposalIds(started);
        var request = new ReconciliationRequest(
            SeedBranchId: $"merge/{started.ParentWorkUnitId}",
            ProposalIds: proposalIds,
            ConflictingPaths: started.ConflictingPaths,
            SourceRef: $"task-conflict:{started.ConflictId}",
            SteeringNotes: string.IsNullOrWhiteSpace(steeringNotes) ? null : steeringNotes,
            ParentWorkUnitId: started.ParentWorkUnitId,
            Credentials: credentials);

        return await reconciliation.TriggerAsync(request, ct).ConfigureAwait(false);
    }

    public async Task<MergeProposal?> TryResolveManuallyAsync(
        string conflictId, IReadOnlyDictionary<string, string> resolvedContent, CancellationToken ct = default)
    {
        var started = await taskConflicts.TryStartReconcilingAsync(conflictId, ct).ConfigureAwait(false);
        if (started is null)
            return null;

        var missing = started.ConflictingPaths.Where(p => !resolvedContent.ContainsKey(p)).ToList();
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"resolvedContent is missing content for: {string.Join(", ", missing)}.");
        }

        // Written to a dedicated scratch branch, not merge/{ParentWorkUnitId} directly — that branch
        // gets destructively rebuilt from scratch by MergeReconciliationService.TryReconcileAsync's
        // own ApplyBranchAsync reset the next time it runs, so the resolved content needs its own
        // durable home a proposal's SourceBranch can point to, same as any other child's own branch.
        var resolutionBranch = $"task-resolution/{started.ConflictId}";
        foreach (var (path, content) in resolvedContent)
            await fileWorkspace.WriteAsync(resolutionBranch, path, content, ct).ConfigureAwait(false);

        var proposalIds = ResolveProposalIds(started);
        var resolutionId = $"MP-{Guid.NewGuid():N}";
        var resolutionProposal = new MergeProposal(
            resolutionId,
            SourceBranch: resolutionBranch,
            TargetBranch: $"merge/{started.ParentWorkUnitId}",
            Goal: "Manually resolved task-level conflict",
            Summary: $"Human-resolved conflict on: {string.Join(", ", resolvedContent.Keys)}",
            ChangeDescription: $"Combined changes from proposals: {string.Join(", ", proposalIds)}",
            VerificationResults: null, RollbackPlan: null, Confidence: null,
            Status: MergeProposalStatus.Merged,
            DiffGeneratedAt: DateTimeOffset.UtcNow,
            FilesTouched: resolvedContent.Keys.ToList(),
            ReconciledFrom: proposalIds,
            // Slice 6.3a — propagated from the conflict record's own RepositoryId.
            RepositoryId: started.RepositoryId);
        await merge.ProposeAsync(resolutionProposal, ct).ConfigureAwait(false);

        var sourceWorkUnitIds = new List<string?>();
        foreach (var sourceId in proposalIds)
        {
            await merge.SupersedeAsync(sourceId, resolutionId, ct).ConfigureAwait(false);
            sourceWorkUnitIds.Add((await merge.GetAsync(sourceId, ct).ConfigureAwait(false))?.WorkUnitId);
        }

        // Same as the agent-reconcile path — a superseded proposal never goes through the one place
        // (ReviewAsync's Rejected branch) that already releases file leases, so it must happen here.
        await ReconciliationFileLeaseRelease.ReleaseForWorkUnitsAsync(sourceWorkUnitIds, fileLease, serviceProvider, ct)
            .ConfigureAwait(false);

        await taskConflicts.MarkResolvedAsync(conflictId, ct).ConfigureAwait(false);
        return resolutionProposal;
    }

    private static List<string> ResolveProposalIds(TaskConflictRecord conflict)
    {
        var proposalIds = new List<string> { conflict.ProposalId };
        if (conflict.WinningProposalId is { } winningProposalId)
            proposalIds.Add(winningProposalId);
        return proposalIds;
    }
}
