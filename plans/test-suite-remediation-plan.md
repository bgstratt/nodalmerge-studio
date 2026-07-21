# Test-suite remediation plan

**Date:** 2026-07-20
**Input:** [test-suite-health-report.md](test-suite-health-report.md) — all findings below cite it.
**Baseline:** 753/753 green, 46 s, 0 skipped. Nothing is on fire; this is planned work, not triage.

---

## How this plan is organised

Three tracks, deliberately separated so each can be reviewed on its own terms:

| Track | What it touches | Review question |
|---|---|---|
| **A — Code defects** | `src/` production code | "Is this behaviour actually wrong?" |
| **B — Test infrastructure** | `tests/` scaffolding only | "Does this change what we're testing?" |
| **C — Test correctness** | `tests/` assertions only | "Was this test proving anything?" |

**Track A changes production behaviour. Tracks B and C cannot.** If a Track B/C change requires a
`src/` edit to land, that is a signal we found a code defect — stop and move it to Track A rather than
bending production code to suit a test.

Each phase is independently committable and independently revertable.

---

## Standing constraints

These come out of the report and the review of it. They bound everything below.

1. **No transition-table changes.** The three FSMs (`TaskTransitions`, `WorkUnitTransitions`,
   `MergeProposalTransitions`) are deliberate, load-bearing, and pinned by 8 assert-throws tests, 2
   table tests, and the `Every_terminal_status_has_zero_outgoing_transition_edges` invariant marked
   **"DO NOT DELETE THIS TEST"**. Specifically: **`Completed -> Merged` is not to be added.**
2. **No widening of a status set to make a test pass.** Report §5.
3. **Do not delete a test to fix a failure.** If a test is wrong, say why in the commit.
4. **Don't change working production code for test convenience.** The prior FSM-loosening proposal
   was dropped for exactly this reason.
5. **Verify by neutering.** For each fix, temporarily disable it and confirm *exactly* the intended
   tests fail. A fix that can't be shown to fail when removed isn't proven.

---

# Track A — Code defects (production changes)

Four items. These are real defects found *via* the tests, not test problems.
**A1 is investigation-first: the fix is not yet known.**

## A1. `Open -> Completed` — a caller completes a task it never claimed

**Severity:** medium. **Risk:** unknown until diagnosed.

`TaskTransitions` has no `Open -> Completed` edge, deliberately and by test
(`InMemoryTaskServiceTests.cs:102`, `DomainTests.cs:115`). The live detector caught this transition
being attempted, which means **some caller completes a task without ever moving it to InProgress**.
It existed silently because it was thrown inside a `catch (InvalidOperationException) { }`.

**This phase is diagnosis, not repair.** Do not write a fix before the caller is identified.

Steps:
1. Fix the detector first (**B1**) — otherwise we cannot reliably observe this.
2. Add a temporary stack-capturing hook at the `InMemoryTaskService.cs:56` throw to identify the
   caller.
3. Classify: is the caller skipping a legitimate `InProgress` step (**caller bug — fix the caller**),
   or is there a real workflow where a task completes without being claimed (**design gap — needs a
   decision, not a code change**)?
4. Only then plan the fix. Record the finding in this file before implementing.

**Explicitly out of scope:** adding the `Open -> Completed` edge. That is the reflex this plan exists
to avoid.

**Verification:** the detector reports zero occurrences across 3 consecutive full runs.

### A1 — INVESTIGATED (2026-07-20): not currently reproducing

With the detector fixed (B1) and the A4 breadcrumb in place, I ran the full suite ~20 times looking
for the `Open -> Completed` transition. **It did not recur once** — zero unobserved exceptions across
all runs, and no detector report ever written.

Static search backs this up: no production code sets a task to `Completed` directly. The only
completion path is the agent `task.update` MCP tool, so the original occurrence was almost certainly a
scripted test agent completing a task that was still `Open` (never assigned to `InProgress`), thrown
inside fire-and-forget worker work and leaked — which is how the detector caught it originally. The
substantial timing changes since (B2 async teardown, A2) appear to have stopped it manifesting.

**Conclusion:** there is no active bug to fix, and manufacturing a speculative fix would violate the
"don't change working code" rule. Two independent safety nets are now in place if it returns: the
now-reliable detector captures it with a full stack (B1), and the A4 breadcrumb logs the exact
`Open -> Completed` refusal at its source. If it recurs, the stack names the caller and we fix *that*.

**Explicitly NOT done:** adding the `Open -> Completed` edge, or any speculative caller change.

