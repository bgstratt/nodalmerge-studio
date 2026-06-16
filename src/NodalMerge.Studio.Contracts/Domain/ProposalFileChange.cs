namespace NodalMerge.Studio.Contracts.Domain;

public enum FileChangeKind
{
    Added,
    Modified,
    Deleted,
}

/// <summary>
/// Concrete per-file before/after content for human (or automated) review.
/// Base content comes from <c>base/{proposalId}</c>; after content from the proposal source branch.
/// </summary>
public sealed record ProposalFileChange(
    string Path,
    FileChangeKind ChangeKind,
    string? BeforeContent,
    string? AfterContent);
