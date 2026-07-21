using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// InMemoryMergeService.ApplyAsync used to write real-disk content back from
/// proposal.SourceBranch unconditionally — completely ignoring WorkspaceOptions.UsePromotionBranch,
/// which was supposed to keep AgentApproval/Hybrid autonomous applies off the real repo until an
/// explicit human /promote call. The candidate-branch workspace redirect worked; the disk write did
/// not respect it at all. Fixed: disk write-back is now deferred to IMergeService.PromoteAsync when
/// promotion is on (and not bypassed), sourced from the candidate branch's own composed content.
/// </summary>
[Trait("Category", "Integration")]
public class PromotionBranchWriteBackTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-promotion-writeback-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_repoPath);

    [Fact]
    public async Task ApplyAsync_with_promotion_branch_on_lands_on_candidate_and_does_not_write_disk()
    {
        Directory.CreateDirectory(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// v1");

        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var workUnit = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Update repo", "test", RepositoryPath: _repoPath));
        await fileWorkspace.WriteAsync(workUnit.BranchId, "Program.cs", "// v2 — from agent");

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: workUnit.BranchId, targetBranch: "main", summary: "Update", workUnitId: workUnit.WorkUnitId);
        await mergeCommands.ValidateAsync(proposal.ProposalId);
        await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        var applied = await mergeCommands.ApplyAsync(proposal.ProposalId);

        Assert.Equal(MergeProposalStatus.Merged, applied.Status);
        Assert.False(applied.PromotedToDisk);
        Assert.Equal("// v2 — from agent", await fileWorkspace.ReadAsync(options.CandidateBranchId, "Program.cs"));
        // The actual bug: disk must still say v1 — nothing should have reached it yet.
        Assert.Equal("// v1", await File.ReadAllTextAsync(Path.Combine(_repoPath, "Program.cs")));

        var stored = await merge.GetAsync(proposal.ProposalId);
        Assert.False(stored!.PromotedToDisk);
    }

    [Fact]
    public async Task PromoteAsync_writes_disk_from_candidates_composed_content_and_flips_PromotedToDisk()
    {
        Directory.CreateDirectory(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// v1");

        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var workUnit = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Update repo", "test", RepositoryPath: _repoPath));
        await fileWorkspace.WriteAsync(workUnit.BranchId, "Program.cs", "// v2 — from agent");

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: workUnit.BranchId, targetBranch: "main", summary: "Update", workUnitId: workUnit.WorkUnitId);
        await mergeCommands.ValidateAsync(proposal.ProposalId);
        await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposal.ProposalId);

        var result = await merge.PromoteAsync();

        Assert.True(result.Succeeded);
        Assert.Single(result.Promoted);
        Assert.True(result.Promoted[0].PromotedToDisk);
        Assert.Equal("// v2 — from agent", await File.ReadAllTextAsync(Path.Combine(_repoPath, "Program.cs")));
        Assert.Equal("// v2 — from agent", await fileWorkspace.ReadAsync("main", "Program.cs"));

        // Idempotent — nothing left pending, a second call is a no-op.
        var second = await merge.PromoteAsync();
        Assert.True(second.Succeeded);
        Assert.Empty(second.Promoted);
    }

    [Fact]
    public async Task Pending_endpoint_selection_matches_PromoteAsyncs_own_eligibility()
    {
        Directory.CreateDirectory(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// v1");

        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var workUnit = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Update repo", "test", RepositoryPath: _repoPath));
        await fileWorkspace.WriteAsync(workUnit.BranchId, "Program.cs", "// v2 — from agent");

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: workUnit.BranchId, targetBranch: "main", summary: "Update", workUnitId: workUnit.WorkUnitId);
        await mergeCommands.ValidateAsync(proposal.ProposalId);
        await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposal.ProposalId);

        var beforePromote = await merge.ListAsync();
        Assert.Contains(beforePromote, p => p.ProposalId == proposal.ProposalId
            && p.Status == MergeProposalStatus.Merged && !p.PromotedToDisk);

        await merge.PromoteAsync();

        var afterPromote = await merge.ListAsync();
        Assert.DoesNotContain(afterPromote, p => p.ProposalId == proposal.ProposalId
            && p.Status == MergeProposalStatus.Merged && !p.PromotedToDisk);
    }

    /// <summary>
    /// A goal merged in a workspace-only session (no repo path — RepositoryPath omitted, so
    /// ResolveWriteBackPathAsync can never resolve a path and PromotedToDisk is never flipped true)
    /// must not retroactively show up in the promotion queue just because UsePromotionBranch is
    /// later switched on. Before LandedOnCandidateBranch existed, PromoteAsync/the pending endpoint
    /// inferred eligibility purely from "!PromotedToDisk", which conflated "never had anywhere to
    /// write back to" with "actually landed on candidate" — every proposal ever merged before the
    /// toggle flipped on would surface as pending the instant it did.
    /// </summary>
    [Fact]
    public async Task Goal_merged_before_promotion_branch_was_enabled_never_appears_in_the_promotion_queue()
    {
        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        // UsePromotionBranch starts (and stays, for this apply) off.

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var workUnit = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Workspace-only goal", "test"));
        await fileWorkspace.WriteAsync(workUnit.BranchId, "Notes.md", "done");

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: workUnit.BranchId, targetBranch: "main", summary: "Notes", workUnitId: workUnit.WorkUnitId);
        await mergeCommands.ValidateAsync(proposal.ProposalId);
        await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        var applied = await mergeCommands.ApplyAsync(proposal.ProposalId);

        // No repo path configured, so this never gets marked PromotedToDisk — the pre-fix leak.
        Assert.False(applied.PromotedToDisk);
        Assert.False(applied.LandedOnCandidateBranch);

        // A human now turns promotion branch on for future goals.
        options.UsePromotionBranch = true;

        var pending = await merge.PromoteAsync();
        Assert.True(pending.Succeeded);
        Assert.Empty(pending.Promoted);
    }
}
