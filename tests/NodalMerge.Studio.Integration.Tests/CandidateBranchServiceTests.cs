using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers Phase 7.1 (Slice 21a/21b) — the candidate/promotion branch safety model.
/// CandidateBranchService just ensures the candidate branch exists; the actual merge-target
/// redirection lives in InMemoryMergeService.ApplyAsync, and the human-only promote step is
/// IFileWorkspaceService.ApplyBranchAsync(candidate, "main") — mirrored here directly rather
/// than through the /studio/branches/candidate/promote endpoint (same precedent as
/// ProposalBranchingTests for /studio/merges/{id}/restore-workspace).
/// </summary>
[Trait("Category", "Integration")]
public class CandidateBranchServiceTests
{
    private static async Task<(
        CandidateBranchService CandidateBranch,
        WorkspaceOptions Options,
        IBranchService Branches,
        IFileWorkspaceService FileWorkspace)> BuildAsync()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        return (
            app.Services.GetRequiredService<CandidateBranchService>(),
            app.Services.GetRequiredService<WorkspaceOptions>(),
            app.Services.GetRequiredService<IBranchService>(),
            app.Services.GetRequiredService<IFileWorkspaceService>());
    }

    [Fact]
    public async Task EnsureAsync_creates_the_candidate_branch_when_missing()
    {
        var (candidateBranch, options, branches, _) = await BuildAsync();

        Assert.DoesNotContain(options.CandidateBranchId, await branches.ListBranchesAsync());

        await candidateBranch.EnsureAsync();

        Assert.Contains(options.CandidateBranchId, await branches.ListBranchesAsync());
    }

    [Fact]
    public async Task EnsureAsync_is_idempotent()
    {
        var (candidateBranch, _, branches, _) = await BuildAsync();

        await candidateBranch.EnsureAsync();
        await candidateBranch.EnsureAsync();

        var matches = (await branches.ListBranchesAsync())
            .Count(b => b == "candidate");
        Assert.Equal(1, matches);
    }

    [Fact]
    public async Task EnsureAsync_uses_a_custom_CandidateBranchId_when_configured()
    {
        var (candidateBranch, options, branches, _) = await BuildAsync();
        options.CandidateBranchId = "staging";

        await candidateBranch.EnsureAsync();

        Assert.Contains("staging", await branches.ListBranchesAsync());
    }

    [Fact]
    public async Task RehydrateAsync_is_a_noop_when_promotion_branch_is_disabled()
    {
        var (candidateBranch, options, branches, _) = await BuildAsync();
        Assert.False(options.UsePromotionBranch); // default

        await candidateBranch.RehydrateAsync();

        Assert.DoesNotContain(options.CandidateBranchId, await branches.ListBranchesAsync());
    }

    [Fact]
    public async Task RehydrateAsync_creates_the_candidate_branch_when_promotion_branch_is_enabled()
    {
        var (candidateBranch, options, branches, _) = await BuildAsync();
        options.UsePromotionBranch = true;

        await candidateBranch.RehydrateAsync();

        Assert.Contains(options.CandidateBranchId, await branches.ListBranchesAsync());
    }

    [Fact]
    public async Task Promoting_applies_the_candidate_branchs_files_onto_main()
    {
        var (candidateBranch, options, _, fileWorkspace) = await BuildAsync();
        await fileWorkspace.InitBranchAsync("main");
        options.UsePromotionBranch = true;
        await candidateBranch.EnsureAsync();
        await fileWorkspace.WriteAsync(options.CandidateBranchId, "src/feature.cs", "// promoted change");

        // Mirrors the /studio/branches/candidate/promote endpoint body exactly.
        await fileWorkspace.ApplyBranchAsync(options.CandidateBranchId, "main");

        Assert.True(await fileWorkspace.ExistsAsync("main", "src/feature.cs"));
    }
}