## A2. `ApplyBranchAsync` has no retry

**Severity:** low. **Risk:** low — mirrors an existing, proven pattern.

The read/write retry added earlier covers `ReadAsync`, `ReadBytesAsync`, and single-file `WriteAsync`.
The **branch-apply copy path** was missed, and produced a real `IOException` under contention.

**Change:** route the copy path in `FileSystemWorkspaceService.ApplyBranchAsync` through the existing
retry helper. No new mechanism — same 5-attempt 25/50/75/100 ms schedule.

**Verification:** neuter the retry; the contention test must fail. Restore; it must pass.

### A2 — DONE (2026-07-20)

- Added `RunWithRetryAsync(Action, ct)` — the copy/delete twin of the existing `ReadWithRetryAsync`,
  same bounded budget (5 attempts, 25/50/75/100ms, last rethrows).
- Wrapped `File.Delete` and `File.Copy` in **both** `ApplyBranchAsync` and `ApplyExternalPathAsync`
  (the latter had the identical race and already returned `Task`, so making it `async` was
  transparent to callers). Also covered the delete path, not just copy — same sharing violation.
- New `FileSystemWorkspaceServiceApplyContentionTests` mirrors the read-contention test: holds the
  target file exclusively for 120ms (inside the retry budget) and asserts the apply rides it out and
  lands the merged content.

**Verified by neuter:** removing the copy retry makes the contention test fail with the exact
IOException; restored, it passes. Full suite 761/761, all other projects green.

## A3. Three `TerminalStatuses` definitions disagree

**Severity:** low, but it is a latent correctness bug, not just duplication.

| definition | Completed | Merged | Cancelled | Failed |
|---|:-:|:-:|:-:|:-:|
| `WorkUnitCommandService.cs:33` | ✓ | ✓ | ✓ | — |
| `GoalGuardrailService.cs:16` | ✓ | ✓ | ✓ | ✓ |
| `SnapshotRetentionPolicy.cs:81` | ✓ | ✓ | — | ✓ |

Three subsystems disagree on whether a `Cancelled` or `Failed` unit is "done" — so requeue, guardrail,
and snapshot retention can each reach a different conclusion about the same work unit.

**Important:** the differences may be *intentional* — retention plausibly should treat `Cancelled`
differently from a guardrail. **Do not blindly unify.** Determine intent per set first; if a
difference is deliberate, keep it and *name* it (e.g. `RetainableStatuses` vs `TerminalStatuses`) so
the divergence is legible rather than accidental.

There is a fourth, related authority — `WorkUnitStatusVectorTests`' `terminal_status_names` vector.
Whatever we conclude must agree with it, since it is the pinned contract.

**Verification:** full suite green; the terminal-invariant test still passes unchanged.

### A3 — DONE (2026-07-20): found a real GC bug, not cosmetic divergence

Analyzed under the principle *a human should be able to revive almost anything; an agent should not*.
The sets SHOULD differ — they answer different questions:
- **SnapshotRetentionPolicy** (GC/retention seed-liveness): must match the frozen `terminal_status_names`
  = `{Completed, Merged}` that the Rust GC coordinator also uses.
- **WorkUnitCommandService** `{Completed, Merged, Cancelled}`: "worth issuing a Cancel?"
- **GoalGuardrailService** `{Completed, Merged, Cancelled, Failed}`: "still burning budget to monitor?"
- **WorkspaceCacheManager** `{Completed, Merged, Cancelled}`: "is this working DIRECTORY safe to evict?"

**The bug:** `SnapshotRetentionPolicy` listed `Failed` as terminal. `BlobGcService` and
`WorkspaceCacheManager` **delete/evict** against its classification, so a `Failed` unit's seed/merge-base
blobs got aged out and deleted — but `Failed -> Cancelled -> Queued/Executing` is a real human revival
path, so a revived `Failed` unit would find its seed gone. finding #30 already fixed the Rust GC + the
frozen vector ("a failed work unit IS resumable"); the C# side was the straggler, its doc comment still
falsely claiming `Failed` "has zero outgoing edges".

