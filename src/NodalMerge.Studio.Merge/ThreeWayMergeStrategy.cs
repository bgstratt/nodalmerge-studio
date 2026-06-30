using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

// Phase 10 — standard line-level three-way merge using LineDiffer.
// Converts diff(base→A) and diff(base→B) into edit scripts, then walks the base applying
// both edit scripts. Succeeds when the edit regions don't overlap or overlap identically.
// Falls through (Success=false) when the same base region is changed differently by A and B.
public sealed class ThreeWayMergeStrategy : IMergeStrategy
{
    public string Name => "threeway";

    public Task<MergeStrategyResult> MergeAsync(MergeContext context, CancellationToken ct = default)
    {
        // Short-circuit: if either side deleted the file, three-way can't auto-merge.
        if (context.ContentA is null || context.ContentB is null)
            return Task.FromResult(new MergeStrategyResult(false, null, Name,
                "Cannot three-way merge a deletion — requires human review."));

        // If A and B are identical, no conflict regardless of base.
        if (context.ContentA == context.ContentB)
            return Task.FromResult(new MergeStrategyResult(true, context.ContentA, Name));

        // If one side is unchanged from base, take the other.
        if (context.ContentA == context.BaseContent)
            return Task.FromResult(new MergeStrategyResult(true, context.ContentB, Name));
        if (context.ContentB == context.BaseContent)
            return Task.FromResult(new MergeStrategyResult(true, context.ContentA, Name));

        var merged = TryMerge(context.BaseContent, context.ContentA, context.ContentB);
        return merged is not null
            ? Task.FromResult(new MergeStrategyResult(true, merged, Name))
            : Task.FromResult(new MergeStrategyResult(false, null, Name,
                "Line-level conflict: both branches modified the same region differently."));
    }

    // A "patch" derived from diff(base, branch): replaces base[BaseStart..BaseEnd) with NewLines.
    private readonly record struct Edit(int BaseStart, int BaseEnd, string[] NewLines);

    private static string? TryMerge(string? baseText, string contentA, string contentB)
    {
        var rawA = LineDiffer.DiffRaw(baseText, contentA);
        var rawB = LineDiffer.DiffRaw(baseText, contentB);

        var editsA = BuildEdits(rawA);
        var editsB = BuildEdits(rawB);

        var baseLines = SplitLines(baseText);
        return ApplyEdits(baseLines, editsA, editsB);
    }

    // Convert raw DiffLines into a sorted list of Edit records. Each Edit represents a
    // contiguous non-Context region: which base lines it replaces and what A/B put there.
    private static List<Edit> BuildEdits(IReadOnlyList<DiffLine> rawDiff)
    {
        var edits = new List<Edit>();
        var baseIdx = 0;
        var i = 0;

        while (i < rawDiff.Count)
        {
            if (rawDiff[i].Kind == DiffLineKind.Context)
            {
                baseIdx++;
                i++;
                continue;
            }

            var editStart = baseIdx;
            var removedLines = new List<string>();
            var addedLines = new List<string>();

            while (i < rawDiff.Count && rawDiff[i].Kind != DiffLineKind.Context)
            {
                if (rawDiff[i].Kind == DiffLineKind.Removed)
                {
                    removedLines.Add(rawDiff[i].Text);
                    baseIdx++;
                }
                else
                {
                    addedLines.Add(rawDiff[i].Text);
                }
                i++;
            }

            edits.Add(new Edit(editStart, baseIdx, [..addedLines]));
        }

        return edits;
    }

    // Walk base, applying both edit scripts. Returns null on conflict.
    private static string? ApplyEdits(string[] baseLines, List<Edit> editsA, List<Edit> editsB)
    {
        var result = new List<string>();
        var ia = 0;
        var ib = 0;
        var baseIdx = 0;

        while (baseIdx <= baseLines.Length)
        {
            var aStart = ia < editsA.Count ? editsA[ia].BaseStart : int.MaxValue;
            var bStart = ib < editsB.Count ? editsB[ib].BaseStart : int.MaxValue;

            // Advance base to the next edit boundary
            var nextEdit = Math.Min(aStart, bStart);
            if (nextEdit > baseLines.Length) nextEdit = baseLines.Length;

            while (baseIdx < nextEdit)
                result.Add(baseLines[baseIdx++]);

            if (baseIdx > baseLines.Length)
                break;

            var aHere = ia < editsA.Count && editsA[ia].BaseStart == baseIdx;
            var bHere = ib < editsB.Count && editsB[ib].BaseStart == baseIdx;

            if (!aHere && !bHere)
            {
                // No edit at this position — advance past remaining base lines
                if (baseIdx < baseLines.Length)
                    result.Add(baseLines[baseIdx++]);
                else
                    break;
                continue;
            }

            if (aHere && bHere)
            {
                var editA = editsA[ia];
                var editB = editsB[ib];

                if (editA.BaseEnd == editB.BaseEnd && editA.NewLines.SequenceEqual(editB.NewLines))
                {
                    // Identical edits — apply once
                    result.AddRange(editA.NewLines);
                    baseIdx = editA.BaseEnd;
                    ia++; ib++;
                }
                else
                {
                    return null; // Real conflict: same base region → different content
                }
            }
            else if (aHere)
            {
                var editA = editsA[ia];
                // Ensure B has no overlapping edit
                if (ib < editsB.Count && editsB[ib].BaseStart < editA.BaseEnd)
                    return null; // B's next edit overlaps A's — conflict
                result.AddRange(editA.NewLines);
                baseIdx = editA.BaseEnd;
                ia++;
            }
            else // bHere
            {
                var editB = editsB[ib];
                if (ia < editsA.Count && editsA[ia].BaseStart < editB.BaseEnd)
                    return null; // A's next edit overlaps B's — conflict
                result.AddRange(editB.NewLines);
                baseIdx = editB.BaseEnd;
                ib++;
            }
        }

        return string.Join("\n", result);
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var normalized = text.Replace("\r\n", "\n");
        var lines = normalized.Split('\n');
        return lines.Length > 0 && lines[^1].Length == 0 && normalized.EndsWith('\n')
            ? lines[..^1]
            : lines;
    }
}
