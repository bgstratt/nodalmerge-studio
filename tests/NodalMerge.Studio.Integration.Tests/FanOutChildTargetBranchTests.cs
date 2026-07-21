using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// A fan-out task child must never be able to land content directly on "main" — regardless of
/// what targetBranch it (or the agent driving it) asks for — because TaskReviewPolicy.AgentApproval
/// /Hybrid auto-triggers that child's own ApplyAsync immediately after ProposeAsync, and "main" is
/// the shared, disk-mirrored branch other work reads as canonical. MergeCommandService.ProposeAsync
/// unconditionally redirects a child's proposal onto merge/{parentWorkUnitId} instead — the same
/// scratch branch MergeReconciliationService itself builds — so unreviewed content can never reach
/// "main" ahead of the goal's own WorkspaceReviewPolicy gate.
/// </summary>
[Trait("Category", "Integration")]
public class FanOutChildTargetBranchTests
{
    [Fact]
    public async Task ProposeAsync_for_fan_out_child_redirects_target_branch_and_leaves_main_untouched()
    {
        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands    = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace    = app.Services.GetRequiredService<IFileWorkspaceService>();

        var parent = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Parent goal", "test"));
        var child = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Task child", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));

        await fileWorkspace.WriteAsync(child.BranchId, "Child.cs", "// added by the child");

        // Explicitly ask to target "main" — exactly what a real agent does per the MCP tool schema's
        // "usually main" guidance — and confirm the server ignores it for a fan-out child.
        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: child.BranchId, targetBranch: "main", summary: "Add Child.cs",
            workUnitId: child.WorkUnitId);

        Assert.Equal($"merge/{parent.WorkUnitId}", proposal.TargetBranch);

        await mergeCommands.ValidateAsync(proposal.ProposalId);
        await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposal.ProposalId);

        Assert.Equal("// added by the child", await fileWorkspace.ReadAsync($"merge/{parent.WorkUnitId}", "Child.cs"));
        Assert.False(await fileWorkspace.ExistsAsync("main", "Child.cs"));
    }

    [Fact]
    public async Task ProposeAsync_for_top_level_goal_respects_the_explicit_target_branch()
    {
        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands    = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace    = app.Services.GetRequiredService<IFileWorkspaceService>();

        var goal = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Solo goal", "test"));
        await fileWorkspace.WriteAsync(goal.BranchId, "Solo.cs", "// added by the solo goal");

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: goal.BranchId, targetBranch: "main", summary: "Add Solo.cs",
            workUnitId: goal.WorkUnitId);

        // A plain top-level goal (no ParentWorkUnitId) is exactly what WorkspaceReviewScope calls
        // "applies to real repo" — the override must not touch it.
        Assert.Equal("main", proposal.TargetBranch);
    }
}