Fix: `SnapshotRetentionPolicy.TerminalStatuses` → `{Completed, Merged}`; corrected the stale comment;
regression test (a `Failed` unit's >30d seed classifies Active/retained). Errs toward retaining —
deleting a revivable seed is the higher risk. Added cross-referencing comments to the other two sets so
the load-bearing divergence isn't "unified" away. Neuter-proven. Integration 762/762.

## A4. Narrow the blind swallow (replaces the earlier FSM proposal)

**Severity:** medium. **Risk:** low — additive logging, no behaviour change.

~19 convergent call sites use `catch (InvalidOperationException) { }`. The catch is not wrong in
principle — convergent callers genuinely should not fail on a lost race — but it is **too broad**: it
cannot distinguish "already converged, nothing to do" from "a caller attempted something illegal."
A1 is proof that the second case hides in there.

**Change:** each such catch logs at debug/warning with the work-unit id and attempted transition.
**No transition table changes. No control-flow changes. No new API.** Purely making the invisible
visible.

This is deliberately far narrower than the intent-split API proposed before the survey — that
proposal is **withdrawn**; the survey did not support it.

**Verification:** suite green; a deliberately-illegal transition produces a log line.

---

# Track B — Test infrastructure (test-only)

**This is the flake root cause and the highest-value work in the plan.** Report findings 1–3 are one
problem: hosts outlive their test and get deleted out from under themselves.

Sequencing is deliberate: **B1 first, because it is our measuring instrument.**

## B1. Fix the detector so it stops destroying evidence

**Must be first.** Everything else in this plan is measured with this tool.

`UnobservedTaskExceptionDetectorTests` calls `Drain()`, which empties the **process-wide** queue
mid-run, discarding every leak captured by the 753 tests that ran before it.

**Changes:**
1. Self-test filters by `SelfTestMarker` instead of draining the shared queue.
2. Settle the open question of **whether the end-of-run summary is visible at all** under
   `dotnet test` — it could not be confirmed even at `verbosity=detailed`. If `ProcessExit` stderr is
   swallowed, move the summary somewhere observable (a file artifact, or fail-on-leak behind an
   opt-in env var).

**Verification:** deliberately leak a faulted task in an early test; confirm it appears in the
end-of-run summary. Today it would not.

**Note:** until B1 lands, treat *all* "zero unobserved exceptions" readings — including today's clean
run — as unproven.

### B1 — DONE (2026-07-20)

Implemented:
- Two queues (`Captured` / `SelfTestCaptured`) rather than one queue plus a filter, so self-test
  interference is structurally impossible rather than dependent on future callers remembering.
- `Drain()` made **private** — only the end-of-run summary may empty the real-findings queue.
- Summary now also written to `unobserved-task-exceptions.txt` in the test output directory.
  Confirmed the stderr-only channel was **not** relayed by `dotnet test`, even at
  `-l "console;verbosity=detailed"` — so the file is the dependable channel.

Verified by neutering, twice:
1. Restored the original shared-queue behaviour → the planted real leak vanished entirely (no report
   file at all). Confirms the original defect was real.
2. **First regression test I wrote was itself unfalsifiable** — it asserted on queue *counts*, which
   pass trivially when the queue is empty, and it passed against the neutered code. Rewritten to
   plant a genuine unmarked leak and assert that specific finding survives; it now fails under the
   neuter with the intended message. Cleans up its own sentinel so no phantom appears in the report.

**Result: 754/754 green, and the "zero unobserved exceptions" reading is now trustworthy for the
first time.** All other projects green (Core 42, Contracts 24, Tasks 14, AgentRuntime 109, Merge 71,
Projections 37).

### Live flake specimen captured during B1 verification

`ScopedTreeFetchTests.PrefetchScopeAsync_warms_exactly_the_scoped_file_blobs` failed **1 of 3** full
runs; passes in isolation and in the 2 subsequent full runs. Unrelated to B1 (detector-only change).

This is a working example of the B2/B3 target family — and `ScopedTreeFetchTests.cs:109` is one of the
inline `Directory.Delete` sites from report §1. **Use it as a canary for B2/B3**: if it stops
recurring after teardown ownership is fixed, that is direct evidence the fix worked.

## B2. Async teardown + guarded delete

**Severity:** high. Fixes the flake signature directly.

Today: 61 `IDisposable` classes, 69 unguarded `Directory.Delete(recursive)` in `Dispose()`, **zero**
`IAsyncDisposable`/`IAsyncLifetime` anywhere — so no test can await outstanding work before deleting
its files.

**Changes:**
1. Introduce a shared async teardown base (xunit v2 `IAsyncLifetime`) that stops hosted services,
   awaits background drain, then deletes.
2. Migrate the 61 classes onto it.
3. Guarded delete with bounded retry — the Windows handle-release race is real and timing-dependent.
   Two files already hand-roll a chmod-then-delete workaround for git's read-only `.git/objects`
   (`RepositoryIdentityHintsTests.cs:142`, `RepositoryRegistryTests.cs:235`); fold those in.

**Do not** simply wrap `Directory.Delete` in `try {} catch {}`. That hides the leak instead of fixing
it and would undo B1's value.

**Migrate incrementally** — 61 classes is too large for one commit. Suggested batching: the 13
`"Sqlite"`-collection classes first (highest contention, best signal), then the rest alphabetically.

**Verification:** run the suite 5× consecutively; zero teardown `IOException`.

### B2 batch 1 — DONE (2026-07-20): the 13 "Sqlite"-collection classes

Shipped:
- `TestTeardown.ClearSqlitePoolsAndDeleteAsync(params roots)` — `ClearAllPools()` then a **bounded
  retry** delete (8 attempts, ~1.3s total). A transient handle-release race clears in the first
  attempt or two; a real leaked handle exhausts the budget and **throws with the path named**, so a
  leak surfaces loudly instead of being swallowed. Also folds in the read-only-attribute clear that
  two tests previously hand-rolled for git's `.git/objects`.
- Unit-tested the helper directly (`TestTeardownTests`), including verify-by-construction of both the
  transient-race-succeeds and permanent-leak-throws cases.
- Migrated all 13 classes from `IDisposable`/`Dispose` to `IAsyncLifetime`/`DisposeAsync`.
  `RepositorySyncServiceTests` gains the `ClearAllPools()` it was previously missing.

**Verification results:** zero teardown `IOException` across ~11 full-suite runs. Migrated classes
pass. Full suite 759/759 on clean runs.

**B2 exonerated of the residual flakes** (all pre-existing, none teardown-related):
- Ruled out by A/B test: same 67-test filter → pre-B2 baseline 3/3 pass, B2 5/5 pass. The one
  `RoomReplication` reconnect failure occurred only in a 72-test run where `TestTeardownTests` (one of
  which deliberately holds a locked handle ~1.3s) added parallel load and tipped a known-fragile
  WebSocket-reconnect timing test — a C1/C2 target, not a teardown fault.
- `ScopedTreeFetchTests.PrefetchScopeAsync_warms_exactly_the_scoped_file_blobs` flaked pre-B2 (during
  B1) and again after — it is **100% in-memory** (no SQLite/host/filesystem), so it cannot be a
  teardown issue. Root cause found: see the new finding below.

### New finding (B-track): `RecordingBlobStoreProvider.GetHashes` is a data race

`RecordingBlobStoreProvider.GetHashes` is a plain `List<string>` (`RecordingBlobStoreProvider.cs:22`)
appended at `:26` from `WorkUnitPrefetchService`'s concurrent `Task.Run` fetches. `List<T>.Add` is not
thread-safe; a lost update drops an entry, so `PrefetchScopeAsync_warms_exactly_the_scoped_file_blobs`
intermittently sees 4 recorded fetches instead of 5. This is the most frequent flake observed across
all runs, is fully independent of B2, and is a clean two-line thread-safety fix. Filed as **B2a**,
fixed as a separate commit.

### B2a — DONE (2026-07-20): locked the recorder. Verified by neuter (fails without the lock).
After the fix: ScopedTreeFetch 20/20 clean, and 4 consecutive full-suite runs 760/760 with zero
teardown failures and zero unobserved exceptions.

### B2 batch 2 — DONE (2026-07-20): the remaining 48 `IDisposable` classes

Migrated all 48 non-Sqlite-collection classes `IDisposable` → `IAsyncLifetime`, routing through a new
`TestTeardown.DeleteDirectoriesAsync` — the same bounded-retry delete **without** `ClearAllPools`
(these classes open no file SQLite db, so they must not force-close pooled connections belonging to
SQLite tests running in parallel). Done mechanically with a guard that skipped any Dispose holding
unexpected statements; none did.

**B2 is now complete: all 61 `IDisposable` test classes migrated.** Verified: clean build,
760/760 across 3 consecutive full runs, zero teardown `IOException`.

### Flake ledger (observed, for batch 2 / C-track triage)

Tests seen flaking across B1/B2 verification, so batch 2 and C-track have a canary list:
- `ScopedTreeFetchTests.PrefetchScopeAsync_warms_exactly_the_scoped_file_blobs` — **FIXED (B2a)**.
- `RoomReplicationTests.Two_hosts_replicate_..._reconnect` — WebSocket reconnect timing; C1/C2 target.
- `StudioHostSmokeTests.Build_registers_studio_services` — seen once; cause not yet captured. Only
  builds the DI container and resolves 5 services (no host start, no DB). Watch for recurrence.

## B3. Close the structural host leaks (~131 sites)

**Severity:** high. **The important half of the leak problem** — these cannot be fixed by caller
discipline.

~40 helpers build a host internally and return only *resolved services*. The host is a local that
falls out of scope, so the caller has no handle to dispose. Examples: `ProjectionSnapshotTests.cs:18`,
`WorkUnitDagTests.cs:13`, `RepositoryRegistryTests.cs:23` (14 call sites).

**Change:** helpers return a disposable context object carrying both the host and the services, so
callers `await using` it. Combined with B2, teardown then stops the host before deleting files.

**Sequencing:** after B2, so the teardown path exists to hand ownership to.

**Verification:** no helper in `tests/` builds a host it does not return ownership of. Suite green.

### B3/B4 assessment (2026-07-20) — reduced ROI after B2, and entangled

Two findings from starting this:

1. **ROI dropped after B2.** The active harm these caused was the teardown `Directory.Delete` race,
   which B2's retry now absorbs. What remains is a leaked host lingering until GC — real (its
   `IHostedService` loops keep running, adding cross-test load) but no longer producing an observed
   failure. This is hygiene, not a live bug.

