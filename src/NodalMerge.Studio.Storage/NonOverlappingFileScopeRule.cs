using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 14b — the first real IPolicyRule. Opt-in via WorkspaceOptions.BlockOverlappingFileScope
// (default false), so this is always registered but evaluates to Allowed regardless of overlap
// until the toggle is flipped — same "always registered, option gates the effect" shape as
// UseLlmProfileSelection. Reads fileScope/activeSiblings from the context bag FanOutService
// builds rather than depending on IWorkUnitService directly (see FileScopeSibling's doc comment).
public sealed class NonOverlappingFileScopeRule(WorkspaceOptions options) : IPolicyRule
{
    private static readonly HashSet<WorkUnitStatus> ActiveStatuses =
    [
        WorkUnitStatus.Queued,
        WorkUnitStatus.Executing,
        WorkUnitStatus.Retrying,
    ];

    public string RuleId => "non-overlapping-file-scope";

    public PolicyCheckpoint Checkpoint => PolicyCheckpoint.BeforeEnqueue;

    public Task<PolicyResult> EvaluateAsync(IReadOnlyDictionary<string, object?> context, CancellationToken ct = default)
    {
        if (!options.BlockOverlappingFileScope)
            return Task.FromResult(new PolicyResult(true, []));

        var fileScope = context.TryGetValue("fileScope", out var fsValue) && fsValue is IReadOnlyList<string> fs
            ? fs
            : [];
        if (fileScope.Count == 0)
            return Task.FromResult(new PolicyResult(true, []));

        var siblings = context.TryGetValue("activeSiblings", out var sibValue) && sibValue is IReadOnlyList<FileScopeSibling> sibs
            ? sibs
            : [];

        var fileScopeSet = new HashSet<string>(fileScope.Select(Normalize), StringComparer.OrdinalIgnoreCase);

        foreach (var sibling in siblings.Where(s => ActiveStatuses.Contains(s.Status)))
        {
            var overlap = sibling.FileScope.Select(Normalize).FirstOrDefault(fileScopeSet.Contains);
            if (overlap is null)
                continue;

            var label = sibling.SliceId ?? sibling.WorkUnitId;
            return Task.FromResult(new PolicyResult(false,
            [
                new PolicyViolation(RuleId, $"overlaps with slice {label} (file {overlap})"),
            ]));
        }

        return Task.FromResult(new PolicyResult(true, []));
    }

    private static string Normalize(string path) => path.Replace('\\', '/');
}
