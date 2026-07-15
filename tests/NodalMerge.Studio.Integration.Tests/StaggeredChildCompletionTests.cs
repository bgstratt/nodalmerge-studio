using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers a live-observed gap: a goal whose children land staggered over time (not all
/// simultaneously Proposed, e.g. dependency-gated fan-out under HumanRequired review) never got
/// marked done, because the only completion signal in the codebase was
/// MergeReconciliationService's combined MergeProposal — which only fires when every child is
/// simultaneously Proposed/Merged in one TryReconcileAsync call (see
/// plans/orchestrator-reliability-and-observability.md's "structural conflict" note). The
/// orchestrator's own LLM turn is the only thing that acts on that signal, so a goal whose children
/// never line up in one snapshot sits forever with no active agent and no parked work — invisible
/// to both the automatic completion path and a human clicking "Reinvoke Orchestrator," since a
/// fresh orchestrator turn just sees the same stale, unchanged projection every time.
///
/// The first test below covers the case where reconciliation eventually DOES catch up (both
/// children individually reach Approved/Merged and the second Approve is enough for one
/// TryReconcileAsync call to see both ready) — the parent's own combined-proposal apply still lands
/// it on Merged normally, exercising InMemoryMergeService.TryCompleteParentIfAllChildrenTerminalAsync
/// only as a defensive no-op. The second test covers the actual independent-of-reconciliation
/// signal: a child that individually applies while a sibling is still failing/dead-lettered must
/// never be silently treated as done.
/// </summary>
[Trait("Category", "Integration")]
public class StaggeredChildCompletionTests
{
    [Fact]
    public async Task Parent_reaches_a_terminal_state_once_the_last_staggered_sibling_applies()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();

        var parent = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Parent goal", "test"));
        // Executing, not just Active — a top-level reconciled proposal's own apply transitions its
        // owning work unit straight from Executing to Merged (WorkUnitTransitions.CanTransition has
        // no Active->Merged edge; a real orchestrator work unit reaches Executing itself before its
        // children ever fan out).
        await workUnits.UpdateStatusAsync(parent.WorkUnitId, WorkUnitStatus.Queued, cancellationToken: default);
        await workUnits.UpdateStatusAsync(parent.WorkUnitId, WorkUnitStatus.Executing, cancellationToken: default);

        var siblingA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling A", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));
        var siblingB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling B", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));

        await workUnits.UpdateStatusAsync(siblingA.WorkUnitId, WorkUnitStatus.Queued, cancellationToken: default);
        await workUnits.UpdateStatusAsync(siblingA.WorkUnitId, WorkUnitStatus.Executing, cancellationToken: default);
        await workUnits.UpdateStatusAsync(siblingB.WorkUnitId, WorkUnitStatus.Queued, cancellationToken: default);
        await workUnits.UpdateStatusAsync(siblingB.WorkUnitId, WorkUnitStatus.Executing, cancellationToken: default);

        await fileWorkspace.WriteAsync(siblingA.BranchId, "FileA.cs", "// added by sibling A");
        await fileWorkspace.WriteAsync(siblingB.BranchId, "FileB.cs", "// added by sibling B");

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: siblingA.BranchId, targetBranch: parent.BranchId, summary: "Add FileA",
            workUnitId: siblingA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: siblingB.BranchId, targetBranch: parent.BranchId, summary: "Add FileB",
            workUnitId: siblingB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");

        // Only A applies for now — mirrors a human approving+applying one sibling hours before the
        // other even reaches review, exactly the staggered timing that leaves
        // MergeReconciliationService.TryReconcileAsync stuck at WaitingForChildren forever (B's own
        // proposal is still Draft here, so reconciliation can't combine them yet).
        await mergeCommands.ApplyAsync(proposalA.ProposalId);

        var afterFirst = await workUnits.GetAsync(parent.WorkUnitId);
        Assert.NotEqual(WorkUnitStatus.Completed, afterFirst!.Status);

        var merge = app.Services.GetRequiredService<IMergeService>();
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        // Reviewing B now — with A already Merged and B just Approved — makes reconciliation's
        // all-current-children-ready condition true for the first time, so
        // TryRetriggerParentReconciliationAsync (fired synchronously off B's own Approve) may have
        // already folded both into one combined reconciled proposal and superseded B's own. Apply
        // whichever proposal is actually still live: the reconciled one if B was superseded, B
        // itself otherwise (e.g. if reconciliation genuinely never combines them in some other run).
        var latestB = await merge.GetAsync(proposalB.ProposalId);
        var toApply = latestB is { Status: MergeProposalStatus.Superseded, SupersededBy: { } supersededId }
            ? supersededId
            : proposalB.ProposalId;

        // A freshly reconciled combined proposal only reaches ReadyForReview (ValidateAsync), not
        // Approved — MergeReconciliationService's own auto-apply only fires for AgentApproval/Hybrid
        // policies, and this goal's default is HumanRequired. Approve it like a human would before
        // applying, same as proposalB itself would have needed if reconciliation hadn't combined it.
        var toApplyProposal = await merge.GetAsync(toApply);
        if (toApplyProposal?.Status == MergeProposalStatus.ReadyForReview)
            await mergeCommands.ReviewAsync(toApply, "Approved");

        await mergeCommands.ApplyAsync(toApply);

        // Either terminal status is a correct outcome here: Merged if reconciliation caught up and
        // combined both into one applied proposal (the case this run actually exercises), Completed
        // if TryCompleteParentIfAllChildrenTerminalAsync's independent signal got there first (e.g.
        // under different timing/ordering). What must never happen is staying non-terminal forever.
        var afterLast = await workUnits.GetAsync(parent.WorkUnitId);
        Assert.True(
            afterLast!.Status is WorkUnitStatus.Merged or WorkUnitStatus.Completed,
            $"Expected a terminal status, got {afterLast.Status}.");
    }

    [Fact]
    public async Task Parent_is_not_completed_while_a_child_is_still_dead_lettered()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var deadLetter = app.Services.GetRequiredService<IDeadLetterService>();

        var parent = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Parent goal", "test"));
        await workUnits.UpdateStatusAsync(parent.WorkUnitId, WorkUnitStatus.Active, cancellationToken: default);

        var siblingA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling A", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));
        var siblingB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling B", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));

        // Sibling B never produces a proposal at all — it dead-lettered instead.
        await workUnits.UpdateStatusAsync(siblingB.WorkUnitId, WorkUnitStatus.Queued, cancellationToken: default);
        await workUnits.UpdateStatusAsync(siblingB.WorkUnitId, WorkUnitStatus.Executing, cancellationToken: default);
        await deadLetter.RecordFailureAsync(
            siblingB.WorkUnitId, "agent-1", PipelineStage.Execute, "worker", "boom");

        await workUnits.UpdateStatusAsync(siblingA.WorkUnitId, WorkUnitStatus.Queued, cancellationToken: default);
        await workUnits.UpdateStatusAsync(siblingA.WorkUnitId, WorkUnitStatus.Executing, cancellationToken: default);
        await fileWorkspace.WriteAsync(siblingA.BranchId, "FileA.cs", "// added by sibling A");
        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: siblingA.BranchId, targetBranch: parent.BranchId, summary: "Add FileA",
            workUnitId: siblingA.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);

        // A dead-lettered sibling must never be silently treated as "done" — a real failure is
        // still sitting in the tree.
        var parentAfter = await workUnits.GetAsync(parent.WorkUnitId);
        Assert.NotEqual(WorkUnitStatus.Completed, parentAfter!.Status);
    }

    /// <summary>
    /// Regression (found live 2026-07-13, first real agent rejection at the workspace gate): when
    /// the last child's apply makes every child terminal, TryCompleteParentIfAllChildrenTerminalAsync
    /// reconciles and — pre-fix — marked the parent Completed immediately, even though the freshly
    /// reconciled workspace proposal was still unreviewed. Completed is terminal, so when the
    /// reviewer then rejected that proposal, the rejection retry's Executing write silently failed
    /// and the goal wedged forever: rejected proposal, nothing running, no way back. The parent
    /// must stay non-Completed until its workspace proposal is Approved/Merged.
    /// </summary>
    [Fact]
    public async Task Parent_is_not_completed_while_its_reconciled_workspace_proposal_awaits_review()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var merge = app.Services.GetRequiredService<IMergeService>();

        var parent = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Parent goal", "test"));
        await workUnits.UpdateStatusAsync(parent.WorkUnitId, WorkUnitStatus.Queued, cancellationToken: default);
        await workUnits.UpdateStatusAsync(parent.WorkUnitId, WorkUnitStatus.Executing, cancellationToken: default);

        var siblingA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling A", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));
        var siblingB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Sibling B", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));
        foreach (var sibling in new[] { siblingA, siblingB })
        {
            await workUnits.UpdateStatusAsync(sibling.WorkUnitId, WorkUnitStatus.Queued, cancellationToken: default);
            await workUnits.UpdateStatusAsync(sibling.WorkUnitId, WorkUnitStatus.Executing, cancellationToken: default);
        }

        await fileWorkspace.WriteAsync(siblingA.BranchId, "FileA.cs", "// added by sibling A");
        await fileWorkspace.WriteAsync(siblingB.BranchId, "FileB.cs", "// added by sibling B");

        // A applies alone first (B not yet proposed, so nothing can combine), same staggered
        // shape as the first test in this file.
        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: siblingA.BranchId, targetBranch: parent.BranchId, summary: "Add FileA",
            workUnitId: siblingA.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);

        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: siblingB.BranchId, targetBranch: parent.BranchId, summary: "Add FileB",
            workUnitId: siblingB.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        // Two legal interleavings from here (same fork the first test navigates): B's Approve may
        // have synchronously combined A+B into a reconciled parent proposal (superseding B), or —
        // the live ordering that exposed the bug — B still applies individually, making every
        // child terminal, which fires TryCompleteParentIfAllChildrenTerminalAsync and creates the
        // reconciled proposal there. Pre-fix, the second interleaving marked the parent Completed
        // with that proposal still unreviewed.
        var latestB = await merge.GetAsync(proposalB.ProposalId);
        if (latestB?.Status is not MergeProposalStatus.Superseded)
            await mergeCommands.ApplyAsync(proposalB.ProposalId);

        // Either way a reconciled parent proposal now exists, unreviewed — and the parent must
        // NOT be Completed while that review gate is open.
        var workspaceProposal = (await merge.ListAsync())
            .FirstOrDefault(p => p.WorkUnitId == parent.WorkUnitId
                && p.Status is MergeProposalStatus.Draft or MergeProposalStatus.ReadyForReview or MergeProposalStatus.UnderReview);
        Assert.NotNull(workspaceProposal);
        var parentPending = await workUnits.GetAsync(parent.WorkUnitId);
        Assert.NotEqual(WorkUnitStatus.Completed, parentPending!.Status);

        // The reviewer rejects — the live scenario. The parent must still be recoverable
        // (non-Completed), so the rejection-retry machinery's status writes can actually land.
        await mergeCommands.ReviewAsync(workspaceProposal!.ProposalId, "Rejected");
        var parentAfterReject = await workUnits.GetAsync(parent.WorkUnitId);
        Assert.NotEqual(WorkUnitStatus.Completed, parentAfterReject!.Status);
    }
}
