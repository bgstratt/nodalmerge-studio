using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Merge;

// Real line-level diff (slice 14d) — the first one in the codebase; FileSystemWorkspaceService's
// DiffAsync only ever compared whole-file text equality. Shared by MergeReconciliationService's
// line-range conflict check (contextLines: 0, since only the raw changed ranges matter there) and,
// later, the Merge Review panel's diff display (default contextLines, git-diff-style padding).
// One engine, two consumers, so there's only ever one diff implementation to get right.
public static class LineDiffer
{
    public static IReadOnlyList<DiffHunk> Diff(string? beforeText, string? afterText, int contextLines = 3)
    {
        var before = SplitLines(beforeText);
        var after  = SplitLines(afterText);
        var diffLines = ComputeDiffLines(before, after);
        return BuildHunks(diffLines, contextLines);
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return [];

        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');

        // A trailing newline produces one trailing empty element that isn't a real line.
        return lines.Length > 0 && lines[^1].Length == 0 && normalized.EndsWith('\n')
            ? lines[..^1]
            : lines;
    }

    // LCS-via-DP backtrack rather than the textbook Myers algorithm — easier to verify correct,
    // and the files this tool diffs (one proposal's worth of source changes) are small enough
    // that O(n*m) time/space isn't a real concern. Swap this internal if that stops being true.
    private static List<DiffLine> ComputeDiffLines(string[] before, string[] after)
    {
        var n = before.Length;
        var m = after.Length;
        var lcs = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                lcs[i, j] = before[i] == after[j]
                    ? lcs[i + 1, j + 1] + 1
                    : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var result = new List<DiffLine>(n + m);
        var beforeLine = 0;
        var afterLine  = 0;
        var bi = 0;
        var ai = 0;
        while (bi < n && ai < m)
        {
            if (before[bi] == after[ai])
            {
                beforeLine++; afterLine++;
                result.Add(new DiffLine(DiffLineKind.Context, beforeLine, afterLine, before[bi]));
                bi++; ai++;
            }
            else if (lcs[bi + 1, ai] >= lcs[bi, ai + 1])
            {
                beforeLine++;
                result.Add(new DiffLine(DiffLineKind.Removed, beforeLine, null, before[bi]));
                bi++;
            }
            else
            {
                afterLine++;
                result.Add(new DiffLine(DiffLineKind.Added, null, afterLine, after[ai]));
                ai++;
            }
        }

        while (bi < n)
        {
            beforeLine++;
            result.Add(new DiffLine(DiffLineKind.Removed, beforeLine, null, before[bi]));
            bi++;
        }

        while (ai < m)
        {
            afterLine++;
            result.Add(new DiffLine(DiffLineKind.Added, null, afterLine, after[ai]));
            ai++;
        }

        return result;
    }

    private static List<DiffHunk> BuildHunks(List<DiffLine> diffLines, int contextLines)
    {
        var n = diffLines.Count;
        if (n == 0)
            return [];

        // cursor[i] = before/after line number of the last consumed line strictly before flat
        // position i (0 if none yet) — the anchor a hunk uses for BeforeStart/AfterStart when its
        // own first line is a pure insertion/deletion and so doesn't carry that side's number.
        var beforeCursor = new int[n + 1];
        var afterCursor  = new int[n + 1];
        for (var idx = 0; idx < n; idx++)
        {
            beforeCursor[idx + 1] = diffLines[idx].BeforeLineNumber ?? beforeCursor[idx];
            afterCursor[idx + 1]  = diffLines[idx].AfterLineNumber  ?? afterCursor[idx];
        }

        var hunks = new List<DiffHunk>();
        var i = 0;
        while (i < n)
        {
            if (diffLines[i].Kind == DiffLineKind.Context) { i++; continue; }

            var changeStart = i;
            var changeEnd = changeStart;
            while (changeEnd < n && diffLines[changeEnd].Kind != DiffLineKind.Context) changeEnd++;

            var hunkStart = Math.Max(0, changeStart - contextLines);
            var hunkEnd = changeEnd;

            // Merge in the next change run if the contextLines-padding on both sides of the gap
            // between them would overlap — same rule GNU diff -U uses to avoid emitting two
            // hunks whose context windows share lines.
            while (true)
            {
                var nextChangeStart = hunkEnd;
                while (nextChangeStart < n && diffLines[nextChangeStart].Kind == DiffLineKind.Context) nextChangeStart++;

                if (nextChangeStart < n && nextChangeStart - hunkEnd <= 2 * contextLines)
                {
                    var nextChangeEnd = nextChangeStart;
                    while (nextChangeEnd < n && diffLines[nextChangeEnd].Kind != DiffLineKind.Context) nextChangeEnd++;
                    hunkEnd = nextChangeEnd;
                    continue;
                }

                hunkEnd = Math.Min(n, hunkEnd + contextLines);
                break;
            }

            var hunkLines = diffLines.GetRange(hunkStart, hunkEnd - hunkStart);

            var hasBefore = hunkLines.Exists(l => l.BeforeLineNumber.HasValue);
            var beforeStart = hasBefore ? beforeCursor[hunkStart] + 1 : beforeCursor[hunkStart];
            var beforeCount = hunkLines.Count(l => l.BeforeLineNumber.HasValue);

            var hasAfter = hunkLines.Exists(l => l.AfterLineNumber.HasValue);
            var afterStart = hasAfter ? afterCursor[hunkStart] + 1 : afterCursor[hunkStart];
            var afterCount = hunkLines.Count(l => l.AfterLineNumber.HasValue);

            hunks.Add(new DiffHunk(beforeStart, beforeCount, afterStart, afterCount, hunkLines));

            i = hunkEnd;
        }

        return hunks;
    }
}
