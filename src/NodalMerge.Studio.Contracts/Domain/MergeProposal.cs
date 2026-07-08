namespace NodalMerge.Studio.Contracts.Domain;

public enum MergeProposalStatus
{
    Draft,
    ReadyForReview,
    Approved,
    Rejected,
    Merged,

    // Phase 4 slice 11d — automated reviewer holds the proposal while evaluating.
    UnderReview,
    Superseded
}

// A human reviewer's choice of how a Rejected proposal's work unit should be restarted.
// Revise: keep the agent's current file edits on its branch, add a compacted RevisionContext
// artifact summarizing the almost-correct attempt, and let the agent continue from there.
// Revert: wipe the work unit's branch back to its pre-attempt (base/{proposalId}) snapshot and
// restart the goal fresh — no stale diff to anchor on, just the reviewer's steering note.
public enum RestartMode
{
    Revise,
    Revert,
}

public sealed record MergeProposal(
    string ProposalId,
    string SourceBranch,
    string TargetBranch,
    string Goal,
    string Summary,
    string ChangeDescription,
    string? VerificationResults,
    string? RollbackPlan,
    double? Confidence,
    MergeProposalStatus Status,
    string? WorkspaceChanges = null,
    DateTimeOffset? DiffGeneratedAt = null,
    string? AgentId = null,
    string? Model = null,
    string? Provider = null,
    string? SessionId = null,
    string? WorkUnitId = null,
    IReadOnlyList<string>? FilesTouched = null,
    IReadOnlyList<string>? ReconciledFrom = null,
    string? SupersededBy = null,
    bool AutoApplied = false,
    // Set when the proposing agent explicitly asserts no file changes were needed for this task
    // (e.g. "task asked me to verify X already works"). Surfaced to the automated reviewer and
    // human review UI instead of letting an empty diff pass silently as if nothing was claimed.
    string? NoFileChangesJustification = null,
    // Free-text steering note a human reviewer attaches when approving/rejecting via the review
    // panel — distinct from VerificationResults (which holds automated build/test output), so a
    // human's "why" doesn't get mixed in with or overwritten by the automated reviewer's notes.
    string? ReviewNotes = null,
    // Slice 23 — Constraint/Research artifact IDs the automated reviewer explicitly cited as
    // considered when deciding this proposal (whether or not they were violated). Distinct from
    // ArtifactSurfacedPayload's "was it returned in a projection" — this is "did the decision-maker
    // say they looked at it."
    IReadOnlyList<string>? ConsideredArtifactIds = null,
    // True once this proposal's content has actually been written to the real on-disk repo.
    // Distinct from Status==Merged, which only means "landed on its effective target branch" —
    // when WorkspaceOptions.UsePromotionBranch is on, that target is the shared "candidate" staging
    // branch, and disk write-back is deferred to an explicit human PromoteAsync call (see
    // InMemoryMergeService.ApplyAsync/PromoteAsync). Always true immediately upon Merged when
    // promotion is off or bypassed, preserving today's "apply == on disk" behavior for that case.
    bool PromotedToDisk = false,
    // True when this proposal's own ApplyAsync call actually redirected onto the shared
    // "candidate" staging branch (WorkspaceOptions.UsePromotionBranch was on, not bypassed, at
    // that moment). Distinct from PromotedToDisk: a workspace-only session (no repo path
    // configured) never sets PromotedToDisk true regardless of promotion branch, so filtering the
    // promotion queue on "!PromotedToDisk" alone would sweep in every historically merged
    // proposal — including ones merged straight to main before promotion was ever turned on — the
    // instant a human flips the toggle. This field is the actual "did this land on candidate and
    // need an explicit promote step" signal PromoteAsync/the pending endpoint must filter on.
    bool LandedOnCandidateBranch = false)
{
    public IReadOnlyList<string> FilesTouched { get; init; } = FilesTouched ?? [];
    public IReadOnlyList<string> ReconciledFrom { get; init; } = ReconciledFrom ?? [];
    public IReadOnlyList<string> ConsideredArtifactIds { get; init; } = ConsideredArtifactIds ?? [];
}

public static class MergeProposalTransitions
{
    public static bool CanTransition(MergeProposalStatus from, MergeProposalStatus to) =>
        (from, to) switch
        {
            (MergeProposalStatus.Draft, MergeProposalStatus.ReadyForReview) => true,
            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Approved) => true,
            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Rejected) => true,
            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.UnderReview) => true,
            (MergeProposalStatus.UnderReview, MergeProposalStatus.ReadyForReview) => true,
            (MergeProposalStatus.UnderReview, MergeProposalStatus.Rejected) => true,
            // Slice 20b — AgentApproval/Hybrid's inline reviewer terminates here directly,
            // bypassing the ReadyForReview hand-back that Slice 11d's human-facing pre-gate uses.
            (MergeProposalStatus.UnderReview, MergeProposalStatus.Approved) => true,
            (MergeProposalStatus.Approved, MergeProposalStatus.Merged) => true,
            // An Approved proposal whose own ApplyAsync threw before ever reaching Merged (e.g. a
            // candidate-branch conflict detected mid-apply) has nowhere else to go — Restart's
            // reject-then-retry flow needs this edge, since ReadyForReview/UnderReview are already
            // behind it by the time a proposal is Approved.
            (MergeProposalStatus.Approved, MergeProposalStatus.Rejected) => true,

            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Superseded) => true,
            (MergeProposalStatus.Approved, MergeProposalStatus.Superseded) => true,
            // A fanned-out task child's own proposal reaches Merged the moment its TaskReviewPolicy
            // gate clears (see MergeReconciliationService); reconciliation then folds its content
            // into the batch-level reconciled proposal and supersedes the constituent afterward.
            (MergeProposalStatus.Merged, MergeProposalStatus.Superseded) => true,
            _ => false
        };

    public static bool RequiresHumanApproval(MergeProposalStatus status) =>
        status is MergeProposalStatus.ReadyForReview or MergeProposalStatus.Approved;
}