2. **B3 and B4 are entangled and NOT mechanically separable.** Both the caller-leak sites (B4) and the
   structural-leak helpers (B3) are the same token: `var app = StudioWebApplication.Build(`. A blunt
   "prepend `await using`" pass is *wrong* for the B3 helpers — they do
   `var app = Build(...); return (app.Services...);`, and `await using` there disposes the host on
   return, handing back services from a disposed provider. It compiles clean (so the build won't
   catch it) and only fails at runtime. Confirmed by trying it: it wrongly wrapped 6 helper files
   (ArtifactInvalidation, FileScopeAmendment, ProjectionMaterialization, ProjectionSnapshot,
   RepositoryRegistry, WorkUnitDag) whose helpers return the provider. Reverted.

**Revised approach when we do this:**
- **B3 first, not B4.** The ~6 tuple-returning helpers need a disposable context type
  (`sealed class TestHost(WebApplication app) : IAsyncDisposable` exposing the services), and each of
  their call sites becomes `await using var ctx = BuildServices(); var (a,b,c) = ctx;`. Only once a
  helper hands back ownership can its `var app = Build` be safely scoped.
- **B4 after**, and per-site: wrap only the `var app = Build(...)` that live in a `[Fact]` body and
  are not returned. Cannot be a blind regex.

