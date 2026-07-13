using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// With WorkspaceOptions.UsePromotionBranch on, independent top-level goals all land on the same
/// shared "candidate" branch — but InMemoryMergeService.ApplyAsync's drift/conflict check
/// (checkForDrift) was scoped to fan-out siblings only (ParentWorkUnitId set), so two unrelated
/// concurrent goals colliding on candidate went completely undetected. Fixed: checkForDrift now
/// also covers the candidate-branch case, and a genuine overlap persists a queryable
/// CandidateConflictRecord in addition to the existing report-file + thrown-exception behavior.
/// </summary>
[Trait("Category", "Integration")]
public class CandidateBranchConflictTests
{
    [Fact]
    public async Task Two_independent_goals_with_non_overlapping_files_both_land_on_candidate()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test"));

        await fileWorkspace.WriteAsync(goalA.BranchId, "FileA.cs", "// added by goal A");
        await fileWorkspace.WriteAsync(goalB.BranchId, "FileB.cs", "// added by goal B");

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: goalA.BranchId, targetBranch: "main", summary: "Add FileA", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: goalB.BranchId, targetBranch: "main", summary: "Add FileB", workUnitId: goalB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await mergeCommands.ApplyAsync(proposalB.ProposalId);

        Assert.Equal("// added by goal A", await fileWorkspace.ReadAsync(options.CandidateBranchId, "FileA.cs"));
        Assert.Equal("// added by goal B", await fileWorkspace.ReadAsync(options.CandidateBranchId, "FileB.cs"));

        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();
        Assert.Empty(await candidateConflicts.GetOpenAsync());
    }

    [Fact]
    public async Task Two_independent_goals_with_overlapping_lines_the_second_apply_throws_and_records_a_conflict()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\nline two\nline three\n");

        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));

        // Both edit the same line of the same file — a genuine overlap.
        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "line one changed by A\nline two\nline three\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "line one changed by B\nline two\nline three\n");

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: goalA.BranchId, targetBranch: "main", summary: "Change line one (A)", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: goalB.BranchId, targetBranch: "main", summary: "Change line one (B)", workUnitId: goalB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        await mergeCommands.ApplyAsync(proposalA.ProposalId);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => mergeCommands.ApplyAsync(proposalB.ProposalId));
        Assert.Contains("conflicts", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Shared.cs", ex.Message);

        // A's change must survive — the conflict must block B, not partially land or revert A.
        Assert.Equal(
            "line one changed by A\nline two\nline three\n",
            await fileWorkspace.ReadAsync(options.CandidateBranchId, "Shared.cs"));

        var open = await candidateConflicts.GetOpenAsync();
        var recorded = Assert.Single(open);
        Assert.Equal(proposalB.ProposalId, recorded.ProposalId);
        Assert.Contains("Shared.cs", recorded.ConflictingPaths);
        Assert.Equal(proposalA.ProposalId, recorded.WinningProposalId);
        // Both goals defaulted to WorkspaceReviewPolicy.HumanRequired — the auto-trigger only fires
        // when both sides are AgentApproval, so this conflict must stay Open for a human to act on.
        Assert.Equal(CandidateConflictStatus.Open, recorded.Status);
    }

    /// <summary>
    /// The concurrent counterpart of the sequential overlap test above, pinning
    /// InMemoryMergeService's _applyGate: two applies racing each other used to BOTH read a
    /// pristine candidate, BOTH pass the drift check, and BOTH land — one goal's change silently
    /// clobbered, no conflict ever recorded. Reachable in production via ProposeAsync's
    /// fire-and-forget auto-apply (AgentApproval/Hybrid) racing a human's own Apply. Which
    /// proposal wins the race is legitimately nondeterministic; what must ALWAYS hold is that
    /// exactly one lands, the loser's apply throws, and the loser is recorded as a conflict.
    /// </summary>
    [Fact]
    public async Task Two_concurrent_applies_with_overlapping_lines_never_both_land_silently()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\nline two\nline three\n");

        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));

        const string contentA = "line one changed by A\nline two\nline three\n";
        const string contentB = "line one changed by B\nline two\nline three\n";
        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", contentA);
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", contentB);

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: goalA.BranchId, targetBranch: "main", summary: "Change line one (A)", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: goalB.BranchId, targetBranch: "main", summary: "Change line one (B)", workUnitId: goalB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        async Task<Exception?> TryApply(string proposalId)
        {
            try { await mergeCommands.ApplyAsync(proposalId); return null; }
            catch (Exception ex) { return ex; }
        }

        var applyTaskA = Task.Run(() => TryApply(proposalA.ProposalId));
        var applyTaskB = Task.Run(() => TryApply(proposalB.ProposalId));
        var outcomes = await Task.WhenAll(applyTaskA, applyTaskB);

        var failures = outcomes.Where(e => e is not null).ToList();
        var loserException = Assert.Single(failures);
        Assert.IsType<InvalidOperationException>(loserException);
        Assert.Contains("conflicts", loserException!.Message, StringComparison.OrdinalIgnoreCase);

        var winnerContent = outcomes[0] is null ? contentA : contentB;
        var loserProposalId = outcomes[0] is null ? proposalB.ProposalId : proposalA.ProposalId;
        Assert.Equal(winnerContent, await fileWorkspace.ReadAsync(options.CandidateBranchId, "Shared.cs"));

        var recorded = Assert.Single(await candidateConflicts.GetOpenAsync());
        Assert.Equal(loserProposalId, recorded.ProposalId);
        Assert.Contains("Shared.cs", recorded.ConflictingPaths);
    }

    /// <summary>
    /// Instead of blocking B and requiring a from-scratch restart, a reconciliation work unit can be
    /// created that sees both goals' intents and both diverged versions of Shared.cs, and produces
    /// one combined result. This test manually plays the "agent" part (write combined content,
    /// propose, approve, apply) to keep the test fast/deterministic — the reconciliation work unit
    /// itself is created via the real ICandidateReconciliationTrigger.
    /// </summary>
    [Fact]
    public async Task Reconcile_folds_both_goals_changes_and_resolves_the_conflict()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();
        var reconciliationTrigger = app.Services.GetRequiredService<ICandidateReconciliationTrigger>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\nline two\nline three\n");

        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));

        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "line one changed by A\nline two\nline three\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "line one changed by B\nline two\nline three\n");

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: goalA.BranchId, targetBranch: "main", summary: "Change line one (A)", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: goalB.BranchId, targetBranch: "main", summary: "Change line one (B)", workUnitId: goalB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(proposalB.ProposalId));

        var conflict = Assert.Single(await candidateConflicts.GetOpenAsync());

        var reconciliationWorkUnit = await reconciliationTrigger.TryTriggerAsync(conflict.ConflictId);
        Assert.NotNull(reconciliationWorkUnit);
        Assert.Equal($"candidate-conflict:{conflict.ConflictId}", reconciliationWorkUnit!.ReconciliationSourceRef);

        const string combined = "line one changed by A and B\nline two\nline three\n";
        await fileWorkspace.WriteAsync(reconciliationWorkUnit.BranchId, "Shared.cs", combined);

        var reconciliationProposal = await mergeCommands.ProposeAsync(
            sourceBranch: reconciliationWorkUnit.BranchId, targetBranch: "main", summary: "Reconciled",
            workUnitId: reconciliationWorkUnit.WorkUnitId);
        await mergeCommands.ValidateAsync(reconciliationProposal.ProposalId);
        await mergeCommands.ReviewAsync(reconciliationProposal.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(reconciliationProposal.ProposalId);

        Assert.Equal(combined, await fileWorkspace.ReadAsync(options.CandidateBranchId, "Shared.cs"));
        Assert.Empty(await candidateConflicts.GetOpenAsync());

        var supersededA = await merge.GetAsync(proposalA.ProposalId);
        var supersededB = await merge.GetAsync(proposalB.ProposalId);
        Assert.Equal(MergeProposalStatus.Superseded, supersededA!.Status);
        Assert.Equal(reconciliationProposal.ProposalId, supersededA.SupersededBy);
        Assert.Equal(MergeProposalStatus.Superseded, supersededB!.Status);
        Assert.Equal(reconciliationProposal.ProposalId, supersededB.SupersededBy);
    }

    /// <summary>
    /// The losing goal's file lease on the conflicting path is never released by anything once
    /// TryTriggerAsync creates a reconciliation work unit for it — SupersedeAsync (which is all that
    /// happens to the losing proposal at apply time) has no lease awareness, unlike ReviewAsync's
    /// Rejected path. Without releasing it up front, the reconciliation work unit itself would
    /// immediately queue behind a holder that will never finish.
    /// </summary>
    [Fact]
    public async Task Reconcile_releases_the_losing_goals_file_lease_so_the_new_work_unit_is_not_blocked()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();
        var reconciliationTrigger = app.Services.GetRequiredService<ICandidateReconciliationTrigger>();
        var fileLease = app.Services.GetRequiredService<IFileLeaseService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));
        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "B\n");
        var proposalA = await mergeCommands.ProposeAsync(goalA.BranchId, "main", "A", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(goalB.BranchId, "main", "B", workUnitId: goalB.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(proposalB.ProposalId));

        // Simulate the losing goal's worker still holding a write lease on the contested path, the
        // way a real agent's nm_v1_workspace_write tool call would — a third goal is queued behind it.
        await fileLease.TryAcquireOrEnqueueAsync(goalB.WorkUnitId, "Shared.cs");
        await fileLease.TryAcquireOrEnqueueAsync("some-other-waiting-goal", "Shared.cs");

        var conflict = Assert.Single(await candidateConflicts.GetOpenAsync());
        var reconciliationWorkUnit = await reconciliationTrigger.TryTriggerAsync(conflict.ConflictId);
        Assert.NotNull(reconciliationWorkUnit);

        var leases = await fileLease.ListAsync();
        var sharedLease = Assert.Single(leases, l => l.Path == "shared.cs");
        // goalB's lease was released and the next FIFO waiter promoted to holder — not left stuck
        // behind a goal that will never finish.
        Assert.Equal("some-other-waiting-goal", sharedLease.HolderWorkUnitId);
    }

    [Fact]
    public async Task Reconcile_is_a_no_op_re_entrancy_guard_when_already_reconciling_or_resolved()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();
        var reconciliationTrigger = app.Services.GetRequiredService<ICandidateReconciliationTrigger>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));
        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "B\n");
        var proposalA = await mergeCommands.ProposeAsync(goalA.BranchId, "main", "A", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(goalB.BranchId, "main", "B", workUnitId: goalB.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(proposalB.ProposalId));

        var conflict = Assert.Single(await candidateConflicts.GetOpenAsync());

        var first = await reconciliationTrigger.TryTriggerAsync(conflict.ConflictId);
        Assert.NotNull(first);

        // Already Reconciling — a second call (e.g. a racing auto-trigger) must not create a
        // second reconciliation work unit.
        var second = await reconciliationTrigger.TryTriggerAsync(conflict.ConflictId);
        Assert.Null(second);
    }

    [Fact]
    public async Task Conflict_between_two_AgentApproval_goals_auto_triggers_reconciliation_without_a_human()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // This test drives validate/review/apply manually — but AgentApproval goals (unlike the
        // HumanRequired defaults every other test here uses) also fire ProposeAsync's background
        // auto-apply, whose delayed Task.Run raced these same manual calls under a loaded thread
        // pool (a flaky interleaving landed one proposal via the background apply mid-sequence,
        // so the second manual apply threw for the wrong reason and no conflict was recorded).
        // The auto-trigger under test lives in the apply path itself, so nothing tested here
        // depends on WHO calls apply.
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Goal A", "test", SeedFromBranchId: "main", WorkspaceReviewPolicy: ReviewPolicy.AgentApproval));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand(
            "Goal B", "test", SeedFromBranchId: "main", WorkspaceReviewPolicy: ReviewPolicy.AgentApproval));
        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "B\n");
        var proposalA = await mergeCommands.ProposeAsync(goalA.BranchId, "main", "A", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(goalB.BranchId, "main", "B", workUnitId: goalB.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(proposalB.ProposalId));

        var conflict = Assert.Single(await candidateConflicts.GetOpenAsync());
        Assert.Equal(CandidateConflictStatus.Reconciling, conflict.Status);

        var allWorkUnits = await workUnits.ListAsync();
        Assert.Contains(allWorkUnits, w => w.ReconciliationSourceRef == $"candidate-conflict:{conflict.ConflictId}");
    }

    [Fact]
    public async Task Two_goals_that_independently_arrive_at_identical_content_are_auto_resolved_without_throwing()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\nline two\n");

        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));

        // Both goals independently land on the exact same fix — a line-range overlap by the coarse
        // detector, but ThreeWayMergeStrategy's own ContentA==ContentB short-circuit resolves it
        // cleanly without needing an LLM.
        const string identicalFix = "line one FIXED\nline two\n";
        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", identicalFix);
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", identicalFix);

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: goalA.BranchId, targetBranch: "main", summary: "Fix line one (A)", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: goalB.BranchId, targetBranch: "main", summary: "Fix line one (B)", workUnitId: goalB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        // Must not throw — auto-resolved instead of falling through to the conflict report.
        await mergeCommands.ApplyAsync(proposalB.ProposalId);

        Assert.Equal(identicalFix, await fileWorkspace.ReadAsync(options.CandidateBranchId, "Shared.cs"));
        Assert.Empty(await candidateConflicts.GetOpenAsync());
    }

    /// <summary>
    /// A human already knows the correct combination (e.g. "keep both, in this order") and supplies
    /// it directly rather than spinning up a reconciliation agent — ICandidateReconciliationTrigger
    /// .TryResolveManuallyAsync writes it straight to candidate, records a synthetic Merged proposal
    /// so it still flows through the normal promote/write-back pipeline, and supersedes both original
    /// conflicting proposals.
    /// </summary>
    [Fact]
    public async Task ResolveManually_writes_combined_content_and_resolves_the_conflict()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();
        var reconciliationTrigger = app.Services.GetRequiredService<ICandidateReconciliationTrigger>();

        await fileWorkspace.WriteAsync("main", "burns.md", "");
        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Burns about Brad", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Burns about Jake", "test", SeedFromBranchId: "main"));
        await fileWorkspace.WriteAsync(goalA.BranchId, "burns.md", "Brad burns\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "burns.md", "Jake burns\n");
        var proposalA = await mergeCommands.ProposeAsync(goalA.BranchId, "main", "Brad", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(goalB.BranchId, "main", "Jake", workUnitId: goalB.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(proposalB.ProposalId));

        var conflict = Assert.Single(await candidateConflicts.GetOpenAsync());

        const string combined = "Brad burns\nJake burns\n";
        var resolution = await reconciliationTrigger.TryResolveManuallyAsync(
            conflict.ConflictId, new Dictionary<string, string> { ["burns.md"] = combined });

        Assert.NotNull(resolution);
        Assert.Equal(MergeProposalStatus.Merged, resolution!.Status);
        Assert.True(resolution.LandedOnCandidateBranch);
        Assert.Equal(combined, await fileWorkspace.ReadAsync(options.CandidateBranchId, "burns.md"));
        Assert.Empty(await candidateConflicts.GetOpenAsync());

        var supersededA = await merge.GetAsync(proposalA.ProposalId);
        var supersededB = await merge.GetAsync(proposalB.ProposalId);
        Assert.Equal(MergeProposalStatus.Superseded, supersededA!.Status);
        Assert.Equal(resolution.ProposalId, supersededA.SupersededBy);
        Assert.Equal(MergeProposalStatus.Superseded, supersededB!.Status);
        Assert.Equal(resolution.ProposalId, supersededB.SupersededBy);
    }

    [Fact]
    public async Task ResolveManually_throws_when_content_is_missing_for_a_conflicting_path()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var candidateConflicts = app.Services.GetRequiredService<ICandidateConflictService>();
        var reconciliationTrigger = app.Services.GetRequiredService<ICandidateReconciliationTrigger>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));
        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "B\n");
        var proposalA = await mergeCommands.ProposeAsync(goalA.BranchId, "main", "A", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(goalB.BranchId, "main", "B", workUnitId: goalB.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(proposalB.ProposalId));

        var conflict = Assert.Single(await candidateConflicts.GetOpenAsync());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            reconciliationTrigger.TryResolveManuallyAsync(conflict.ConflictId, new Dictionary<string, string>()));
    }

    /// <summary>
    /// Restart posts Rejected+Revert to the losing proposal's own review endpoint — but a proposal
    /// whose ApplyAsync threw on a candidate conflict never advances past Approved (ApplyAsync's
    /// Merged transition never runs), and Approved -> Rejected was previously not a legal
    /// MergeProposalTransitions edge, so Restart silently no-op'd via a swallowed
    /// InvalidOperationException. Fixed by adding that edge.
    /// </summary>
    [Fact]
    public async Task Restart_rejects_a_still_Approved_losing_proposal_after_a_conflict()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        options.UsePromotionBranch = true;
        // Every test here drives validate/review/apply manually — disable ProposeAsync's
        // background auto-apply so its delayed Task.Run can't race those manual calls (see the
        // AgentApproval test's fuller comment; the reconciliation work unit's own policy makes
        // this reachable even when the two goals are HumanRequired).
        options.AutoApplyOnPropose = false;

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var merge = app.Services.GetRequiredService<IMergeService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));
        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "B\n");
        var proposalA = await mergeCommands.ProposeAsync(goalA.BranchId, "main", "A", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(goalB.BranchId, "main", "B", workUnitId: goalB.WorkUnitId);
        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => mergeCommands.ApplyAsync(proposalB.ProposalId));

        var stillApproved = await merge.GetAsync(proposalB.ProposalId);
        Assert.Equal(MergeProposalStatus.Approved, stillApproved!.Status);

        var rejected = await mergeCommands.ReviewAsync(proposalB.ProposalId, "Rejected");
        Assert.Equal(MergeProposalStatus.Rejected, rejected.Status);
    }
}
