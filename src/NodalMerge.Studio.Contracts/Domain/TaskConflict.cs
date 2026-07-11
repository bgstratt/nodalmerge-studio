namespace NodalMerge.Studio.Contracts.Domain;

public enum TaskConflictStatus
{
    Open,
    // A reconciliation work unit (IReconciliationAgentService) has been created for this conflict
    // and hasn't landed yet — mirrors CandidateConflictStatus.Reconciling.
    Reconciling,
    Resolved,
}

// A line-overlap conflict between two fan-out siblings both landing on the same
// merge/{ParentWorkUnitId} scratch branch (see InMemoryMergeService.ApplyAsync's checkForDrift —
// the same drift check that covers the candidate-branch case, here scoped to its original
// fan-out-sibling case instead). Distinct from CandidateConflictRecord: siblings are different
// TASKS decomposed from the SAME goal (ideally non-overlapping file scope — a conflict here usually
// means the planner mis-scoped two slices), not independent top-level goals, so the resolution also
// has to satisfy MergeReconciliationService.TryReconcileAsync's fold step, not just land additively
// on a shared branch — see that method's own comment on how it follows a superseded proposal's
// SupersededBy pointer to a reconciliation proposal instead of treating it as still-blocking.
public sealed record TaskConflictRecord(
    string ConflictId,
    string ParentWorkUnitId,
    string ProposalId,           // the losing sibling's proposal whose apply was blocked
    string? WorkUnitId,
    IReadOnlyList<string> ConflictingPaths,
    DateTimeOffset DetectedAt,
    TaskConflictStatus Status = TaskConflictStatus.Open,
    // The already-landed sibling proposal whose content is currently on merge/{ParentWorkUnitId}
    // for ConflictingPaths — best-effort resolved at record time, same approach as
    // CandidateConflictRecord.WinningProposalId. Feeds IReconciliationAgentService as the second
    // participant alongside ProposalId.
    string? WinningProposalId = null);
