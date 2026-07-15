# Review-apply, eviction, and CLI verdict-landing fixes

Four fixes coming out of the 6 failing integration tests (root-caused 2026-07-13/14) plus one
robustness hardening for the CLI review path. Items 1–3 are the failing tests; item 4 is the
CLI verdict-landing hardening. All four are independent and land together.

**Verification is test-driven** — each code fix has a specific integration test that must go
from red to green, and no previously-green test may regress. Run after each fix:

```
$env:NODALMERGE_NATIVE_AVAILABLE='true'
dotnet test tests/NodalMerge.Studio.Integration.Tests --filter "<class>"
```

Full target set at the end: the 3 `HarnessReviewModeSeamTests`, 2 `AutonomousReviewTests`
Hybrid tests, 1 `WorkspaceCacheManagerMultiRepoTests`, plus the unit suites for the files
touched (`NodalMerge.Studio.Merge.Tests`).

---

## Fix 1 — Hybrid review policy must not auto-apply on approval (TRUE regression, code)

**Failing tests:** `AutonomousReviewTests.Hybrid_reviewer_approves_schedules_timer_then_expiry_autoApplies`,
`AutonomousReviewTests.Hybrid_human_applies_before_expiry_cancels_timer`. Both time out polling
for status `Approved` (the proposal races straight to `Merged`).

**Root cause.** `InMemoryMergeService.AutomatedReviewAsync`, the `nextStatus == Approved` block
(~lines 524-546). The WIP added a direct `ApplyAsync(..., autoApplied: true)` for any
`WorkspaceReviewScope.AppliesToRealRepo` proposal, gated on repo scope but **not on review
policy**. That low-level apply bypasses the `AutoReviewRule` BeforeMerge gate
([AutoReviewRule.cs](../src/NodalMerge.Studio.Merge/AutoReviewRule.cs)) that is supposed to,
for Hybrid, schedule the countdown timer and *block* the immediate apply. So under Hybrid the
proposal merges instantly, destroying the human-override window and preventing the timer from
ever being scheduled.

**Change.** Gate the direct apply to `AgentApproval` only. `effectiveInlinePolicy` is already
computed a few lines above (the WorkspaceReviewScope-aware policy pick). Under Hybrid, do
neither the direct apply nor the retrigger — leave the proposal at `Approved`; the gate that
invoked this review (`AutoReviewRule`, via the inline reviewer) schedules the timer on return,
exactly as it did pre-WIP.

```csharp
if (nextStatus == MergeProposalStatus.Approved)
{
    if (WorkspaceReviewScope.AppliesToRealRepo(owningWorkUnit))
    {
        // AgentApproval auto-applies on approval. Hybrid must NOT: its human-override countdown
        // is owned by AutoReviewRule's BeforeMerge gate (schedule timer, block immediate apply),
        // and ReviewTimerService.ProcessExpiredAsync applies at expiry. A direct apply here under
        // Hybrid merges instantly and destroys the override window (AutonomousReviewTests).
        if (effectiveInlinePolicy == ReviewPolicy.AgentApproval)
        {
            try { await ApplyAsync(proposalId, cancellationToken, autoApplied: true).ConfigureAwait(false); }
            catch { /* best-effort — review already succeeded; apply can be retried/inspected */ }
        }
    }
    else
    {
        await TryRetriggerParentReconciliationAsync(proposal.WorkUnitId, proposal.SessionId, cancellationToken)
            .ConfigureAwait(false);
    }
}
```

**Verify.** Both Hybrid tests green; `AgentApproval_reviewer_approves_autoMerges` and
`AgentApproval_reviewer_rejects_blocks_without_autoMerge` stay green.

---

## Fix 2 — HarnessReviewModeSeamTests expect Merged, not Approved (purposeful regression, tests only)

