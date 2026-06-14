namespace NodalMerge.Studio.Contracts.Domain;

public enum MergeProposalStatus
{
    Draft,
    ReadyForReview,
    Approved,
    Rejected,
    Merged
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
    MergeProposalStatus Status);

public static class MergeProposalTransitions
{
    public static bool CanTransition(MergeProposalStatus from, MergeProposalStatus to) =>
        (from, to) switch
        {
            (MergeProposalStatus.Draft, MergeProposalStatus.ReadyForReview) => true,
            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Approved) => true,
            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Rejected) => true,
            (MergeProposalStatus.Approved, MergeProposalStatus.Merged) => true,
            _ => false
        };

    public static bool RequiresHumanApproval(MergeProposalStatus status) =>
        status is MergeProposalStatus.ReadyForReview or MergeProposalStatus.Approved;
}
