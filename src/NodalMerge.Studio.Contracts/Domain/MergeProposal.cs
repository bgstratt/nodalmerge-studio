namespace NodalMerge.Studio.Contracts.Domain;

public enum MergeProposalStatus
{
    Draft,
    ReadyForReview,
    Approved,
    Rejected,
    Merged,

    // Phase 4 slice 11a. UnderReview is defined but has no wired transitions yet — nothing
    // produces it until 11d's automated-reviewer pre-gate exists.
    UnderReview,
    Superseded
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
    IReadOnlyList<string>? FilesTouched = null)
{
    public IReadOnlyList<string> FilesTouched { get; init; } = FilesTouched ?? [];
}

public static class MergeProposalTransitions
{
    public static bool CanTransition(MergeProposalStatus from, MergeProposalStatus to) =>
        (from, to) switch
        {
            (MergeProposalStatus.Draft, MergeProposalStatus.ReadyForReview) => true,
            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Approved) => true,
            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Rejected) => true,
            (MergeProposalStatus.Approved, MergeProposalStatus.Merged) => true,

            // Phase 4 slice 11c (merger/reducer) will produce Superseded; the transition is
            // defined now so 11c doesn't need to touch this file.
            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Superseded) => true,
            (MergeProposalStatus.Approved, MergeProposalStatus.Superseded) => true,
            _ => false
        };

    public static bool RequiresHumanApproval(MergeProposalStatus status) =>
        status is MergeProposalStatus.ReadyForReview or MergeProposalStatus.Approved;
}
