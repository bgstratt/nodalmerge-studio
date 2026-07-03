using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 4 item 3 (plans/orchestrator-reliability-and-observability.md) — found via a live
/// multi-sibling run: applying one sibling's proposal after another used to silently revert the
/// first sibling's changes, because InMemoryMergeService.ApplyAsync landed proposals via a
/// destructive full-mirror (ApplyBranchAsync — "delete anything absent from source"). Fixed to
/// land additively. These tests reproduce the exact bug shape directly (two independent fan-out
/// siblings, applied one at a time) and confirm the new conflict detection fires for a genuine
/// overlapping edit.
/// </summary>
[Trait("Category", "Integration")]
public class SiblingProposalApplyTests
{
    [Fact]
    public async Task Applying_a_second_sibling_proposal_does_not_revert_the_first_siblings_files()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands    = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace    = app.Services.GetRequiredService<IFileWorkspaceService>();

        var parent = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Parent goal", "test"));
        await fileWorkspace.WriteAsync(parent.BranchId, "Shared.cs", "// shared, untouched by either sibling");

        var siblingA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling A", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));
        var siblingB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling B", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));

        // Genuinely disjoint files — neither sibling's branch ever sees the other's change, since
        // only *declared dependencies* get refreshed (FanOutService.RefreshBranchFromDependenciesAsync)
        // and these two are independent.
        await fileWorkspace.WriteAsync(siblingA.BranchId, "FileA.cs", "// added by sibling A");
        await fileWorkspace.WriteAsync(siblingB.BranchId, "FileB.cs", "// added by sibling B");

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: siblingA.BranchId, targetBranch: parent.BranchId, summary: "Add FileA",
            workUnitId: siblingA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: siblingB.BranchId, targetBranch: parent.BranchId, summary: "Add FileB",
            workUnitId: siblingB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        // Apply one at a time, exactly like a human approving each sibling individually as it
        // finishes — this is the exact sequencing that used to trigger the bug.
        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await mergeCommands.ApplyAsync(proposalB.ProposalId);

        Assert.Equal("// added by sibling A", await fileWorkspace.ReadAsync(parent.BranchId, "FileA.cs"));
        Assert.Equal("// added by sibling B", await fileWorkspace.ReadAsync(parent.BranchId, "FileB.cs"));
        Assert.Equal("// shared, untouched by either sibling", await fileWorkspace.ReadAsync(parent.BranchId, "Shared.cs"));
    }

    [Fact]
    public async Task Applying_a_sibling_proposal_that_genuinely_overlaps_an_already_landed_sibling_throws()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands    = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace    = app.Services.GetRequiredService<IFileWorkspaceService>();

        var parent = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Parent goal", "test"));
        await fileWorkspace.WriteAsync(parent.BranchId, "Shared.cs", "line one\nline two\nline three\n");

        var siblingA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling A", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));
        var siblingB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling B", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));

        // Both siblings edit the SAME line of the SAME file — a genuine overlap, not just a
        // same-file-different-region edit.
        await fileWorkspace.WriteAsync(siblingA.BranchId, "Shared.cs", "line one changed by A\nline two\nline three\n");
        await fileWorkspace.WriteAsync(siblingB.BranchId, "Shared.cs", "line one changed by B\nline two\nline three\n");

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: siblingA.BranchId, targetBranch: parent.BranchId, summary: "Change line one (A)",
            workUnitId: siblingA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: siblingB.BranchId, targetBranch: parent.BranchId, summary: "Change line one (B)",
            workUnitId: siblingB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        await mergeCommands.ApplyAsync(proposalA.ProposalId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mergeCommands.ApplyAsync(proposalB.ProposalId));
        Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Shared.cs", ex.Message);

        // A's change must survive the failed apply attempt — the conflict must block, not partially land.
        Assert.Equal("line one changed by A\nline two\nline three\n", await fileWorkspace.ReadAsync(parent.BranchId, "Shared.cs"));
    }
}
