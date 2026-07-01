using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 16e/16f — opt-in policy rule that runs build/test before proposals.
// Zero behavioral change when RequireBuildBeforeProposal and RequireTestBeforeProposal are both false.
public sealed class WorkspaceExecutionRule(
    WorkspaceOptions options,
    IWorkspaceExecutionService execution) : IPolicyRule
{
    string IPolicyRule.RuleId => "workspace-execution";
    PolicyCheckpoint IPolicyRule.Checkpoint => PolicyCheckpoint.ProposalCreated;

    async Task<PolicyResult> IPolicyRule.EvaluateAsync(
        IReadOnlyDictionary<string, object?> context, CancellationToken ct)
    {
        if (!options.RequireBuildBeforeProposal && !options.RequireTestBeforeProposal)
            return new PolicyResult(true, []);

        var branchId = context.TryGetValue("branchId", out var b) ? b as string : null;
        if (branchId is null)
            return new PolicyResult(true, []);

        var result = await execution.ExecuteAsync(branchId, new WorkspaceExecutionRequest(
            Build: options.RequireBuildBeforeProposal,
            Test: options.RequireTestBeforeProposal,
            BuildCommand: options.BuildCommand,
            TestCommand: options.TestCommand,
            TimeoutSeconds: options.ExecutionTimeoutSeconds), ct).ConfigureAwait(false);

        // Attach result to context so MergeCommandService can store it on the proposal.
        // context is typed IReadOnlyDictionary but is always a mutable Dictionary<string, object?> underneath.
        if (context is Dictionary<string, object?> mutable)
            mutable["executionResult"] = result;

        var violations = new List<PolicyViolation>();
        foreach (var build in result.Builds.Where(b => !b.Success))
            violations.Add(new PolicyViolation("workspace-execution",
                $"[{build.BuildSystem ?? "build"}] failed (exit {build.ExitCode}): {Truncate(build.StdErr)}"));

        foreach (var test in result.Tests.Where(t => !t.Success))
            violations.Add(new PolicyViolation("workspace-execution",
                $"[{test.BuildSystem ?? "test"}] {test.Failed}/{test.TotalTests} tests failed"));

        return violations.Count == 0
            ? new PolicyResult(true, [])
            : new PolicyResult(false, violations);
    }

    private static string Truncate(string s, int max = 300) =>
        s.Length <= max ? s : s[..max] + "...";
}