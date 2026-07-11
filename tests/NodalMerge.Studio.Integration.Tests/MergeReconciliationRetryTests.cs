using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// merge/{parentWorkUnitId} is a fixed branch name reused across a goal's entire lifetime. After a
/// reconciled proposal is rejected and its children are retried, that branch must not carry forward
/// the previous (rejected) attempt's content — a file only the old attempt touched must not linger
/// into the new attempt's reconciled diff, even though InitBranchAsync-style seed-if-empty semantics
/// would otherwise leave it there untouched. Covers MergeReconciliationService's full-mirror reset
/// (ApplyBranchAsync replacing InitBranchAsync) and AutomatedReviewGateService's matching reset at
/// retry-kickoff time.
/// </summary>
[Trait("Category", "Integration")]
public class MergeReconciliationRetryTests
{
    [Fact]
    public async Task Retrying_a_rejected_reconciled_proposal_does_not_carry_forward_the_old_attempts_files()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var workUnits        = app.Services.GetRequiredService<IWorkUnitService>();
        var mergeCommands    = app.Services.GetRequiredService<IMergeCommandService>();
        var merge            = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace    = app.Services.GetRequiredService<IFileWorkspaceService>();
        var reconciliation   = app.Services.GetRequiredService<IMergeReconciliationService>();
        var reviewGate       = app.Services.GetRequiredService<IAutomatedReviewGateService>();

        var parent = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Parent goal", "test"));
        var child1 = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Child 1", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));
        var child2 = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Child 2", "test", ParentWorkUnitId: parent.WorkUnitId, SeedFromBranchId: parent.BranchId));

        // A HumanRequired child (the default) is never individually Applied in production — approval
        // is the last touch on its own proposal; reconciliation is what actually copies its content
        // in. Approving the *last* child can itself auto-trigger reconciliation (InMemoryMergeService
        // .TryRetriggerParentReconciliationAsync), so this helper deliberately stops at Review.
        async Task ProposeAndApproveAsync(WorkUnit child, string file, string content)
        {
            await fileWorkspace.WriteAsync(child.BranchId, file, content);
            // Fresh (Created, from CreateAsync) needs Queued first; already-retried (Queued, from
            // HandleHumanRejectionAsync's reset) doesn't — (Queued, Queued) isn't a legal transition.
            var current = await workUnits.GetAsync(child.WorkUnitId);
            if (current!.Status == WorkUnitStatus.Created)
                await workUnits.UpdateStatusAsync(child.WorkUnitId, WorkUnitStatus.Queued);
            await workUnits.UpdateStatusAsync(child.WorkUnitId, WorkUnitStatus.Executing);
            var proposal = await mergeCommands.ProposeAsync(
                sourceBranch: child.BranchId, targetBranch: "main", summary: $"Add {file}",
                workUnitId: child.WorkUnitId);
            await mergeCommands.ValidateAsync(proposal.ProposalId);
            await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        }

        // First attempt: child1 adds FileA.cs, child2 adds FileB.cs.
        await ProposeAndApproveAsync(child1, "FileA.cs", "// A, attempt 1");
        await ProposeAndApproveAsync(child2, "FileB.cs", "// B, attempt 1");

        // Approving child2 (the last one) may have already auto-triggered reconciliation — this
        // explicit call is then just confirming it happened (AlreadyReconciled is as valid as
        // Reconciled here, same as MergeReconciliationServiceTests).
        var firstResult = await reconciliation.TryReconcileAsync(parent.WorkUnitId);
        Assert.True(
            firstResult.Outcome is MergeReconciliationOutcome.Reconciled or MergeReconciliationOutcome.AlreadyReconciled,
            $"Expected Reconciled or AlreadyReconciled, got {firstResult.Outcome}.");

        var mergeBranch = $"merge/{parent.WorkUnitId}";
        Assert.Equal("// A, attempt 1", await fileWorkspace.ReadAsync(mergeBranch, "FileA.cs"));
        Assert.Equal("// B, attempt 1", await fileWorkspace.ReadAsync(mergeBranch, "FileB.cs"));

        // Reject the reconciled proposal — mergeCommands.ReviewAsync itself doesn't trigger a retry
        // (only the REST /review endpoint chains reviewGate.HandleHumanRejectionAsync after it), so
        // call it directly here since this test drives the command services, not the REST layer.
        // RestartMode.Revert: the default Revise mode leaves each child's own branch untouched (an
        // agent building on its near-miss attempt), so child2's branch would still carry FileB.cs
        // forward regardless of the merge-branch reset under test — Revert resets each child's own
        // branch back to its pre-attempt snapshot too, matching "the plan changed, start clean".
        await mergeCommands.ReviewAsync(firstResult.ReconciledProposalId!, "Rejected");
        await reviewGate.HandleHumanRejectionAsync(
            firstResult.ReconciledProposalId!, reviewNotes: "Wrong approach.", mode: RestartMode.Revert);

        var child1AfterReject = await workUnits.GetAsync(child1.WorkUnitId);
        var child2AfterReject = await workUnits.GetAsync(child2.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Queued, child1AfterReject!.Status);
        Assert.Equal(WorkUnitStatus.Queued, child2AfterReject!.Status);

        // Second attempt: the plan changed — child2 no longer touches FileB.cs at all, it now
        // writes FileC.cs instead. child1's own file is unchanged.
        await ProposeAndApproveAsync(child1, "FileA.cs", "// A, attempt 2");
        await ProposeAndApproveAsync(child2, "FileC.cs", "// C, attempt 2");

        var secondResult = await reconciliation.TryReconcileAsync(parent.WorkUnitId);
        Assert.True(
            secondResult.Outcome is MergeReconciliationOutcome.Reconciled or MergeReconciliationOutcome.AlreadyReconciled,
            $"Expected Reconciled or AlreadyReconciled, got {secondResult.Outcome}.");

        Assert.Equal("// A, attempt 2", await fileWorkspace.ReadAsync(mergeBranch, "FileA.cs"));
        Assert.Equal("// C, attempt 2", await fileWorkspace.ReadAsync(mergeBranch, "FileC.cs"));
        // FileB.cs was only ever part of the rejected first attempt — it must not linger.
        Assert.False(await fileWorkspace.ExistsAsync(mergeBranch, "FileB.cs"));

        var secondReconciled = await merge.GetAsync(secondResult.ReconciledProposalId!);
        Assert.DoesNotContain("FileB.cs", secondReconciled!.FilesTouched);
    }
}
