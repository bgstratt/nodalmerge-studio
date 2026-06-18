namespace NodalMerge.Studio.Contracts.Domain;

public enum DiffLineKind
{
    Context,
    Added,
    Removed,
}

/// <summary>
/// One line of a diff. <see cref="BeforeLineNumber"/>/<see cref="AfterLineNumber"/> are 1-based
/// and null on the side a line doesn't exist on (e.g. <see cref="DiffLineKind.Added"/> has no
/// <see cref="BeforeLineNumber"/>).
/// </summary>
public sealed record DiffLine(DiffLineKind Kind, int? BeforeLineNumber, int? AfterLineNumber, string Text);

/// <summary>
/// A unified-diff-style hunk. <c>BeforeStart</c>/<c>BeforeCount</c> and
/// <c>AfterStart</c>/<c>AfterCount</c> follow the same convention as a <c>@@ -a,b +c,d @@</c>
/// header — for a pure insertion/deletion (count 0), Start is the anchor line immediately
/// preceding the change. Two hunks' <c>[BeforeStart, BeforeStart + max(BeforeCount, 1) - 1]</c>
/// ranges intersecting is the line-range-overlap conflict check (slice 14d).
/// </summary>
public sealed record DiffHunk(
    int BeforeStart,
    int BeforeCount,
    int AfterStart,
    int AfterCount,
    IReadOnlyList<DiffLine> Lines);
