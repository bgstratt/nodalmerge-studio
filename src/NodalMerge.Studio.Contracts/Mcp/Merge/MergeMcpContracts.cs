namespace NodalMerge.Studio.Contracts.Mcp.Merge;

public sealed record MergeProposeRequest(
    string SourceBranch,
    string TargetBranch,
    string Summary,
    string? Goal = null,
    string? ChangeDescription = null,
    IReadOnlyList<string>? VerificationResults = null);

public sealed record MergeValidateRequest(string ProposalId);

public sealed record MergeReviewRequest(
    string ProposalId,
    string Decision,
    string? Reviewer = null,
    string? Notes = null);

public sealed record MergeApplyRequest(string ProposalId);

public sealed record MergeProposeResponse(string ProposalId);
