# Phase 12 — File Ownership & Merge-Gated Leasing

`WorkUnit.FileScope` today conflates two unrelated concerns: **edit authorization** (which paths a
Worker may write) and **concurrency ownership** (preventing two agents from stomping the same
file). It's set once by the Planner at slice-creation time (`FanOutService.cs:217`, copied verbatim
from `plan.json`'s `fileScope`) and enforced as an immutable, hard glob-match block at every write
(`McpToolDispatcher.WorkspaceWriteAsync`/`WorkspaceDeleteAsync` → `CheckFileScopeAsync` →
`AgentWorkspaceService.ValidateWriteAsync`). There is no path for a Worker to correct a wrong guess
after creation — `WorkUnitUpdate` doesn't support changing `FileScope`, and isn't in any agent's
allowed-tools list regardless.

This was diagnosed from a real, reproduced production incident (verified via the live host's
conversation-log endpoint): a Planner ran against a genuinely empty branch (first-ever goal, no
seed content yet), guessed a placeholder path (`WeatherApi/Program.cs`) for a CORS-config task, and
even hedged about it in its own plan text. Once the real file (`Scratch/Program.cs`) existed and a
Worker correctly discovered it via `nm_v1_workspace_list`, every write to the real path was
hard-rejected by the stale `FileScope`, so the Worker fabricated a duplicate file at the one path it
*was* allowed to touch. This recurred three times across two days because once a Plan artifact
exists, the Orchestrator never re-plans, even as the branch's real contents changed underneath it.

Two existing mechanisms gesture at "real" concurrency control but neither actually enforces
anything at write time: `NonOverlappingFileScopeRule` (opt-in, default off, sibling-overlap check
at enqueue time only) and `IntentGraphService`/`nm_v1_intent_record` (advisory-only, not exposed to
Workers). Phase 12 replaces the hard `FileScope` block entirely with a merge-gated FIFO file lease
queue, and closes a second, related gap: a dependent slice's branch is seeded once from the parent
at fan-out time and never refreshed, so even a slice that correctly `dependsOn` another can start
before that dependency's actual output exists in its branch.

## How it works

A file's lease is held by whichever active sibling WorkUnit first successfully writes to it, and is
held until that WorkUnit's `MergeProposal` touching that file is actually merged (human- or
auto-approved). Any other sibling that tries to write the same path while it's held is queued (FIFO,
one entry per WorkUnit per path) instead of rejected. The blocked Worker's agent loop exits
immediately on the same turn the conflict is detected — no extra LLM round trip, no busy-wait
context burn — and its scheduler entry is parked (mirroring the existing `AwaitingResume` pattern
already used for host-restart recovery) rather than removed or dead-lettered. When the holder
merges, the next queued WorkUnit for that path gets the merged file copied into its branch and is
re-enqueued fresh (`isResume: true`, the same resume pathway already used for dead-letter retries
and host-restart recovery).

```
Worker A writes src/Foo.cs  → lease(src/Foo.cs) granted to A
Worker B writes src/Foo.cs  → lease held by A → B enqueued on src/Foo.cs's wait queue
                              → B's scheduler entry marked AwaitingFileLease, loop exits now
A proposes + merges          → ApplyAsync copies Foo.cs into target branch
                              → release hook: lease(src/Foo.cs) cleared, B popped off queue
                              → Foo.cs's merged content copied into B's branch
                              → B re-enqueued, resumes (isResume=true), writes succeed
```

`WorkUnit.FileScope` stays in the data model as a hint — still consumed by
`FanOutService.TryMatchFileScopeProfileAsync` (profile routing) and
`NonOverlappingFileScopeRule`/`WorkSchedulerService.DetectConflictAsync` (pre-enqueue advisory
warning, unchanged, still opt-in/off). Only the write-time hard block is removed.
`IntentGraphService`/`nm_v1_intent_record` are left in place, unused — their advisory-record
semantics don't fit an exclusive-lease-with-queue model.

The lease queue only catches *file*-level collisions. It does nothing for a slice that `dependsOn`
another for semantic reasons (a new class, schema, or contract) with zero file overlap — that gap
is closed separately in 12e/12f by tightening `dependsOn` gating to require `Merged` (not just
`Proposed`) and proactively refreshing a dependent's branch from its merged dependencies before it
starts, plus a Planner prompt rule that `dependsOn` must capture semantic producer/consumer
relationships, not just shared files.

