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
    bool AutoApplied = false)
{
    public IReadOnlyList<string> FilesTouched { get; init; } = FilesTouched ?? [];
    public IReadOnlyList<string> ReconciledFrom { get; init; } = ReconciledFrom ?? [];
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
            (MergeProposalStatus.Approved, MergeProposalStatus.Merged) => true,

            (MergeProposalStatus.ReadyForReview, MergeProposalStatus.Superseded) => true,
            (MergeProposalStatus.Approved, MergeProposalStatus.Superseded) => true,
            _ => false
        };

    public static bool RequiresHumanApproval(MergeProposalStatus status) =>
        status is MergeProposalStatus.ReadyForReview or MergeProposalStatus.Approved;
}
