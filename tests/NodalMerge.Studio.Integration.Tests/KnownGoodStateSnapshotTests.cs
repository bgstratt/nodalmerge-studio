using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 13e — KnownGoodState previously captured no restorable snapshot reference;
/// CheckoutKnownGoodAsync was read-only metadata lookup that restored nothing.
/// MarkKnownGoodAsync now creates a real point-in-time copy via IBranchService.CreateBranchAsync
/// (knowngood/{stateId}, seeded from the source branch's current files) and CheckoutKnownGoodAsync
/// copies that snapshot's content back onto the target branch via IFileWorkspaceService.ApplyBranchAsync.
/// </summary>
[Trait("Category", "Integration")]
public class KnownGoodStateSnapshotTests
{
    private static async Task<(IKnownGoodStateService KnownGood, IFileWorkspaceService FileWorkspace)> BuildAsync()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        return (
            app.Services.GetRequiredService<IKnownGoodStateService>(),
            app.Services.GetRequiredService<IFileWorkspaceService>());
    }

    [Fact]
    public async Task MarkKnownGoodAsync_seeds_a_snapshot_branch_from_the_sources_current_files()
    {
        var (knownGood, fileWorkspace) = await BuildAsync();
        var branchId = $"branch-{Guid.NewGuid():N}";
        await fileWorkspace.InitBranchAsync(branchId);
        await fileWorkspace.WriteAsync(branchId, "src/feature.cs", "// known good content");

        var state = new KnownGoodState(
            $"KGS-{Guid.NewGuid():N}", branchId, "checkpoint", null, DateTimeOffset.UtcNow, "test");

        var marked = await knownGood.MarkKnownGoodAsync(state);

        Assert.NotNull(marked.SnapshotBranchId);
        Assert.Equal(
            "// known good content",
            await fileWorkspace.ReadAsync(marked.SnapshotBranchId!, "src/feature.cs"));
    }

    [Fact]
    public async Task CheckoutKnownGoodAsync_reverts_an_edited_file_and_removes_a_file_added_afterward()
    {
        var (knownGood, fileWorkspace) = await BuildAsync();
        var branchId = $"branch-{Guid.NewGuid():N}";
        await fileWorkspace.InitBranchAsync(branchId);
        await fileWorkspace.WriteAsync(branchId, "src/feature.cs", "// known good content");

        var state = new KnownGoodState(
            $"KGS-{Guid.NewGuid():N}", branchId, "checkpoint before risky refactor", null,
            DateTimeOffset.UtcNow, "test");
        var marked = await knownGood.MarkKnownGoodAsync(state);

        // A risky edit, plus a brand-new file, after the known-good mark.
        await fileWorkspace.WriteAsync(branchId, "src/feature.cs", "// a risky, uncommitted edit");
        await fileWorkspace.WriteAsync(branchId, "src/scratch.cs", "// should not survive checkout");

        var checkedOut = await knownGood.CheckoutKnownGoodAsync(marked.StateId);

        Assert.NotNull(checkedOut);
        Assert.Equal("// known good content", await fileWorkspace.ReadAsync(branchId, "src/feature.cs"));
        Assert.False(await fileWorkspace.ExistsAsync(branchId, "src/scratch.cs"));
    }
}
