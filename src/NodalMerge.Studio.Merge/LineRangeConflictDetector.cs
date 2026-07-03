namespace NodalMerge.Studio.Merge;

// Extracted from MergeReconciliationService's own sibling-overlap detection (its
// DetectOverlappingFilesAsync/RangesOverlap) — a genuinely content-in/ranges-out primitive, not
// coupled to comparing two proposals against each other. Reused by InMemoryMergeService.ApplyAsync
// to detect the *other* shape of conflict: has the target branch drifted since this proposal's own
// base/{proposalId} snapshot, in a way that overlaps what the proposal itself changed. Both
// comparisons (proposal's own before/after, and base-vs-current-target) share the same "before"
// side (base/{proposalId}), so their resulting ranges are directly comparable via simple interval
// overlap — no coordinate translation needed.
internal static class LineRangeConflictDetector
{
    public static List<(int Start, int End)> ComputeChangedRanges(string? before, string? after)
    {
        var hunks = LineDiffer.Diff(before, after, contextLines: 0);
        return hunks
            .Select(h => (h.BeforeStart, h.BeforeStart + Math.Max(h.BeforeCount, 1) - 1))
            .ToList();
    }

    public static bool RangesOverlap(List<(int Start, int End)> a, List<(int Start, int End)> b)
    {
        foreach (var ra in a)
        {
            foreach (var rb in b)
            {
                if (ra.Start <= rb.End && rb.Start <= ra.End)
                    return true;
            }
        }

        return false;
    }
}
