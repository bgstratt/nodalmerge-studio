using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

// Slice 20b/20c — BeforeMerge policy rule for AgentApproval and Hybrid review policies.
//
// HumanRequired (default): passes through immediately — no behavioral change.
//
// AgentApproval: runs the reviewer agent inline and returns Allowed when approved; Rejected
//   blocks apply and surfaces the reviewer's notes as a policy violation.
//
// Hybrid: runs the reviewer inline; if approved, schedules a countdown timer (default 5 min) and
//   returns NOT allowed (blocking immediate apply). ReviewTimerService.ProcessExpiredAsync fires
//   ApplyAsync again at expiry; at that point the proposal is already in Approved status and this
//   rule detects the pre-approved state and passes through. A human Reject before expiry cancels
//   the timer via TryCancelAsync.
//
// Context keys: "proposalId" (string), "workUnit" (WorkUnit?), "proposal" (MergeProposal? — optional,
//   supplied by ApplyAsync so the rule can check the current proposal status without an extra fetch).
public sealed class AutoReviewRule(
    IInlineReviewerService? inlineReviewer = null,
    IReviewTimerService? timerService = null,
    IMergeService? mergeService = null) : IPolicyRule
{
    private static readonly TimeSpan DefaultHybridTimeout = TimeSpan.FromMinutes(5);

    public string RuleId => "auto-review";
    public PolicyCheckpoint Checkpoint => PolicyCheckpoint.BeforeMerge;

    public async Task<PolicyResult> EvaluateAsync(
        IReadOnlyDictionary<string, object?> context,
        CancellationToken ct = default)
    {
        var workUnit = context.TryGetValue("workUnit", out var wu) ? wu as WorkUnit : null;
        // A work unit whose own apply can reach the real on-disk repo (see WorkspaceReviewScope —
        // top-level goals, plus any work unit explicitly linked to its own RepositoryId such as a
        // Multi-Model Comparison child) is gated by WorkspaceReviewPolicy; everything else
        // (fanned-out task children, generic experiment forks) is gated by TaskReviewPolicy since
        // their apply only ever merges into their parent's branch, never onto disk.
        var appliesToRealRepo = WorkspaceReviewScope.AppliesToRealRepo(workUnit);
        var policy = (appliesToRealRepo ? workUnit?.WorkspaceReviewPolicy : workUnit?.TaskReviewPolicy)
            ?? ReviewPolicy.HumanRequired;
        var hybridTimeoutMinutes = appliesToRealRepo
            ? workUnit?.WorkspaceReviewHybridTimeoutMinutes
            : workUnit?.TaskReviewHybridTimeoutMinutes;
        var hybridTimeout = hybridTimeoutMinutes is { } minutes
            ? TimeSpan.FromMinutes(minutes)
            : DefaultHybridTimeout;

        if (policy == ReviewPolicy.HumanRequired)
            return new PolicyResult(true, []);

        var proposalId = context.TryGetValue("proposalId", out var pid) ? pid as string : null;
        if (string.IsNullOrEmpty(proposalId))
            return new PolicyResult(false, [new PolicyViolation(RuleId, "No proposalId in BeforeMerge context.")]);

        var workUnitId = workUnit?.WorkUnitId;
        if (string.IsNullOrEmpty(workUnitId))
            return new PolicyResult(false, [new PolicyViolation(RuleId, "Cannot determine workUnitId for inline review.")]);

        // Fetch current proposal status — if already Approved (by prior reviewer run or human),
        // pass through. This handles: (a) the timer-expiry re-entry for Hybrid, (b) the case where
        // the async enqueue path approved before the inline reviewer ran.
        var currentProposal = context.TryGetValue("proposal", out var p) ? p as MergeProposal : null;
        if (currentProposal is null && mergeService is not null)
            currentProposal = await mergeService.GetAsync(proposalId, ct).ConfigureAwait(false);

        if (currentProposal?.Status is MergeProposalStatus.Approved)
        {
            // For Hybrid: cancel any pending timer since we're proceeding now.
            if (policy == ReviewPolicy.Hybrid && timerService is not null)
                await timerService.TryCancelAsync(proposalId, ct).ConfigureAwait(false);
            return new PolicyResult(true, []);
        }

        if (inlineReviewer is null)
            return new PolicyResult(false, [new PolicyViolation(RuleId, "IInlineReviewerService is not registered.")]);

        var result = await inlineReviewer.ReviewAsync(workUnitId, proposalId, ct).ConfigureAwait(false);

        if (!result.Approved)
        {
            var message = string.IsNullOrWhiteSpace(result.Notes)
                ? "Reviewer agent rejected the proposal."
                : $"Reviewer agent rejected: {result.Notes}";
            return new PolicyResult(false, [new PolicyViolation(RuleId, message)]);
        }

        // AgentApproval: reviewer approved → proceed immediately.
        if (policy == ReviewPolicy.AgentApproval)
            return new PolicyResult(true, []);

        // Hybrid: reviewer approved → start countdown, block immediate apply.
        if (timerService is not null)
            await timerService.ScheduleAsync(proposalId, workUnitId, hybridTimeout, ct).ConfigureAwait(false);

        return new PolicyResult(false,
        [
            new PolicyViolation(RuleId,
                $"Hybrid review: agent approved. Auto-merges in {(int)hybridTimeout.TotalMinutes} minutes unless overridden.")
        ]);
    }
}