**Recommendation:** defer B3/B4 as lower-priority hygiene now that B2 removed the active flake, and
spend the next effort on the remaining **code defects** (A2 `ApplyBranchAsync` retry, A3
`TerminalStatuses`) and the cheap test-correctness wins (C1, C3), which address real issues rather
than leaked in-memory hosts. Revisit B3/B4 as a deliberate, non-mechanical pass.

## B4. Close the caller-side host leaks (~71 sites)

**Severity:** medium. Mechanical once B3 lands.

~40 helpers already return the `WebApplication`; 124 call sites dispose it, **71 don't**. Same file
often does both (`CrossRepoFileReferenceTests.cs` — 5 disposed, 6 leaked).

**Change:** add `await using` at the 71 sites. Plus the 33 raw `var app = StudioWebApplication.Build(...)`
sites and 17 un-disposed `BuildServiceProvider()` sites.

**Verification:** grep-level invariant — every `Build(` result is bound by `await using` or returned.

## B5. Decide the parallelism posture (decision, then config)

**Severity:** medium. **Do this LAST in Track B, not first.**

There is no `xunit.runner.json` or `.runsettings` anywhere; xunit v2 defaults apply. The one
mitigation — `[CollectionDefinition("Sqlite")]` over 13 classes — **cannot fully work**: `ClearAllPools()`
is process-global while ~150 other classes run concurrently. The file says so itself.

**Why last:** serializing tests is the standard reflex, and it would *mask* B2–B4 rather than fix
them. Once hosts are owned and torn down properly, we may not need it — and we'll be able to tell,
because we'll have the measurement. Serializing first would destroy that signal and cost the 46 s
runtime.

**Options, to decide *with data* after B2–B4:**
- **(a) Do nothing** — if B2–B4 eliminate the contention. Preferred if it holds.
- **(b) Widen the Sqlite collection** to every SQLite-touching class, making `ClearAllPools()` safe.
- **(c) Disable assembly parallelism** — the blunt instrument. Costs wall-clock; reserve for
  irreducible contention.

