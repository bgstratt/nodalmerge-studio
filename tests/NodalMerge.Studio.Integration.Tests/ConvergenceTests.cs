using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Comparison engine — IExperimentService.ConvergeAsync. Confirms "pick a winner among
/// competing forks" actually writes durable convergence state (proposal approve/reject,
/// DecisionNode per sibling, HypothesisNode status) instead of only approving the winner.
/// </summary>
[Trait("Category", "Integration")]
public class ConvergenceTests
{
    private static async Task<(
        IExperimentService Experiments,
        IMergeCommandService MergeCommands,
        IMergeService Merge,
        IDecisionNodeService Decisions,
        IHypothesisNodeService Hypotheses,
        IFileWorkspaceService FileWorkspace)> BuildAsync()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        await fileWorkspace.InitBranchAsync("main");

        return (
            app.Services.GetRequiredService<IExperimentService>(),
            app.Services.GetRequiredService<IMergeCommandService>(),
            app.Services.GetRequiredService<IMergeService>(),
            app.Services.GetRequiredService<IDecisionNodeService>(),
            app.Services.GetRequiredService<IHypothesisNodeService>(),
            fileWorkspace);
    }

    private static async Task<(string ParentWorkUnitId, string WinnerWorkUnitId, string LoserWorkUnitId,
        string WinnerProposalId, string LoserProposalId)> SetUpExperimentWithProposalsAsync(
        IExperimentService experiments, IMergeCommandService mergeCommands, IFileWorkspaceService fileWorkspace)
    {
        var result = await experiments.CreateAsync(new ExperimentSpec(
            "Pick a caching layer", "test", HypothesisForkType.Library,
            [
                new ExperimentForkSpec(null, "Redis"),
                new ExperimentForkSpec(null, "Memcached"),
            ]));

        var winnerWorkUnitId = result.ForkWorkUnitIds[0];
        var loserWorkUnitId = result.ForkWorkUnitIds[1];

        await fileWorkspace.InitBranchAsync(winnerWorkUnitId);
        await fileWorkspace.InitBranchAsync(loserWorkUnitId);

        var winnerProposal = await mergeCommands.ProposeAsync(
            sourceBranch: winnerWorkUnitId, targetBranch: "main", summary: "Use Redis",
            workUnitId: winnerWorkUnitId);
        var loserProposal = await mergeCommands.ProposeAsync(
            sourceBranch: loserWorkUnitId, targetBranch: "main", summary: "Use Memcached",
            workUnitId: loserWorkUnitId);

        await mergeCommands.ValidateAsync(winnerProposal.ProposalId);
        await mergeCommands.ValidateAsync(loserProposal.ProposalId);

        return (result.ParentWorkUnitId, winnerWorkUnitId, loserWorkUnitId,
            winnerProposal.ProposalId, loserProposal.ProposalId);
    }

    [Fact]
    public async Task ConvergeAsync_approves_winner_and_rejects_loser_proposals()
    {
        var (experiments, mergeCommands, merge, _, _, fileWorkspace) = await BuildAsync();
        var (parentId, winnerId, _, winnerProposalId, loserProposalId) =
            await SetUpExperimentWithProposalsAsync(experiments, mergeCommands, fileWorkspace);

        await experiments.ConvergeAsync(parentId, winnerId, "Redis benchmarks better under load.");

        var winnerProposal = await merge.GetAsync(winnerProposalId);
        var loserProposal = await merge.GetAsync(loserProposalId);
        Assert.Equal(MergeProposalStatus.Approved, winnerProposal!.Status);
        Assert.Equal(MergeProposalStatus.Rejected, loserProposal!.Status);
    }

    [Fact]
    public async Task ConvergeAsync_writes_a_DecisionNode_per_sibling()
    {
        var (experiments, mergeCommands, _, decisions, _, fileWorkspace) = await BuildAsync();
        var (parentId, winnerId, loserId, _, _) =
            await SetUpExperimentWithProposalsAsync(experiments, mergeCommands, fileWorkspace);

        await experiments.ConvergeAsync(parentId, winnerId, "Redis benchmarks better under load.");

        var winnerDecisions = await decisions.ListByWorkUnitAsync(winnerId);
        var loserDecisions = await decisions.ListByWorkUnitAsync(loserId);

        Assert.Equal(DecisionOutcome.Accepted, winnerDecisions.Single().Outcome);
        Assert.Equal(DecisionOutcome.Rejected, loserDecisions.Single().Outcome);
        Assert.Equal("Redis benchmarks better under load.", winnerDecisions.Single().Rationale);
    }

    [Fact]
    public async Task ConvergeAsync_transitions_HypothesisNode_status_for_every_sibling()
    {
        var (experiments, mergeCommands, _, _, hypotheses, fileWorkspace) = await BuildAsync();
        var (parentId, winnerId, loserId, _, _) =
            await SetUpExperimentWithProposalsAsync(experiments, mergeCommands, fileWorkspace);

        await experiments.ConvergeAsync(parentId, winnerId, rationale: null);

        var nodes = await hypotheses.ListByParentWorkUnitIdAsync(parentId);
        Assert.Equal(HypothesisStatus.Converged, nodes.Single(n => n.WorkUnitId == winnerId).Status);
        Assert.Equal(HypothesisStatus.Rejected, nodes.Single(n => n.WorkUnitId == loserId).Status);
    }

    [Fact]
    public async Task ConvergeAsync_returns_rejected_work_unit_ids()
    {
        var (experiments, mergeCommands, _, _, _, fileWorkspace) = await BuildAsync();
        var (parentId, winnerId, loserId, _, _) =
            await SetUpExperimentWithProposalsAsync(experiments, mergeCommands, fileWorkspace);

        var result = await experiments.ConvergeAsync(parentId, winnerId, rationale: null);

        Assert.Equal(winnerId, result.WinnerWorkUnitId);
        Assert.Equal([loserId], result.RejectedWorkUnitIds);
    }

    [Fact]
    public async Task ConvergeAsync_throws_when_winner_is_not_a_fork_of_the_parent()
    {
        var (experiments, mergeCommands, _, _, _, fileWorkspace) = await BuildAsync();
        var (parentId, _, _, _, _) =
            await SetUpExperimentWithProposalsAsync(experiments, mergeCommands, fileWorkspace);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => experiments.ConvergeAsync(parentId, "not-a-real-fork", rationale: null));
    }
}