| Slice | Focus | Status |
|---|---|---|
| 12a | `IFileLeaseService` — per-path holder + FIFO wait queue, persisted/rehydratable | Done |
| 12b | Write-time enforcement — remove hard `FileScope` block, wire `CheckFileLeaseAsync` into `McpToolDispatcher` | Done |
| 12c | Zero-context-burn suspend — `AgentLoopCompletion.AwaitingFileLease`, `WorkerAgentLoop` exits same-turn on conflict | Done |
| 12d | Scheduler parking — `ScheduledItem.AwaitingFileLease`, skip in acquire sweep, release-and-resume hook on `MergeApplyAsync`, force-release on failure | Done |
| 12e | Tighten `dependsOn` gating to `Merged`-only; proactively refresh a dependent's branch from its merged dependencies before enqueue | Done |
| 12f | Planner prompt — `fileScope` as hint not permission, empty-branch deferral rule, semantic-vs-file `dependsOn` rule | Done |
| 12g | Tests — unit (`FileLeaseService`, dispatcher, loop, scheduler, fan-out) + integration (two-slice collision-and-resume scenario) | Done |

## Slice 12a — `IFileLeaseService`

New interface in `ServiceContracts.cs`, implementation in
`src/NodalMerge.Studio.Storage/FileLeaseService.cs`, modeled on `IntentGraphService.cs`'s
storage/rehydrate shape (not an extension of it — the semantics differ: exclusive grant + FIFO
queue vs. advisory record).

- `TryAcquireOrEnqueueAsync(workUnitId, path, ct) -> (Granted, HolderWorkUnitId)` — grants
  immediately if unheld or already held by `workUnitId`; otherwise enqueues (idempotent) and
  returns `Granted: false`.
- `ReleaseAndAdvanceAsync(path, ct) -> NextWaiterWorkUnitId?` — clears the holder, pops the next
  FIFO waiter, makes it the new holder.
- `ForceReleaseAllForWorkUnitAsync(workUnitId, ct)` — releases every path a failed/dead-lettered
  WorkUnit held, advancing each queue with no content to forward.
