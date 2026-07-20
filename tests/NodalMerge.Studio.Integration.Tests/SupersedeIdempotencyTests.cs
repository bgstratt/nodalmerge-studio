using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/vision-punchlist-remediation.md — re-superseding a proposal onto the SAME target must be a
/// no-op, not a throw.
///
/// Found by the unobserved-task-exception detector, not by a failing assertion. MergeReconciliation
/// loops SupersedeAsync over every constituent proposal; if one was already superseded (a retry, a
/// reinvoke, a second convergence pass over the same parent) the throw aborted the loop, so the
/// remaining constituents were never superseded and the stage was never advanced. And because that
/// whole chain runs fire-and-forget from the scheduler, the exception was swallowed — the goal simply
/// stopped converging, with nothing logged anywhere.
///
/// Re-applying the same conclusion is not a state transition; superseding onto a *different* proposal
/// still throws, because that is a genuine conflict.
/// </summary>
[Trait("Category", "Integration")]
public class SupersedeIdempotencyTests
{
    private static async Task<(IMergeService Merge, string BranchId)> BuildAsync(IServiceProvider services)
    {
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.InitBranchAsync("feature");
        return (services.GetRequiredService<IMergeService>(), "feature");
    }

    [Fact]
    public async Task Superseding_twice_onto_the_same_proposal_is_a_no_op()
    {
        await using var app = StudioWebApplication.Build(
            [], configureServices: s => s.AddInMemoryStorage());
        var (merge, branchId) = await BuildAsync(app.Services);

        var loser = await merge.ProposeAsync(new MergeProposal(
            "MP-loser", branchId, "main", "goal", "loser", "desc", null, null, 0.9,
            MergeProposalStatus.ReadyForReview));
        var winner = await merge.ProposeAsync(new MergeProposal(
            "MP-winner", branchId, "main", "goal", "winner", "desc", null, null, 0.9,
            MergeProposalStatus.ReadyForReview));

        var first = await merge.SupersedeAsync(loser.ProposalId, winner.ProposalId);
        Assert.Equal(MergeProposalStatus.Superseded, first.Status);
        Assert.Equal(winner.ProposalId, first.SupersededBy);

        // The second call is what used to throw and kill the enclosing reconciliation pass.
        var second = await merge.SupersedeAsync(loser.ProposalId, winner.ProposalId);
        Assert.Equal(MergeProposalStatus.Superseded, second.Status);
        Assert.Equal(winner.ProposalId, second.SupersededBy);
    }

    [Fact]
    public async Task Superseding_onto_a_different_proposal_still_throws()
    {
        await using var app = StudioWebApplication.Build(
            [], configureServices: s => s.AddInMemoryStorage());
        var (merge, branchId) = await BuildAsync(app.Services);

        var loser = await merge.ProposeAsync(new MergeProposal(
            "MP-loser", branchId, "main", "goal", "loser", "desc", null, null, 0.9,
            MergeProposalStatus.ReadyForReview));
        var winner = await merge.ProposeAsync(new MergeProposal(
            "MP-winner", branchId, "main", "goal", "winner", "desc", null, null, 0.9,
            MergeProposalStatus.ReadyForReview));
        var other = await merge.ProposeAsync(new MergeProposal(
            "MP-other", branchId, "main", "goal", "other", "desc", null, null, 0.9,
            MergeProposalStatus.ReadyForReview));

        await merge.SupersedeAsync(loser.ProposalId, winner.ProposalId);

        // Idempotency is deliberately narrow: re-stating the same outcome is fine, but claiming a
        // different winner is a real conflict and must not be silently accepted.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => merge.SupersedeAsync(loser.ProposalId, other.ProposalId));
    }
}
