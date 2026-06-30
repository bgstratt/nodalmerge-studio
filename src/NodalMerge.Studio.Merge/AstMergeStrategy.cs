using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

// Phase 10 — structure-aware merge for .cs files: runs ThreeWay and then validates the
// merged output with Roslyn (via ISourceValidator) to ensure it parses cleanly. Falls
// through when ThreeWay fails or the merged content doesn't parse.
//
// Full AST-level declaration merging (merging at the member level rather than line level)
// is deferred to a future phase — this strategy adds real value today by preventing the
// system from accepting a ThreeWay merge that produces syntactically broken C#.
public sealed class AstMergeStrategy(
    ThreeWayMergeStrategy threeWay,
    ISourceValidator? validator = null) : IMergeStrategy
{
    public string Name => "ast";

    public async Task<MergeStrategyResult> MergeAsync(MergeContext context, CancellationToken ct = default)
    {
        if (!IsCSharpFile(context.Path))
            return new MergeStrategyResult(false, null, Name, "Not a C# file — AST strategy skipped.");

        var baseResult = await threeWay.MergeAsync(context, ct).ConfigureAwait(false);
        if (!baseResult.Success)
            return new MergeStrategyResult(false, null, Name,
                $"ThreeWay pass failed: {baseResult.FailureReason}");

        if (validator is not null && baseResult.MergedContent is not null
            && !validator.IsValidSyntax(baseResult.MergedContent, context.Path))
        {
            return new MergeStrategyResult(false, null, Name,
                "ThreeWay produced syntactically invalid C# — falling through to LLM strategy.");
        }

        return baseResult with { StrategyName = Name };
    }

    private static bool IsCSharpFile(string path) =>
        path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
}
