using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge.Tests;

public class AutoReviewRuleTests
{
    [Fact]
    public async Task HumanRequired_passes_through_without_invoking_the_reviewer()
    {
        var reviewer = new FakeInlineReviewerService { ThrowIfCalled = true };
        var rule = new AutoReviewRule(reviewer);
        var context = Context(MakeWorkUnit(ReviewPolicy.HumanRequired), "MP-1");

        var result = await rule.EvaluateAsync(context);

        Assert.True(result.Allowed);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public async Task AgentApproval_reviewer_approves_allows_apply()
    {
        var reviewer = new FakeInlineReviewerService { Result = new InlineReviewResult(true, null) };
        var rule = new AutoReviewRule(reviewer);
        var context = Context(MakeWorkUnit(ReviewPolicy.AgentApproval), "MP-1");

        var result = await rule.EvaluateAsync(context);

        Assert.True(result.Allowed);
        Assert.Equal(("WU-1", "MP-1"), reviewer.LastCall);
    }

    [Fact]
    public async Task AgentApproval_reviewer_rejects_blocks_apply_with_notes_in_violation()
    {
        var reviewer = new FakeInlineReviewerService { Result = new InlineReviewResult(false, "Missing tests.") };
        var rule = new AutoReviewRule(reviewer);
        var context = Context(MakeWorkUnit(ReviewPolicy.AgentApproval), "MP-1");

        var result = await rule.EvaluateAsync(context);

        Assert.False(result.Allowed);
        Assert.Contains(result.Violations, v => v.Message.Contains("Missing tests.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Hybrid_reviewer_approves_schedules_timer_and_blocks_immediate_apply()
    {
        var reviewer = new FakeInlineReviewerService { Result = new InlineReviewResult(true, null) };
        var timers = new FakeReviewTimerService();
        var rule = new AutoReviewRule(reviewer, timers);
        var context = Context(MakeWorkUnit(ReviewPolicy.Hybrid), "MP-1");

        var result = await rule.EvaluateAsync(context);

        Assert.False(result.Allowed);
        Assert.Equal(("MP-1", "WU-1"), timers.Scheduled.Single());
        Assert.Empty(timers.Cancelled);
    }

    [Fact]
    public async Task Hybrid_reviewer_rejects_does_not_schedule_a_timer()
    {
        var reviewer = new FakeInlineReviewerService { Result = new InlineReviewResult(false, "Broken build.") };
        var timers = new FakeReviewTimerService();
        var rule = new AutoReviewRule(reviewer, timers);
        var context = Context(MakeWorkUnit(ReviewPolicy.Hybrid), "MP-1");

        var result = await rule.EvaluateAsync(context);

        Assert.False(result.Allowed);
        Assert.Empty(timers.Scheduled);
    }

    [Fact]
    public async Task Already_approved_proposal_skips_the_reviewer_and_allows_apply()
    {
        // Timer-expiry re-entry: AutoReviewRule.EvaluateAsync runs again on the already-approved
        // proposal without re-invoking the reviewer agent.
        var reviewer = new FakeInlineReviewerService { ThrowIfCalled = true };
        var rule = new AutoReviewRule(reviewer);
        var proposal = MakeProposal("MP-1", MergeProposalStatus.Approved);
        var context = Context(MakeWorkUnit(ReviewPolicy.AgentApproval), "MP-1", proposal);

        var result = await rule.EvaluateAsync(context);

        Assert.True(result.Allowed);
    }

    [Fact]
    public async Task Hybrid_already_approved_proposal_cancels_the_pending_timer()
    {
        var reviewer = new FakeInlineReviewerService { ThrowIfCalled = true };
        var timers = new FakeReviewTimerService();
        var rule = new AutoReviewRule(reviewer, timers);
        var proposal = MakeProposal("MP-1", MergeProposalStatus.Approved);
        var context = Context(MakeWorkUnit(ReviewPolicy.Hybrid), "MP-1", proposal);

        var result = await rule.EvaluateAsync(context);

        Assert.True(result.Allowed);
        Assert.Equal(["MP-1"], timers.Cancelled);
    }

    private static IReadOnlyDictionary<string, object?> Context(
        WorkUnit workUnit, string proposalId, MergeProposal? proposal = null) =>
        new Dictionary<string, object?>
        {
            ["workUnit"] = workUnit,
            ["proposalId"] = proposalId,
            ["proposal"] = proposal,
        };

    private static WorkUnit MakeWorkUnit(ReviewPolicy policy) =>
        new(
            "WU-1",
            "goal",
            "main",
            WorkUnitStatus.Proposed,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "owner",
            null,
            null,
            null,
            null,
            [],
            [],
            ReviewPolicy: policy);

    private static MergeProposal MakeProposal(string proposalId, MergeProposalStatus status) =>
        new(proposalId, "feat/x", "main", "goal", "summary", "desc", null, null, null, status);

    private sealed class FakeInlineReviewerService : IInlineReviewerService
    {
        public InlineReviewResult Result { get; set; } = new(true, null);
        public bool ThrowIfCalled { get; set; }
        public (string WorkUnitId, string ProposalId)? LastCall { get; private set; }

        public Task<InlineReviewResult> ReviewAsync(string workUnitId, string proposalId, CancellationToken ct = default)
        {
            if (ThrowIfCalled)
                throw new InvalidOperationException("Reviewer should not have been invoked.");

            LastCall = (workUnitId, proposalId);
            return Task.FromResult(Result);
        }
    }

    private sealed class FakeReviewTimerService : IReviewTimerService
    {
        public List<(string ProposalId, string WorkUnitId)> Scheduled { get; } = [];
        public List<string> Cancelled { get; } = [];

        public Task ScheduleAsync(string proposalId, string workUnitId, TimeSpan delay, CancellationToken ct = default)
        {
            Scheduled.Add((proposalId, workUnitId));
            return Task.CompletedTask;
        }

        public Task TryCancelAsync(string proposalId, CancellationToken ct = default)
        {
            Cancelled.Add(proposalId);
            return Task.CompletedTask;
        }

        public Task ProcessExpiredAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<ReviewTimer?> GetAsync(string proposalId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ReviewTimer>> ListPendingAsync(string? workUnitId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReviewTimer>>([]);
    }
}