**Failing tests:** `Claude_stub_approved_verdict_lands_via_AutomatedReviewAsync` (line 139),
`Codex_stub_approved_verdict_lands_the_same_way` (line 214),
`Inline_reviewer_with_claude_cli_provider_routes_to_the_CLI_adapter_and_approves` (line 246).
Each asserts `MergeProposalStatus.Approved`; gets `Merged`.

**Rationale.** Under `AgentApproval` + real-repo, an approved verdict now auto-applies to
`Merged` — the documented "auto-applies on approval" behavior (this is the *intended* effect of
Fix 1's AgentApproval branch). The tests assert the obsolete pre-apply intermediate state.
Tests-only change; **no production/candidate-branch code is touched.**

**Change.** In each of the three tests, update the terminal status assertion from `Approved` to
`Merged`, and keep the content assertions (`VerificationResults`, `ReviewedBy`) which still hold
on the merged proposal. Add `Assert.True(reviewed.AutoApplied)` to lock in that it was the
automated apply. Do **not** change the rejected/missing-verdict sibling tests (they pass and are
correct: `Rejected` stays `Rejected`, missing verdict stays `ReadyForReview`).

Note the seam-test comment at line 120-121 ("an automated Approved verdict is terminal
(Approved)") is now stale — update it to describe the auto-apply.

**Verify.** All 5 `HarnessReviewModeSeamTests` green.

---

## Fix 3 — eviction invariant defeated by apply-time snapshot ordering (TRUE regression, code)

**Failing test:** `WorkspaceCacheManagerMultiRepoTests.EvictAsync_uses_the_work_units_own_repository_snapshot_not_the_others`
(line 80: `evictedA` expected True, got False). Deterministic in isolation.

**Root cause.** `WorkspaceCacheManager.PassesSafeEvictionInvariantAsync` decides evictability by
`snapshot.CreatedAt > wu.UpdatedAt` ([line 147](../src/NodalMerge.Studio.Storage/WorkspaceCacheManager.cs#L147)).
The WIP's apply-time `BestEffortResyncAsync` ([InMemoryMergeService.cs:745](../src/NodalMerge.Studio.Merge/InMemoryMergeService.cs#L745))
stamps the repo snapshot **before** the work unit's own `Merged` status bump
([~line 995](../src/NodalMerge.Studio.Merge/InMemoryMergeService.cs#L995)) sets `UpdatedAt`. So
the capturing snapshot always predates `UpdatedAt`, and the test's later manual re-sync is a
content-unchanged no-op ([RepositorySyncService.cs:93](../src/NodalMerge.Studio.Storage/RepositorySyncService.cs#L93)
`diff.IsEmpty` early return) that writes no fresher snapshot. Net: `snapshot.CreatedAt > UpdatedAt`
is structurally false for a freshly-merged real-repo unit → it can never be evicted (branch-dir
leak) until some unrelated later sync happens to advance the snapshot.

**Change (two moves).**

1. **Persist the applied snapshot id.** `appliedSnapshotId` is computed at apply time (line 745,
   the snapshot that captured the write-back) but currently only emitted into a `MergeAppliedPayload`
   event — nothing durable reads it. When the work unit transitions to `Merged`
   (~line 995, right after the successful `UpdateStatusAsync(..., Merged)`), also persist it:
   ```csharp
   if (!string.IsNullOrEmpty(appliedSnapshotId))
   {
       try { await workUnits.SetMetadataAsync(proposal.WorkUnitId, "appliedSnapshotId", appliedSnapshotId, cancellationToken).ConfigureAwait(false); }
       catch (KeyNotFoundException) { }
   }
   ```
   (`SetMetadataAsync(workUnitId, key, value, ct)` already exists — used by ClaudeCodeExecutor for
   the harness session id. `WorkUnit.Metadata` is the string dict it writes to.)

2. **Change the eviction check to snapshot identity, not timestamp race.** In
   `PassesSafeEvictionInvariantAsync`: a `Merged` work unit that recorded a non-null
   `appliedSnapshotId` is safe to evict — its merged content was demonstrably captured in a repo
   snapshot at apply time, independent of any `CreatedAt`/`UpdatedAt` sub-second ordering. Keep the
   existing `snapshot.CreatedAt > wu.UpdatedAt` heuristic as the fallback (null appliedSnapshotId:
   resync failed or no snapshot service; and non-Merged terminal states like a Completed-without-merge unit).
   ```csharp
   private async Task<bool> PassesSafeEvictionInvariantAsync(WorkUnit wu, CancellationToken ct)
   {
       if (wu.Status == WorkUnitStatus.Merged
           && wu.Metadata is { } md
           && md.TryGetValue("appliedSnapshotId", out var snapId)
           && !string.IsNullOrEmpty(snapId))
       {
           return true;
       }

       var repositoryId = await GetRepositoryIdAsync(wu, ct).ConfigureAwait(false);
       var snapshot = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
       if (snapshot?.TreeEntries is null) return false;
       return snapshot.CreatedAt > wu.UpdatedAt;
   }
   ```

This is strictly safer than today: today's check can *wrongly refuse* eviction (the leak); it
never wrongly *permits*. A non-null `appliedSnapshotId` is only ever set after a successful
content-capturing resync, so permitting on it preserves that safety property.

**Verify.** `WorkspaceCacheManagerMultiRepoTests` green (evictA True via the new snapshot-id
path; evictB False — B is `Approved`/unapplied, no appliedSnapshotId, falls to the heuristic which
correctly returns False).

---

## Fix 4 — CLI review verdict-landing fallback (robustness hardening, code)

**Symptom (observed live, not a failing test):** the reviewer CLI sometimes `cd`s into a nested
project dir to run tests and doesn't `cd` back, so its relative `.workspace/review.json` write
lands in `…/tests/Project/.workspace/` — a path the Edit allowlist doesn't permit and the harvest
doesn't read. The run then Stalls as "no verdict written" even though the model produced one. The
review prompt already asks the model not to do this ([ClaudeCodeExecutor.cs:519-523](../src/NodalMerge.Studio.AgentRuntime/Harnesses/ClaudeCode/ClaudeCodeExecutor.cs#L519-L523));
this makes it robust to the model slipping.

**Change.** In `HarnessHarvestPipeline.HarvestReviewAsync` (~line 304), when the canonical
`.workspace/review.json` read comes back null/empty, before returning `Stalled`, search the branch
working directory for a misplaced `review.json` under any nested `.workspace/` directory and adopt
it (most-recently-written wins). Emit a warning event so the misplacement is visible, then proceed
with the recovered verdict through the same parse/validate path.

Implementation notes:
- Resolve the branch working dir via `fileWorkspace.GetWorkingDirectoryAsync(branchId, ct)` (the
  same call RunAsync uses).
- `Directory.EnumerateFiles(workDir, "review.json", SearchOption.AllDirectories)`, keep only paths
  whose immediate parent directory is named `.workspace`, exclude the canonical root
  `<workDir>/.workspace/review.json` (already known absent), order by `LastWriteTimeUtc` descending,
  take the first, `File.ReadAllTextAsync`.
- Keep it best-effort: any exception in the fallback → treat as still-missing (the existing Stalled
  path is the correct floor).

**Test.** Add a `HarnessReviewModeSeamTests` case: a Claude stub that writes its `review.json`
into a nested `.workspace/` subdir (not the root). Assert the run does **not** Stall — the verdict
is recovered and lands (proposal reaches `Merged` under AgentApproval, same as the happy-path seam
test), and a warning was logged/evented.

---

## Definition of done

- 6 previously-failing integration tests green; no previously-green test regressed.
- New nested-verdict seam test green.
- `NodalMerge.Studio.Merge.Tests` green (Fix 1/3 touch `InMemoryMergeService`).
- `dotnet build NodalMerge.Studio.slnx` clean.
- These fixes stay isolated from the rest of the uncommitted WIP so they can be reviewed as a set.
