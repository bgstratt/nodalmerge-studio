using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the success criteria from Slice 10f — Proposal DAG & Artifact Branching. As-built:
/// there is no separate IWorkspaceService.CreateFromProposalBaseAsync — the proposal's base state
/// (S0) is a branch snapshot (<c>base/{proposalId}</c>) taken at propose time via the existing
/// IFileWorkspaceService.InitBranchAsync, and "branching" is just IOrchestratorService.CreateWorkUnitAsync
/// seeded from that snapshot. See the 10f as-built note in plans/phase-3-foundations.md.
/// </summary>
[Trait("Category", "Integration")]
public class ProposalBranchingTests
{
    private static async Task<(
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
            app.Services.GetRequiredService<IOrchestratorService>(),
            app.Services.GetRequiredService<IWorkUnitService>(),
            app.Services.GetRequiredService<IMergeService>(),
            fileWorkspace,
            app.Services.GetRequiredService<IArtifactLineageService>(),
            app.Services.GetRequiredService<IWorkScheduler>());
    }

    private static async Task<MergeProposal> RaiseProposalAsync(
        IMergeService merge, IArtifactLineageService artifacts,
        string proposalId, string sourceBranch, string workUnitId, IReadOnlyList<string> filesTouched)
    {
        var proposal = new MergeProposal(
            proposalId, sourceBranch, "main", "goal", "summary", "desc",
            null, null, null, MergeProposalStatus.Draft,
            WorkUnitId: workUnitId, FilesTouched: filesTouched);
        var created = await merge.ProposeAsync(proposal);
        await artifacts.RecordAsync(new ArtifactRef(
            proposalId, ArtifactType.MergeProposal, workUnitId, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, workUnitId, null));
        return created;
    }

    [Fact]
    public async Task ProposeAsync_snapshots_target_branch_as_base_state()
    {
        var (orchestrator, _, merge, fileWorkspace, artifacts, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.InitBranchAsync(origin.BranchId);
        await fileWorkspace.WriteAsync(origin.BranchId, "src/feature.cs", "// v1");

        await RaiseProposalAsync(merge, artifacts, "MP-1", origin.BranchId, origin.WorkUnitId, ["src/feature.cs"]);

        // main never had src/feature.cs, so the base/MP-1 snapshot must not have it either.
        Assert.False(await fileWorkspace.ExistsAsync("base/MP-1", "src/feature.cs"));
    }

    [Fact]
    public async Task Branching_creates_work_unit_seeded_from_the_proposals_base_state()
    {
        var (orchestrator, workUnits, merge, fileWorkspace, artifacts, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.InitBranchAsync(origin.BranchId);
        await fileWorkspace.WriteAsync(origin.BranchId, "src/feature.cs", "// v1 — only on origin's branch");

        await RaiseProposalAsync(merge, artifacts, "MP-1", origin.BranchId, origin.WorkUnitId, ["src/feature.cs"]);

        var branched = await orchestrator.CreateWorkUnitAsync(
            "Try a different approach", "user",
            parentWorkUnitId: origin.WorkUnitId,
            seedFromBranchId: "base/MP-1",
            branchedFromProposalId: "MP-1");

        Assert.Equal(origin.WorkUnitId, branched.ParentWorkUnitId);
        Assert.Equal("MP-1", branched.Metadata?["branchedFromProposalId"]);
        // Branched work unit starts from main's pre-proposal state, not origin's in-progress branch.
        Assert.False(await fileWorkspace.ExistsAsync(branched.BranchId, "src/feature.cs"));

        var children = await workUnits.GetChildrenAsync(origin.WorkUnitId);
        Assert.Contains(children, c => c.WorkUnitId == branched.WorkUnitId);
    }

    [Fact]
    public async Task Branched_work_unit_Goal_artifact_is_parented_to_origin_Goal()
    {
        var (orchestrator, _, merge, fileWorkspace, artifacts, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.InitBranchAsync(origin.BranchId);
        await RaiseProposalAsync(merge, artifacts, "MP-1", origin.BranchId, origin.WorkUnitId, []);

        var branched = await orchestrator.CreateWorkUnitAsync(
            "Try again", "user", parentWorkUnitId: origin.WorkUnitId,
            seedFromBranchId: "base/MP-1", branchedFromProposalId: "MP-1");

        var goalArtifact = await artifacts.GetAsync(branched.WorkUnitId);
        Assert.NotNull(goalArtifact);
        Assert.Equal(ArtifactType.Goal, goalArtifact!.Type);
        Assert.Equal(origin.WorkUnitId, goalArtifact.ParentArtifactId);
    }

    [Fact]
    public async Task Branched_work_unit_enqueues_and_runs_through_the_scheduler_like_any_other()
    {
        var (orchestrator, _, merge, fileWorkspace, artifacts, scheduler) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.InitBranchAsync(origin.BranchId);
        await RaiseProposalAsync(merge, artifacts, "MP-1", origin.BranchId, origin.WorkUnitId, []);

        var branched = await orchestrator.CreateWorkUnitAsync(
            "Try again", "user", parentWorkUnitId: origin.WorkUnitId,
            seedFromBranchId: "base/MP-1", branchedFromProposalId: "MP-1");

        await scheduler.EnqueueAsync(branched.WorkUnitId, "worker");

        var pending = await scheduler.ListPendingAsync();
        Assert.Contains(pending, i => i.WorkUnitId == branched.WorkUnitId);
    }

    [Fact]
    public async Task Compare_two_proposals_from_the_same_origin_reports_overlapping_files()
    {
        var (orchestrator, _, merge, fileWorkspace, artifacts, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.InitBranchAsync(origin.BranchId);

        var a = await RaiseProposalAsync(merge, artifacts, "MP-A", origin.BranchId, origin.WorkUnitId,
            ["src/feature.cs", "src/shared.cs"]);
        var b = await RaiseProposalAsync(merge, artifacts, "MP-B", origin.BranchId, origin.WorkUnitId,
            ["src/shared.cs", "src/other.cs"]);

        // Mirrors the /studio/merges/compare endpoint's comparison logic.
        Assert.Equal(a.WorkUnitId, b.WorkUnitId);
        var overlap = a.FilesTouched.Intersect(b.FilesTouched, StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(["src/shared.cs"], overlap);
    }

    [Fact]
    public async Task ProposalDag_chain_reflects_status_and_branches_reflect_metadata()
    {
        var (orchestrator, workUnits, merge, fileWorkspace, artifacts, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.InitBranchAsync(origin.BranchId);
        await RaiseProposalAsync(merge, artifacts, "MP-1", origin.BranchId, origin.WorkUnitId, ["src/a.cs"]);
        await artifacts.UpdateStatusAsync("MP-1", ArtifactStatus.Approved);

        var branched = await orchestrator.CreateWorkUnitAsync(
            "Branch attempt", "user", parentWorkUnitId: origin.WorkUnitId,
            seedFromBranchId: "base/MP-1", branchedFromProposalId: "MP-1");

        // Mirrors the /studio/workunits/{id}/proposal-dag endpoint's composition.
        var chain = await artifacts.GetChainAsync(origin.WorkUnitId);
        var proposalRef = chain.Single(r => r.Type == ArtifactType.MergeProposal);
        Assert.Equal(ArtifactStatus.Approved, proposalRef.Status);

        var children = await workUnits.GetChildrenAsync(origin.WorkUnitId);
        var branches = children.Where(c => c.Metadata?.ContainsKey("branchedFromProposalId") == true).ToList();
        Assert.Single(branches);
        Assert.Equal(branched.WorkUnitId, branches[0].WorkUnitId);
        Assert.Equal("MP-1", branches[0].Metadata!["branchedFromProposalId"]);
    }
}