- Path normalization matches `IntentGraphService.Normalize`/`AgentWorkspaceService.MatchesGlob`'s
  existing convention (lowercase, `\` → `/`).

## Slice 12b — Write-time enforcement

- Delete `CheckFileScopeAsync` (`McpToolDispatcher.cs:399-411`) and its call sites in
  `WorkspaceWriteAsync`/`WorkspaceDeleteAsync`. Remove `AgentWorkspaceService.ValidateWriteAsync`
  only if nothing else references it after this change — `MatchesGlob` itself stays, since
  `FanOutService.cs:393` and `WorkSchedulerService.cs:147` still use it for profile routing and the
  advisory conflict warning.
- Add `CheckFileLeaseAsync(branchId, workUnitId, path, ct)` calling
  `IFileLeaseService.TryAcquireOrEnqueueAsync`. On conflict, return a structured, distinguishable
  sentinel (not prose) so the calling agent loop can recognize it programmatically.
- `IFileLeaseService` becomes a new constructor dependency on `McpToolDispatcher` (same DI pattern
  as the already-injected `IIntentGraphService`/`IWorkUnitService`).

## Slice 12c — Zero-context-burn suspend

- Add `AwaitingFileLease` to `AgentLoopCompletion` (alongside `Stalled`/`Cancelled`/
  `MaxIterationsExceeded`/`Succeeded`).
- `WorkerAgentLoop.RunAsync`'s tool-dispatch loop: after each `dispatcher.DispatchAsync` call, check
  for the lease-conflict sentinel from 12b. If present, break the loop **on this same turn** —
  don't send another message to the LLM — and return `AgentLoopCompletion.AwaitingFileLease`. This
  is the literal "don't burn context" mechanism: zero extra model calls, not fewer.

## Slice 12d — Scheduler parking, release, and resume

- Add `AwaitingFileLease: string?` to `ScheduledItem`, mirroring the existing `AwaitingResume`
  pattern used for host-restart recovery.
- Wherever the scheduler interprets a finished loop's `AgentLoopCompletion`: branch on
  `.AwaitingFileLease` to set the flag and keep the item queued (not removed, not dead-lettered).
  `TryAcquireAsync`'s sweep skips any item with the flag set.
- `InMemoryMergeService.ApplyAsync`, right after `ApplyBranchAsync` succeeds: for each path in
  `proposal.FilesTouched`, call `ReleaseAndAdvanceAsync`; if a waiter is returned, copy the merged
  file into its branch (`IFileWorkspaceService.CopyFilesAsync`, the same primitive
  `MergeReconciliationService.cs` already uses for cross-branch file copies), clear its
  `AwaitingFileLease` flag, and re-enqueue with `isResume: true`.
- Failure path: on final dead-letter/abandonment, call `ForceReleaseAllForWorkUnitAsync` so a
  crashed holder doesn't strand its queue forever (no content to forward — nothing was merged).

### Post-implementation fixes (found via review, not in the original plan text)

- **`ApplyBranchAsync` → `CopyFilesAsync` for the 12e branch refresh.** `ApplyBranchAsync` is a
  destructive full-mirror — it deletes every file in the target absent from the source
  (`FileSystemWorkspaceService.ApplyBranchAsync`, by design: that's correct for landing one
  proposal into a target branch, "approved diff == merged result"). Using it for
  `RefreshBranchFromDependenciesAsync` was wrong: with two-or-more dependencies, applying dep2's
  whole branch after dep1's would delete every one of dep1's files dep2's branch doesn't also
  contain. Fixed to copy only each dependency's own `MergeProposal.FilesTouched`
  (`IFileWorkspaceService.CopyFilesAsync`, additive) — multiple dependencies can now only ever
  contend over a genuine overlap, never delete an unrelated dependency's contribution. Covered by
  `FanOutServiceTests.TryFanOutFromPlan_refreshes_dependent_branch_from_merged_dependency`,
  rewritten with three slices (s3 `dependsOn` both s1 and s2, fully disjoint files) specifically to
  catch this class of bug.
- **`ForceReleaseAllForWorkUnitAsync` never told the scheduler about a promoted waiter.** Both
  existing callers (`InMemoryDeadLetterService`, on final dead-letter) advanced the lease's
  internal holder via `ReleaseAndAdvanceAsync` but discarded the returned new-holder WorkUnitId —
  so a promoted waiter became the real lease holder while its scheduler item stayed parked
  (`AwaitingFileLease: true`) forever, since only the merge-apply success path ever called
  `IWorkScheduler.ClearAwaitingFileLeaseAsync`. Fixed by changing
  `ForceReleaseAllForWorkUnitAsync`'s return type to `Task<IReadOnlyList<string>>` (every promoted
  WorkUnitId); every caller now loops over the result and clears each one's flag.
- **No release path existed for a human-rejected proposal or a manually-stopped worker.**
  `IAgentControlService.StopAsync` only cancelled the CTS and marked the agent record stopped —
  it never touched `IFileLeaseService`, and a manually-stopped worker's run is caught by its own
  `OperationCanceledException` branch, which never sets a `failureReason`, so it never reaches the
  dead-letter path either. Separately, `InMemoryMergeService.ReviewAsync`'s `Rejected` branch (the
  human review path) didn't release anything — a human-rejected proposal had no automatic
  follow-up at all, unlike `AutomatedReviewGateService`'s rejection-count retry loop, which already
  released leases correctly by routing its own final escalation through
  `IDeadLetterService.RecordFailureAsync`. Fixed both: `StopAsync` and the human `ReviewAsync`
  Rejected branch now call `ForceReleaseAllForWorkUnitAsync` and clear the promoted waiters'
  scheduler flags, same as the dead-letter path. `AutomatedReviewAsync`'s own per-rejection calls
  (before its retry loop exhausts) deliberately do **not** release — that would race its own
  upcoming retry. Covered by
  `FileLeaseConflictIntegrationTests.ConflictingWaiter_ResumesAfterHolderProposalIsRejected`.
  Stopping a worker mid-run specifically is not separately covered end-to-end: an instant fake LLM
  handler completes a scripted run faster than a test can ever call `StopAsync` on it mid-flight,
  so the only realistic way to exercise that exact race would need a deliberately-blocking fake
  handler — left as a gap, the underlying `StopAsync` fix is otherwise identical in shape to the
  rejection fix and exercises the exact same `ForceReleaseAllForWorkUnitAsync` code path.

## Slice 12e — Dependency-aware branch freshness

- `FanOutService.IsReadyToEnqueueAsync`: require every dependency to be `Merged` (not `Proposed`)
  before a dependent is enqueued — `Proposed` only means a proposal is awaiting review, its content
  isn't real yet.
- Once all of a dependent's dependencies are `Merged`, before enqueueing it, copy each dependency's
  actual changes into the dependent's branch (`ApplyBranchAsync`/`CopyFilesAsync` — the dependency's
  full merged output, not just files the dependent happened to declare interest in, since a semantic
  dependency may need files outside the dependent's own `fileScope`).
- Implemented in `FanOutService.ProcessAsync`'s enqueue path, between the `IsReadyToEnqueueAsync`
  check and `EnqueueChildWorkerAsync` — separate from, and in addition to, 12d's lease queue, which
  only handles *undeclared* file collisions reactively.
- As-built addition (not in the original plan text): the existing `TryEnqueueReadyDependentsAsync`
  trigger fires from `WorkSchedulerService.ReleaseAsync`'s success path, i.e. at worker-completion
  (`Proposed`) time — too early once the gate requires `Merged`. `InMemoryMergeService.ApplyAsync`
  now fires the same call again right after a WorkUnit's status actually reaches `Merged`, so a
  dependent that was correctly held back at `Proposed` time gets re-checked once its dependency
  truly merges. Discovered by `FanOutIntegrationTests.Dependent_slice_is_enqueued_only_after_
  dependency_is_Merged` failing with the dependent stuck at `Created` — nothing was re-triggering
  the gate check after the gate's own new threshold.

## Slice 12f — Planner prompt

`PlannerAgentLoop.cs`'s `DefaultSystemPrompt`:

- Reframe `fileScope` as a routing/advisory hint, not a permission — a Worker is no longer
  restricted to it.
- Empty-branch rule: if `nm_v1_workspace_list` returns zero files for the whole branch, don't guess
  conventional framework paths — either write a single slice with `fileScope: []` whose first step
  is "scaffold the project structure," or make scaffolding its own slice every feature slice
  `dependsOn`.
- Semantic-vs-file-dependency rule: `dependsOn` must capture producer/consumer relationships
  (abstractions, contracts, schemas, interfaces, services, models, migrations) even when the two
  slices' `fileScope`s are completely disjoint — not just inferred from shared files.

## Slice 12g — Tests

- `FileLeaseServiceTests.cs`: acquire/grant when unheld; conflict enqueues FIFO; release pops next
  waiter in order; force-release-all clears every path a failed WorkUnit held.
- Dispatcher-level: two siblings writing the same path — second gets the lease sentinel, not a
  plain error.
- `WorkerAgentLoop`-level: a lease-conflict result causes the loop to return `AwaitingFileLease`
  with exactly one LLM call for that turn, not two.
- `WorkSchedulerService`: an `AwaitingFileLease` item is skipped by `TryAcquireAsync` and is not
  dead-lettered.
- `FanOutServiceTests.cs`: a dependent with zero file overlap with its dependency is not enqueued
  while the dependency is only `Proposed`; once enqueued, its branch contains the dependency's
  merged files even though they're outside the dependent's own `fileScope`.
- Integration test: two-slice scenario — B hits a lease conflict on a file A is mid-writing, gets
  queued; A proposes and merges; B resumes automatically with A's merged content already present
  and completes its own write.

## Explicitly deferred (v2 and beyond)

- **`produces`/`consumes` slice schema.** Let a slice declare what it produces (e.g.
  `"produces": ["EncryptionService"]`) and what it consumes, so the Planner can infer `dependsOn`
  automatically instead of declaring it by hand. Worth pursuing once 12f's explicit-`dependsOn`
  version is proven out — inferring dependencies reliably from an LLM's own plan text is a
  meaningfully harder problem than enforcing dependencies once declared.
- **Release-on-proposal instead of release-on-merge.** The current design stalls every
  queued/dependent Worker behind however long human review takes. A faster alternative — release
  the lease (and advance dependents) once a proposal is *created*, having waiters rebase onto the
  proposal branch instead of waiting for the human gate — would give much higher throughput at the
  cost of proposal churn (a waiter might rebase onto a proposal later rejected). Revisit only if
  review latency proves to be a real bottleneck in practice; merge-gated release is the safer
  starting point.
- **Region-level (sub-file) leasing.** `ChangeIntent.RegionDescriptor`/`IntentGraphService.
  RegionsOverlap` already model this for the (unused) advisory intent system; a real lease could
  reuse the same shape later. Whole-file only for now — getting Workers to declare meaningful
  regions reliably is a separate prompt-engineering problem.
- **Lease timeout/expiry.** No timeout for a holder that hangs without ever failing cleanly.
  `WorkSchedulerService`'s existing `LeaseTimeout` (5 min) constant is a reasonable model to copy if
  this proves necessary in practice.
- **Multi-file joint waiting.** A resumed Worker that immediately hits a second conflicting file
  simply blocks again (sequential single-file blocking), rather than registering a joint wait on
  multiple files at once.
- **Removing `IntentGraphService`/`nm_v1_intent_record`.** Left in place, unused, rather than ripped
  out — harmless dead weight, not worth the churn of deleting now.
- **Dynamic re-ordering/inference of `dependsOn` from discovered file overlap.** The lease queue
  handles unplanned same-file collisions reactively; the Planner's declared `dependsOn` remains the
  only proactive sequencing signal this phase relies on.

## Verification (whole phase)

1. `dotnet build NodalMerge.Studio.slnx` / `dotnet test` — 0 errors, all new tests pass.
2. Reproduce the original incident's shape: a Planner guesses a wrong path on a sparsely-seeded
   branch; a Worker discovers the real file and successfully writes to it (previously hard-blocked).
   Confirm via `GET /studio/workunits/{id}/conversation-log` that the write now succeeds instead of
   forking a duplicate file.
3. Force a same-file collision between two siblings: confirm the second Worker's loop exits
   immediately (no extra LLM turn in the conversation log), the dashboard shows it parked, and once
   the first slice's proposal is merged, the second Worker resumes automatically with the merged
   content already in its branch.