**Verification:** 10 consecutive green runs, and a recorded runtime delta.

---

# Track C — Test correctness (test-only)

Tests that pass without proving anything. Small, high-clarity, independent of A and B.

## C1. The two tests that pass *because* a timeout wins

**Severity:** medium — these are false assurance.

- `RoomReplicationTests.cs:365` — `Assert.Null` after `WhenAny(pending, Task.Delay(3s))`. **A hung
  socket makes this green.**
- `ArtifactSurfacedEventTests.cs:113` — `Task.Delay(1000)` then `Assert.Empty`.
- `RoomReplicationTests.cs:285` — `Task.Delay(7s)` then assert (longest sleep in the suite).

**Honest caveat:** these prove a *negative* ("no message arrives"), and
`ArtifactSurfacedEventTests.cs:111` is candid that there is no deterministic signal to poll for. A
bounded wait may be genuinely irreducible.

**Change:** where a positive signal exists, wait on it. Where none does, keep the wait but make the
test **state that it is a bounded-wait negative assertion** and assert something that distinguishes
"nothing happened" from "we timed out waiting to find out". Do not simply extend the timeouts.

**Verification:** inject a deliberate failure the test should catch; confirm it now fails. If it
still passes, the test is still worthless and needs rethinking.

## C2. Unify the poll-loop timeout convention

**Severity:** medium.

57 poll loops across 24 files use **four different** timeout behaviours: throw (6 sites),
`Assert.Fail` (2 sites), **silent fall-through (the majority)**, and silent last-try return
(`ReadBeforeWriteEnforcementTests.cs:83`). In the majority case a timeout is indistinguishable from a
wrong value.

**Change:** one shared `PollUntilAsync` helper that **throws `TimeoutException` with the condition
text** on expiry. Migrate all 57.

**Expect this to surface latent failures** — some loops may currently be timing out and passing on the
following assertion by luck. That is the point. Any such discovery is a **finding, not a regression**;
triage it (test bug vs code bug → Track A) rather than tuning the timeout up.

Timeout values range 5 s → 180 s with no rationale. Normalise, but preserve deliberately-long ones
(`AutomatedReviewIntegrationTests.cs:184` at 180 s re-drives approvals per tick).

**Verification:** suite green; each migrated loop names its condition on failure.

## C3. Fixed temp paths in Merge.Tests

**Severity:** low, mechanical.

`InMemoryMergeServiceTests.cs:604,643,648,669` use fixed paths; three tests share literal
`%TEMP%\seed-repo`. Every other temp root in the repo uses `Guid.NewGuid()`.

**Change:** Guid-suffix them. **Verification:** suite green.

## C4. Record, do not fix: the widened assertions

5 sites accept multiple outcomes (`StaggeredChildCompletionTests.cs:111` — `Merged or Completed`;
`FanOutIntegrationTests.cs:70` — `Queued or Executing or Proposed`).

**No change proposed.** Several are legitimately correct — genuinely multiple valid outcomes, and
`StaggeredChildCompletionTests.cs:106` argues its case well ("What must never happen is staying
non-terminal forever"). Narrowing them risks re-introducing flakes for no gain.

Listed only so a future reader knows they were reviewed and deliberately left alone.

---

# Suggested sequencing

**B1 first — it is the measuring instrument, and it is currently lying.**

```
B1  detector           ── unblocks measurement for everything else
 ├─ A1  diagnose Open->Completed   (needs B1)
 └─ B2  async teardown             (biggest single flake win)
     └─ B3  structural leaks
         └─ B4  caller leaks
             └─ B5  parallelism decision (with data)

Independent, any time:  A2, A3, A4, C1, C2, C3
```

**Highest value:** B2 → B3. That is the flake root cause.
**Highest risk of scope creep:** C2 (will surface latent failures) and A3 (differences may be
intentional).
**Lowest risk:** A2, C3.

## Explicitly NOT in this plan

- Any transition-table edit, especially `Completed -> Merged`.
- The intent-split `Command`/`Converge` API — **withdrawn**; the survey did not support it.
- Splitting Integration.Tests into unit/integration projects. Real (716 of 953 tests are in one
  "integration" project, many not integration at all) but it is a large reorganisation with no
  correctness payoff, and it would collide with every phase here. Worth its own discussion later.
- Retiring the `domain` field — excluded by prior decision.
- Deleting or skipping any test.
