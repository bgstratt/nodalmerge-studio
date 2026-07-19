namespace NodalMerge.Studio.Contracts.Domain;

public enum CandidateConflictStatus
{
    Open,
    // A reconciliation work unit (IReconciliationAgentService) has been created for this conflict
    // and hasn't landed yet — set by the candidate-branch adapter so the UI can disable the
    // Reconcile button mid-flight and the trigger itself is idempotent (only fires from Open).
    Reconciling,
    Resolved,
}

// A line-overlap conflict between two independent goals both landing on the shared
// WorkspaceOptions.CandidateBranchId (see InMemoryMergeService.ApplyAsync's checkForDrift, extended
// to cover the candidate-branch case alongside its original fan-out-sibling scope). Distinct from
// RepositoryConflict, which is a different, CAS/RepositoryOperation-level structural-fork concept
// driven by agents' direct file-edit tool calls and repo-import paths — never by the merge-proposal
// apply pipeline, so entangling the two would require fabricating CAS entries for content that was
// never routed through that log.
public sealed record CandidateConflictRecord(
    string ConflictId,
    string ProposalId,           // the losing proposal whose apply was blocked
    string? WorkUnitId,
    IReadOnlyList<string> ConflictingPaths,
    DateTimeOffset DetectedAt,
    CandidateConflictStatus Status = CandidateConflictStatus.Open,
    // The already-landed proposal whose content is currently on candidate for ConflictingPaths —
    // best-effort resolved at record time by scanning for a Merged, LandedOnCandidateBranch
    // proposal whose FilesTouched overlaps. Null if no such proposal could be identified. Feeds
    // IReconciliationAgentService as the second participant alongside ProposalId.
    string? WinningProposalId = null,
    // Slice 6.3a — denormalized at record-creation time from the owning (losing) proposal's
    // owning work unit's RepositoryId (already in scope at InMemoryMergeService.
    // TryApplyAdditivelyAsync's construction site as owningWorkUnit). Null when unresolvable or the
    // record predates 6.3a.
    string? RepositoryId = null);
