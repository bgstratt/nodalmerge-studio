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
/// <c>Hunks</c> (slice 14d) is the same <c>LineDiffer.Diff</c> output the merge-conflict
/// line-range check uses, just with default context padding for display — Before/AfterContent
/// are kept alongside it since "Open Diff in Editor" still wants the full text for vscode.diff.
/// </summary>
public sealed record ProposalFileChange(
    string Path,
    FileChangeKind ChangeKind,
    string? BeforeContent,
    string? AfterContent,
    IReadOnlyList<DiffHunk> Hunks);
