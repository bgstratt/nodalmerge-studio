using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>Covers Phase 7.5 (Slice 25a) — counterfactual replay from an existing proposal.</summary>
[Trait("Category", "Integration")]
public class CounterfactualServiceTests
{
    private static async Task<(
        ICounterfactualService Counterfactuals,
        IOrchestratorService Orchestrator,
        IWorkUnitService WorkUnits,
        IMergeService Merge,
        IFileWorkspaceService FileWorkspace,
        IArtifactLineageService Artifacts,
        IWorkScheduler Scheduler)> BuildAsync()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        await fileWorkspace.InitBranchAsync("main");

        return (
            app.Services.GetRequiredService<ICounterfactualService>(),
            app.Services.GetRequiredService<IOrchestratorService>(),
            app.Services.GetRequiredService<IWorkUnitService>(),
            app.Services.GetRequiredService<IMergeService>(),
            fileWorkspace,
            app.Services.GetRequiredService<IArtifactLineageService>(),
            app.Services.GetRequiredService<IWorkScheduler>());
    }

    private static async Task<MergeProposal> RaiseProposalAsync(
        IMergeService merge, IArtifactLineageService artifacts,
        string proposalId, string sourceBranch, string workUnitId)
    {
        var proposal = new MergeProposal(
            proposalId, sourceBranch, "main", "goal", "summary", "desc",
            null, null, null, MergeProposalStatus.Draft,
            WorkUnitId: workUnitId, FilesTouched: ["src/feature.cs"]);
        var created = await merge.ProposeAsync(proposal);
        await artifacts.RecordAsync(new ArtifactRef(
            proposalId, ArtifactType.MergeProposal, workUnitId, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, workUnitId, null));
        return created;
    }

    [Fact]
    public async Task CreateAsync_branches_from_the_proposals_base_state_with_a_new_profile()
    {
        var (counterfactuals, orchestrator, workUnits, merge, fileWorkspace, artifacts, scheduler) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.InitBranchAsync(origin.BranchId);
        await fileWorkspace.WriteAsync(origin.BranchId, "src/feature.cs", "// origin's in-progress edit");
        await RaiseProposalAsync(merge, artifacts, "MP-1", origin.BranchId, origin.WorkUnitId);

        var result = await counterfactuals.CreateAsync(new CounterfactualCommand("MP-1", "alt-profile"));

        Assert.Equal(origin.WorkUnitId, result.OriginalWorkUnitId);
        Assert.Equal("MP-1", result.OriginalProposalId);
        Assert.NotEqual(origin.WorkUnitId, result.NewWorkUnitId);

        var counterfactualWu = await workUnits.GetAsync(result.NewWorkUnitId);
        Assert.NotNull(counterfactualWu);
        Assert.Equal("MP-1", counterfactualWu!.BranchedFromProposalId);
        Assert.Equal(HypothesisForkType.Model, counterfactualWu.ForkType);
        Assert.Contains("[counterfactual]", counterfactualWu.Goal);

        // Seeded from main's pre-proposal state, not origin's in-progress branch.
        Assert.False(await fileWorkspace.ExistsAsync(counterfactualWu.BranchId, "src/feature.cs"));

        var pending = await scheduler.ListPendingAsync();
        Assert.Contains(pending, i => i.WorkUnitId == result.NewWorkUnitId && i.ProfileId == "alt-profile");
    }

    [Fact]
    public async Task CreateAsync_respects_a_goal_override_and_constraint_text()
    {
        var (counterfactuals, orchestrator, workUnits, merge, fileWorkspace, artifacts, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.InitBranchAsync(origin.BranchId);
        await RaiseProposalAsync(merge, artifacts, "MP-2", origin.BranchId, origin.WorkUnitId);

        var result = await counterfactuals.CreateAsync(new CounterfactualCommand(
            "MP-2", "alt-profile", NewGoalOverride: "Retry with GPT-5", ConstraintText: "no external deps"));

        var counterfactualWu = await workUnits.GetAsync(result.NewWorkUnitId);
        Assert.Equal("Retry with GPT-5", counterfactualWu!.Goal);
        Assert.Equal("no external deps", counterfactualWu.Metadata?["constraint"]);
    }

    [Fact]
    public async Task CreateAsync_unknown_proposal_throws()
    {
        var (counterfactuals, _, _, _, _, _, _) = await BuildAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            counterfactuals.CreateAsync(new CounterfactualCommand("MP-does-not-exist", "alt-profile")));
    }
}
