using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Merge;

namespace NodalMerge.Studio.Merge.Tests;

public class LineDifferTests
{
    [Fact]
    public void Diff_identical_text_produces_no_hunks()
    {
        var hunks = LineDiffer.Diff("a\nb\nc\n", "a\nb\nc\n");
        Assert.Empty(hunks);
    }

    [Fact]
    public void Diff_pure_insertion_anchors_to_preceding_base_line()
    {
        // Insert "x" after base line 2 (between "b" and "c").
        var hunks = LineDiffer.Diff("a\nb\nc\n", "a\nb\nx\nc\n", contextLines: 0);

        var hunk = Assert.Single(hunks);
        Assert.Equal(2, hunk.BeforeStart);
        Assert.Equal(0, hunk.BeforeCount);
        Assert.Equal(3, hunk.AfterStart);
        Assert.Equal(1, hunk.AfterCount);
        var line = Assert.Single(hunk.Lines);
        Assert.Equal(DiffLineKind.Added, line.Kind);
        Assert.Equal("x", line.Text);
    }

    [Fact]
    public void Diff_pure_deletion_reports_removed_base_range()
    {
        var hunks = LineDiffer.Diff("a\nb\nc\n", "a\nc\n", contextLines: 0);

        var hunk = Assert.Single(hunks);
        Assert.Equal(2, hunk.BeforeStart);
        Assert.Equal(1, hunk.BeforeCount);
        Assert.Equal(1, hunk.AfterStart); // anchor: nothing added, points at the preceding kept line
        Assert.Equal(0, hunk.AfterCount);
        var line = Assert.Single(hunk.Lines);
        Assert.Equal(DiffLineKind.Removed, line.Kind);
        Assert.Equal("b", line.Text);
    }

    [Fact]
    public void Diff_separate_changes_far_apart_produce_separate_hunks_with_zero_context()
    {
        var before = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"line{i}")) + "\n";
        var lines = before.TrimEnd('\n').Split('\n');
        lines[0] = "line1-changed";
        lines[9] = "line10-changed";
        var after = string.Join('\n', lines) + "\n";

        var hunks = LineDiffer.Diff(before, after, contextLines: 0);

        Assert.Equal(2, hunks.Count);
        Assert.Equal(1, hunks[0].BeforeStart);
        Assert.Equal(10, hunks[1].BeforeStart);
    }

    [Fact]
    public void Diff_nearby_changes_merge_into_one_hunk_when_gap_fits_context()
    {
        // Two single-line changes 2 lines apart; contextLines: 3 means the padding windows
        // (gap <= 2*contextLines = 6) overlap, so this should merge into a single hunk.
        var before = "a\nb\nc\nd\ne\n";
        var after  = "X\nb\nc\nd\nY\n";

        var hunks = LineDiffer.Diff(before, after, contextLines: 3);

        Assert.Single(hunks);
    }

    [Fact]
    public void Diff_distant_appends_to_same_file_have_non_overlapping_ranges()
    {
        // Two proposals each append a distinct function at opposite ends of the same base file —
        // the scenario slice 14d's conflict check must stop flagging as a false-positive conflict.
        var baseText = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line{i}")) + "\n";

        var p1After = "newFunctionA();\n" + baseText;             // prepend
        var p2After = baseText + "newFunctionB();\n";             // append

        var p1Hunks = LineDiffer.Diff(baseText, p1After, contextLines: 0);
        var p2Hunks = LineDiffer.Diff(baseText, p2After, contextLines: 0);

        var p1Ranges = p1Hunks.Select(h => (h.BeforeStart, h.BeforeStart + Math.Max(h.BeforeCount, 1) - 1));
        var p2Ranges = p2Hunks.Select(h => (h.BeforeStart, h.BeforeStart + Math.Max(h.BeforeCount, 1) - 1));

        var overlaps = p1Ranges.Any(r1 => p2Ranges.Any(r2 => r1.Item1 <= r2.Item2 && r2.Item1 <= r1.Item2));
        Assert.False(overlaps);
    }

    [Fact]
    public void Diff_edits_to_the_same_line_have_overlapping_ranges()
    {
        var baseText = "a\nb\nc\n";
        var p1After = "a\nb-v1\nc\n";
        var p2After = "a\nb-v2\nc\n";

        var p1Hunks = LineDiffer.Diff(baseText, p1After, contextLines: 0);
        var p2Hunks = LineDiffer.Diff(baseText, p2After, contextLines: 0);

        var p1Ranges = p1Hunks.Select(h => (h.BeforeStart, h.BeforeStart + Math.Max(h.BeforeCount, 1) - 1));
        var p2Ranges = p2Hunks.Select(h => (h.BeforeStart, h.BeforeStart + Math.Max(h.BeforeCount, 1) - 1));

        var overlaps = p1Ranges.Any(r1 => p2Ranges.Any(r2 => r1.Item1 <= r2.Item2 && r2.Item1 <= r1.Item2));
        Assert.True(overlaps);
    }

    [Fact]
    public void Diff_handles_null_and_empty_text()
    {
        Assert.Empty(LineDiffer.Diff(null, null));

        var added = LineDiffer.Diff(null, "a\nb\n", contextLines: 0);
        var hunk = Assert.Single(added);
        Assert.Equal(0, hunk.BeforeStart);
        Assert.Equal(0, hunk.BeforeCount);
        Assert.Equal(2, hunk.AfterCount);
    }
}
