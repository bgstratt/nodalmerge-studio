using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Merge;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Fan-out siblings landing on the shared merge/{parentWorkUnitId} scratch branch can collide the
/// same way independent top-level goals collide on "candidate" — InMemoryMergeService.ApplyAsync's
/// checkForDrift already covered this case (it's the original fan-out-sibling scenario the check was
/// built for), but nothing recorded a queryable TaskConflictRecord or offered Reconcile/Resolve
/// Manually the way the candidate-branch case does. These tests cover the new task-level adapter,
/// including the MergeReconciliationService.TryReconcileAsync patch that lets its fold step follow a
/// superseded child proposal's SupersededBy pointer to the reconciliation proposal that replaced it.
/// </summary>
[Trait("Category", "Integration")]
public class TaskConflictReconciliationTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-taskconflict-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildApp() => StudioWebApplication.Build(
        [],
        configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Workspace:RootPath"] = _tempRoot,
        }),
        configureServices: s => s.AddInMemoryStorage());

    private static async Task<string> ProposeValidateApproveAsync(
        IWorkUnitService workUnits, IMergeCommandService mergeCommands, WorkUnit workUnit, string summary)
    {
        // MergeReconciliationService.TryReconcileAsync's own precondition requires the child
        // WorkUnit itself to be Proposed/Merged, not just its proposal — MergeCommandService
        // .ProposeAsync tries to set that, but WorkUnitTransitions has no direct Created -> Proposed
        // edge, so the work unit needs to already be Executing first (same dance
        // MergeReconciliationServiceTests's own helper uses).
        await workUnits.UpdateStatusAsync(workUnit.WorkUnitId, WorkUnitStatus.Queued);
        await workUnits.UpdateStatusAsync(workUnit.WorkUnitId, WorkUnitStatus.Executing);

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: workUnit.BranchId, targetBranch: "main", summary: summary, workUnitId: workUnit.WorkUnitId);
        await mergeCommands.ValidateAsync(proposal.ProposalId);
        await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        return proposal.ProposalId;
    }

    // For a work unit whose effective review policy is AgentApproval, MergeCommandService
    // .ProposeAsync's own Slice-20b auto-trigger races a background ApplyAsync against this test's
    // own explicit Validate/Review/Apply sequence — the reconciliation work unit created by
    // IReconciliationAgentService always carries AgentApproval by default. Bypassing the command
    // wrapper (raw IMergeService.ProposeAsync, same pattern MergeReconciliationServiceTests' own
    // helper uses) avoids the race entirely while still exercising the real ApplyAsync/
    // TryApplyAdditivelyAsync path this test actually cares about.
    private static async Task<string> ProposeRawValidateApproveAsync(
        IWorkUnitService workUnits, IMergeService merge, IArtifactLineageService artifacts,
        WorkUnit workUnit, string targetBranch, string summary)
    {
        var id = $"MP-{Guid.NewGuid():N}";
        var proposal = new MergeProposal(
            id, workUnit.BranchId, targetBranch, workUnit.Goal, summary, summary,
            null, null, null, MergeProposalStatus.Draft,
            // Must be set explicitly here — this raw path skips MergeCommandService.ProposeAsync's
            // own diff-based FilesTouched derivation, and an empty FilesTouched makes
            // MergeReconciliationService.DetectOverlappingFilesAsync see nothing to compare, letting
            // it reconcile (and supersede) both siblings before this test's own ApplyAsync/
            // TryApplyAdditivelyAsync conflict check ever gets a chance to run.
            FilesTouched: workUnit.FileScope,
            WorkUnitId: workUnit.WorkUnitId);
        await merge.ProposeAsync(proposal);
        await merge.ValidateAsync(id);
        await artifacts.RecordAsync(new ArtifactRef(
            id, ArtifactType.MergeProposal, workUnit.WorkUnitId, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, workUnit.WorkUnitId, null));
        await workUnits.UpdateStatusAsync(workUnit.WorkUnitId, WorkUnitStatus.Queued);
        await workUnits.UpdateStatusAsync(workUnit.WorkUnitId, WorkUnitStatus.Executing);
        await workUnits.UpdateStatusAsync(workUnit.WorkUnitId, WorkUnitStatus.Proposed);
        await merge.ReviewAsync(id, MergeProposalStatus.Approved);
        return id;
    }

    [Fact]
    public async Task Two_siblings_with_overlapping_lines_the_second_apply_throws_and_records_a_task_conflict()
    {
        var app = BuildApp();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var taskConflicts = app.Services.GetRequiredService<ITaskConflictService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\nline two\n");
        var parent = await orchestrator.CreateWorkUnitAsync("parent goal", "test", seedFromBranchId: "main");
        var child1 = await orchestrator.CreateWorkUnitAsync(
            "slice one", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["Shared.cs"], seedFromBranchId: parent.BranchId);
        var child2 = await orchestrator.CreateWorkUnitAsync(
            "slice two", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["Shared.cs"], seedFromBranchId: parent.BranchId);

        await fileWorkspace.WriteAsync(child1.BranchId, "Shared.cs", "line one changed by 1\nline two\n");
        await fileWorkspace.WriteAsync(child2.BranchId, "Shared.cs", "line one changed by 2\nline two\n");

        var p1 = await ProposeValidateApproveAsync(workUnits, mergeCommands, child1, "Change line one (1)");
        var p2 = await ProposeValidateApproveAsync(workUnits, mergeCommands, child2, "Change line one (2)");

        await mergeCommands.ApplyAsync(p1);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(p2));
        Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);

        var open = await taskConflicts.GetOpenAsync(parent.WorkUnitId);
        var recorded = Assert.Single(open);
        Assert.Equal(p2, recorded.ProposalId);
        Assert.Equal(p1, recorded.WinningProposalId);
        Assert.Equal(parent.WorkUnitId, recorded.ParentWorkUnitId);
        Assert.Contains("Shared.cs", recorded.ConflictingPaths);
    }

    [Fact]
    public async Task Reconcile_folds_both_siblings_and_TryReconcileAsync_succeeds_via_the_reconciliation_stand_in()
    {
        var app = BuildApp();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var taskConflicts = app.Services.GetRequiredService<ITaskConflictService>();
        var taskReconciliationTrigger = app.Services.GetRequiredService<ITaskReconciliationTrigger>();
        var reconciliationService = app.Services.GetRequiredService<IMergeReconciliationService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\nline two\n");
        var parent = await orchestrator.CreateWorkUnitAsync("parent goal", "test", seedFromBranchId: "main");
        var child1 = await orchestrator.CreateWorkUnitAsync(
            "slice one", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["Shared.cs"], seedFromBranchId: parent.BranchId);
        var child2 = await orchestrator.CreateWorkUnitAsync(
            "slice two", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["Shared.cs"], seedFromBranchId: parent.BranchId);

        await fileWorkspace.WriteAsync(child1.BranchId, "Shared.cs", "line one changed by 1\nline two\n");
        await fileWorkspace.WriteAsync(child2.BranchId, "Shared.cs", "line one changed by 2\nline two\n");

        var p1 = await ProposeValidateApproveAsync(workUnits, mergeCommands, child1, "Change line one (1)");
        var p2 = await ProposeValidateApproveAsync(workUnits, mergeCommands, child2, "Change line one (2)");
        await mergeCommands.ApplyAsync(p1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(p2));

        var conflict = Assert.Single(await taskConflicts.GetOpenAsync(parent.WorkUnitId));

        var reconciliationChild = await taskReconciliationTrigger.TryTriggerAsync(conflict.ConflictId);
        Assert.NotNull(reconciliationChild);
        Assert.Equal(parent.WorkUnitId, reconciliationChild!.ParentWorkUnitId);
        Assert.Equal($"task-conflict:{conflict.ConflictId}", reconciliationChild.ReconciliationSourceRef);

        const string combined = "line one changed by 1 and 2\nline two\n";
        await fileWorkspace.WriteAsync(reconciliationChild.BranchId, "Shared.cs", combined);
        var reconciliationProposal = await ProposeRawValidateApproveAsync(
            workUnits, merge, artifacts, reconciliationChild, $"merge/{parent.WorkUnitId}", "Reconciled line one");
        await mergeCommands.ApplyAsync(reconciliationProposal);

        // The conflict is resolved and both original siblings superseded by the reconciliation
        // proposal, purely from the reconciliation child's own apply — before TryReconcileAsync ever
        // runs again.
        Assert.Empty(await taskConflicts.GetOpenAsync(parent.WorkUnitId));
        var p1After = await merge.GetAsync(p1);
        var p2After = await merge.GetAsync(p2);
        Assert.Equal(MergeProposalStatus.Superseded, p1After!.Status);
        Assert.Equal(reconciliationProposal, p1After.SupersededBy);
        Assert.Equal(MergeProposalStatus.Superseded, p2After!.Status);
        Assert.Equal(reconciliationProposal, p2After.SupersededBy);

        // The real test: MergeReconciliationService.TryReconcileAsync must now succeed despite two of
        // its three children (p1, p2) having Superseded proposals — by following SupersededBy to the
        // still-live reconciliation proposal and deduping the two children down to one fold entry.
        var result = await reconciliationService.TryReconcileAsync(parent.WorkUnitId);
        Assert.True(
            result.Outcome is MergeReconciliationOutcome.Reconciled or MergeReconciliationOutcome.AlreadyReconciled,
            $"Expected Reconciled or AlreadyReconciled, got {result.Outcome}.");

        var finalProposal = await merge.GetAsync(result.ReconciledProposalId!);
        Assert.NotNull(finalProposal);
        Assert.Contains("Shared.cs", finalProposal!.FilesTouched);
        Assert.Equal(combined, await fileWorkspace.ReadAsync($"merge/{parent.WorkUnitId}", "Shared.cs"));

        // The reconciliation proposal itself was folded and superseded by the final batch proposal —
        // p1/p2 stay pointed at the reconciliation proposal (unchanged), not directly at the final one.
        var reconciliationProposalAfter = await merge.GetAsync(reconciliationProposal);
        Assert.Equal(MergeProposalStatus.Superseded, reconciliationProposalAfter!.Status);
        Assert.Equal(result.ReconciledProposalId, reconciliationProposalAfter.SupersededBy);
    }

    [Fact]
    public async Task ResolveManually_writes_to_a_scratch_branch_and_TryReconcileAsync_succeeds()
    {
        var app = BuildApp();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var taskConflicts = app.Services.GetRequiredService<ITaskConflictService>();
        var taskReconciliationTrigger = app.Services.GetRequiredService<ITaskReconciliationTrigger>();
        var reconciliationService = app.Services.GetRequiredService<IMergeReconciliationService>();

        await fileWorkspace.WriteAsync("main", "burns.md", "");
        var parent = await orchestrator.CreateWorkUnitAsync("parent goal", "test", seedFromBranchId: "main");
        var child1 = await orchestrator.CreateWorkUnitAsync(
            "burns about Brad", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["burns.md"], seedFromBranchId: parent.BranchId);
        var child2 = await orchestrator.CreateWorkUnitAsync(
            "burns about Jake", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["burns.md"], seedFromBranchId: parent.BranchId);

        await fileWorkspace.WriteAsync(child1.BranchId, "burns.md", "Brad burns\n");
        await fileWorkspace.WriteAsync(child2.BranchId, "burns.md", "Jake burns\n");

        var p1 = await ProposeValidateApproveAsync(workUnits, mergeCommands, child1, "Brad");
        var p2 = await ProposeValidateApproveAsync(workUnits, mergeCommands, child2, "Jake");
        await mergeCommands.ApplyAsync(p1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(p2));

        var conflict = Assert.Single(await taskConflicts.GetOpenAsync(parent.WorkUnitId));

        const string combined = "Brad burns\nJake burns\n";
        var resolution = await taskReconciliationTrigger.TryResolveManuallyAsync(
            conflict.ConflictId, new Dictionary<string, string> { ["burns.md"] = combined });

        Assert.NotNull(resolution);
        Assert.Equal(MergeProposalStatus.Merged, resolution!.Status);
        Assert.Empty(await taskConflicts.GetOpenAsync(parent.WorkUnitId));

        var result = await reconciliationService.TryReconcileAsync(parent.WorkUnitId);
        Assert.True(
            result.Outcome is MergeReconciliationOutcome.Reconciled or MergeReconciliationOutcome.AlreadyReconciled,
            $"Expected Reconciled or AlreadyReconciled, got {result.Outcome}.");

        Assert.Equal(combined, await fileWorkspace.ReadAsync($"merge/{parent.WorkUnitId}", "burns.md"));
    }

    [Fact]
    public async Task Reconcile_is_a_no_op_re_entrancy_guard_when_already_reconciling_or_resolved()
    {
        var app = BuildApp();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var taskConflicts = app.Services.GetRequiredService<ITaskConflictService>();
        var taskReconciliationTrigger = app.Services.GetRequiredService<ITaskReconciliationTrigger>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var parent = await orchestrator.CreateWorkUnitAsync("parent goal", "test", seedFromBranchId: "main");
        var child1 = await orchestrator.CreateWorkUnitAsync(
            "slice one", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["Shared.cs"], seedFromBranchId: parent.BranchId);
        var child2 = await orchestrator.CreateWorkUnitAsync(
            "slice two", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["Shared.cs"], seedFromBranchId: parent.BranchId);

        await fileWorkspace.WriteAsync(child1.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(child2.BranchId, "Shared.cs", "B\n");
        var p1 = await ProposeValidateApproveAsync(workUnits, mergeCommands, child1, "A");
        var p2 = await ProposeValidateApproveAsync(workUnits, mergeCommands, child2, "B");
        await mergeCommands.ApplyAsync(p1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(p2));

        var conflict = Assert.Single(await taskConflicts.GetOpenAsync(parent.WorkUnitId));

        var first = await taskReconciliationTrigger.TryTriggerAsync(conflict.ConflictId);
        Assert.NotNull(first);

        var second = await taskReconciliationTrigger.TryTriggerAsync(conflict.ConflictId);
        Assert.Null(second);
    }

    [Fact]
    public async Task Conflict_between_two_AgentApproval_siblings_auto_triggers_reconciliation_without_a_human()
    {
        var app = BuildApp();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var taskConflicts = app.Services.GetRequiredService<ITaskConflictService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var parent = await orchestrator.CreateWorkUnitAsync("parent goal", "test", seedFromBranchId: "main");
        var child1 = await orchestrator.CreateWorkUnitAsync(
            "slice one", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["Shared.cs"],
            seedFromBranchId: parent.BranchId, taskReviewPolicy: ReviewPolicy.AgentApproval);
        var child2 = await orchestrator.CreateWorkUnitAsync(
            "slice two", "test", parentWorkUnitId: parent.WorkUnitId, fileScope: ["Shared.cs"],
            seedFromBranchId: parent.BranchId, taskReviewPolicy: ReviewPolicy.AgentApproval);

        await fileWorkspace.WriteAsync(child1.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(child2.BranchId, "Shared.cs", "B\n");
        // Raw IMergeService path (not the mergeCommands wrapper) — AgentApproval here would
        // otherwise race MergeCommandService.ProposeAsync's own background auto-apply, same as
        // the reconciliation child's proposal above.
        var p1 = await ProposeRawValidateApproveAsync(workUnits, merge, artifacts, child1, $"merge/{parent.WorkUnitId}", "A");
        var p2 = await ProposeRawValidateApproveAsync(workUnits, merge, artifacts, child2, $"merge/{parent.WorkUnitId}", "B");
        await mergeCommands.ApplyAsync(p1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(p2));

        var conflict = Assert.Single(await taskConflicts.GetOpenAsync(parent.WorkUnitId));
        Assert.Equal(TaskConflictStatus.Reconciling, conflict.Status);

        var siblings = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        Assert.Contains(siblings, w => w.ReconciliationSourceRef == $"task-conflict:{conflict.ConflictId}");
    }
}
