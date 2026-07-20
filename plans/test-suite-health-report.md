# Test-suite health report

**Date:** 2026-07-20
**Scope:** all 7 test projects, 953 test attributes.
**Purpose:** establish what is actually wrong with the tests *before* planning any fix, so we can
tell test defects apart from code defects and avoid changing working production code to suit tests.

---

## 0. Baseline: the suite is green

```
Integration.Tests:  753 passed, 0 failed, 0 skipped — 46 s
```

Nothing is failing. **Zero tests are skipped or quarantined** anywhere in the repo. So "what is wrong
with the tests" is not "they fail" — it is that several of them *cannot* fail, several leak resources
that make *other* tests fail, and the suite's own diagnostics are unreliable.

This matters for sequencing: there is no fire. We can be conservative.

### Size and shape

| Project | Files | Tests |
|---|---:|---:|
| **Integration.Tests** | 163 | **716** |
| AgentRuntime.Tests | 12 | 100 |
| Merge.Tests | 5 | 71 |
| Projections.Tests | 1 | 33 |
| Tasks.Tests | 2 | 14 |
| Contracts.Tests | 3 | 13 |
| Core.Tests | 1 | 6 |

**75% of the suite lives in one "Integration" project**, and essentially 100% of the pathology below
lives there too. The other six projects have zero sleeps, zero poll loops, zero `IDisposable`
classes, zero SQLite handling. They are clean. This is a single-project problem.

---

## 1. Resource lifetime — the root of the flake family

This is the most consequential finding and the one that explains the recurring
"different test each run" failures.

### ~200 leaked hosts against 716 tests

`StudioWebApplication.Build(...)` is the only host entry point. 209 call sites:

| form | count | disposed? |
|---|---:|---|
| `await using var x = Build(...)` | 135 | yes |
| returned from a helper | 54 | caller's choice |
| `var app = Build(...)` — no `using` | **33** | **no** |

Plus **17 `services.BuildServiceProvider()` sites, none in a `using`.**

Two distinct leak shapes:

**(a) Caller *could* dispose but doesn't — ~71 sites.** ~40 helpers return the `WebApplication`;
124 call sites dispose it, 71 don't. The same file routinely does both — `CrossRepoFileReferenceTests.cs`
disposes at 5 sites and leaks at 6.

**(b) Caller *cannot* dispose — ~131 sites.** ~40 helpers build a host internally and return only
*resolved services*; the host is a local that falls out of scope. Disposal is impossible by
construction. e.g. `ProjectionSnapshotTests.cs:18`, `WorkUnitDagTests.cs:13`,
`RepositoryRegistryTests.cs:23` (14 call sites).

Shape (b) is the important one: no amount of caller discipline fixes it. It needs a helper-signature
change.

### Started but never stopped

`.StartAsync(` — **151** calls. `.StopAsync(` — **55**. **33 files start hosted services and never
stop them anywhere in the file.**

The suite already documents why this bites, at `MultiUserMilestoneTests.cs:321`:

> `IHost.DisposeAsync` disposes the container but does NOT run hosted services' `StopAsync` … the
> test could reach `Dispose()` -> `Directory.Delete` while a checkpoint was still writing to
> hostA/nodes.db, which is exactly the IOException this suite kept flaking on.

### 61 unguarded `Directory.Delete` in `Dispose()`

- 61 `IDisposable` test classes; **69 `Directory.Delete(recursive: true)` calls inside `Dispose()`**.
- **0 of the 61 use try/catch.**
- **0 classes implement `IAsyncDisposable` or `IAsyncLifetime`** — there is no async teardown
  anywhere in the suite, so nothing can await outstanding work before deleting its files.

**Combine the three and you have the exact flake signature:** a leaked, never-stopped host keeps
writing; teardown deletes the directory under it; `Dispose` throws `IOException`; the test *body*
passed. Blame lands on whichever test xunit reports next.

---

## 2. Parallelism is default-on and unconfigured

- No `xunit.runner.json`. No `.runsettings`. No `[assembly: CollectionBehavior]`. No
  `DisableTestParallelization` anywhere.
- xunit 2.9.3 defaults apply: **collections run in parallel at `maxParallelThreads` = CPU count**.
  Each of ~150 un-attributed classes is its own collection.
- One collection exists — `[CollectionDefinition("Sqlite")]`, used by 13 classes.

**The serialization is partial by construction.** `SqliteConnection.ClearAllPools()` (18 calls across
13 files) is process-global, and the collection only serializes those 13 classes *against each
other*. They still run concurrently with the other ~150. From `SqliteTestCollection.cs:3`:

> `ClearAllPools()` … is a process-wide static call — it force-closes every pooled SQLite connection
> in the whole test process, not just the caller's own … one test's teardown can yank a connection
> out from under an unrelated SQLite-backed test still mid-flight on another thread.

So the existing mitigation cannot fully work, and the file says so.

**Isolation itself is good**, which is worth stating: 98 of 102 temp roots use `Guid.NewGuid()`, temp
roots are instance fields (fresh per test method), there are only 5 static fields (all immutable),
and zero `Environment.SetEnvironmentVariable` / `SetCurrentDirectory`. The 4 exceptions are fixed
paths in `Merge.Tests/InMemoryMergeServiceTests.cs:604,643,648,669` — three tests share the literal
`%TEMP%\seed-repo`.

---

## 3. Tests that cannot fail

**83 real sleeps** (81 in Integration.Tests) and **57 poll-until loops** across 24 files. ~65 sleeps
are poll backoffs; ~18 are unconditional "wait and hope."

Timeout behaviour is **inconsistent across four different conventions**:

| on timeout | where |
|---|---|
| throws `TimeoutException` | the shared `PollUntil*` helpers — 6 sites |
| `Assert.Fail` | 2 sites only |
| **silently falls through** to the next assertion | **the majority** — all inline `while (deadline)` loops |
| silently returns a last-try value | `ReadBeforeWriteEnforcementTests.cs:83` |

For the majority case, **a timeout is indistinguishable from a wrong value**.

### Two tests pass *because* a timeout wins

`RoomReplicationTests.cs:365`:
```csharp
var liveCompleted = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3)));
var liveMessage = liveCompleted == pending ? await pending : null;
Assert.Null(liveMessage);
```
A hung or merely slow socket produces a **green** test. Same shape at
`ArtifactSurfacedEventTests.cs:113` (`Task.Delay(1000)` then `Assert.Empty`) and
`RoomReplicationTests.cs:285` (7 s — the longest sleep in the suite).

These are negative assertions gated only on wall-clock. They are not evidence of anything.
`ArtifactSurfacedEventTests.cs:111` is honest about it: *"No deterministic 'done' signal to poll for
here (the very thing we're proving is absent)."*

### Assertions widened to absorb nondeterminism — 5 sites

e.g. `StaggeredChildCompletionTests.cs:111` accepts `Merged or Completed`;
`FanOutIntegrationTests.cs:70` accepts `Queued or Executing or Proposed`. Some of these are
legitimately correct (genuinely multiple valid outcomes), but they discriminate less than they appear
to.

### ~60 comments naming races/flakes

The suite has an extensive written record of its own history. Most important, because it draws
exactly the line this report is about — `FileSystemWorkspaceServiceReadContentionTests.cs:18`:

> This surfaced as a recurring integration-suite flake … **but the bug is in the service, not the test.**

---

## 4. The diagnostic is broken (my own defect)

`UnobservedTaskExceptionDetector` records unobserved fire-and-forget exceptions. Its self-test calls
`Drain()` — which **dequeues the entire process-wide queue** — at
`UnobservedTaskExceptionDetector.cs:131`:

```csharp
// Drain first so an unrelated leak from another test cannot make this pass spuriously.
UnobservedTaskExceptionDetector.Drain();
```

I wrote that reasoning about false positives in the self-test. But the queue is shared and this test
runs in the middle of 753 others, so **every real leak captured before it is discarded** and never
reaches the end-of-run summary.

Consequences:
- The clean run above is **not evidence of zero leaks**.
- The "4 → 0" measurements I reported previously came from the immediate per-leak stderr print (still
  working), but any summary-derived reading is unreliable.
- Separately, **I could not confirm the summary is visible at all** — even
  `-l "console;verbosity=detailed"` surfaced no detector output. Unproven either way.

Test-only, no production risk, trivially fixable (filter by marker rather than draining).

---

## 5. What the tests deliberately pin — do NOT "fix" these

Direct evidence against the FSM-loosening idea from the previous analysis.

**8 tests assert an illegal transition throws**, from `CanTransition == false`:

| test | pinned edge |
|---|---|
| `InMemoryTaskServiceTests.cs:102` | `Open -> Completed` |
| `InMemoryTaskServiceTests.cs:133` | `Completed -> InProgress` (assign) |
| `WorkUnitLifecycleTests.cs:51` | `Created -> Merged` |
| `InMemoryMergeServiceTests.cs:400` | `Approved -> ReadyForReview` |
| `InMemoryMergeServiceTests.cs:444` | `Draft -> Approved` |
| `InMemoryMergeServiceTests.cs:576` | `ReadyForReview -> Merged` |
| `InMemoryMergeServiceTests.cs:584` | `Draft -> Merged` |
| `InMemoryMergeServiceTests.cs:738` | `Rejected -> Merged` |

`WorkUnitLifecycleTests.cs:8` states the policy outright: *"the throw-based internal pattern is kept."*

**2 table-level tests** pin ~40 edges by `[InlineData]` (`Core.Tests/DomainTests.cs`).

**One explicit invariant** — `Contracts.Tests/WorkUnitStatusVectorTests.cs:158`,
`Every_terminal_status_has_zero_outgoing_transition_edges`, carrying a **"DO NOT DELETE THIS TEST"**
note and a record of catching a real regression (`CanTransition(Failed, Cancelled) == true`).

### Settled: `Completed -> Merged` must NOT be added

Three independent confirmations:

1. **No consumer distinguishes the two states.** `StudioRestEndpoints:4492`, `ProjectionManager:634`,
   `TrajectoryTools:79` all read `Completed or Merged => Converged`. A refused transition is
   therefore harmless — the unit is already terminal-success and every reader sees the right thing.
2. **The terminal invariant test would fail by design.** `Completed` is in `terminal_status_names`.
3. **`StaggeredChildCompletionTests.cs:156` exists because a goal wedged forever** when `Completed`
   stopped behaving as a one-way trap: *"the rejection retry's Executing write silently failed and
   the goal wedged forever."*

The bug at `InMemoryMergeService.cs:1069` was never the transition — it was the over-broad `try` that
also skipped the stage clear and fan-out enqueue, **already fixed** by splitting the catch.

### Narrow survivor

Same-status no-op is already legal and tested for tasks
(`InMemoryTaskServiceTests.cs:111`, `UpdateAsync_allows_same_status_as_no_op`) but not for work units
or merge proposals. That inconsistency is real and narrow. A candidate, not a mandate.

---

## 6. Code defects, not test defects

Found via the tests but genuinely production-side:

| defect | status |
|---|---|
| shutdown contract — unshutdownable `_ = Task.Run` durable writes | fixed (`StudioBackgroundWorkQueue`) |
| `FileSystemWorkspaceService` read/write retry | fixed |
| `SupersedeAsync` non-idempotent | fixed |
| **`Open -> Completed`** — a caller completes a task it never claimed | **open** — real caller bug, hidden by `catch (InvalidOperationException) { }` |
| **`ApplyBranchAsync`** has no retry — the branch-apply copy path is uncovered by the read/write retry | **open** |
| **three `TerminalStatuses` definitions disagree** on Cancelled/Failed membership (`WorkUnitCommandService:33`, `GoalGuardrailService:16`, `SnapshotRetentionPolicy:81`) | **open** |

### The swallow, reconsidered

Previously I argued for an intent-explicit API across ~19 convergent call sites. The survey
**weakens the FSM half and sharpens the swallow half**:

- Loosening the tables is wrong — they are deliberate, tested, and load-bearing.
- But `catch (InvalidOperationException) { }` is genuinely too broad: it cannot distinguish "already
  converged, nothing to do" from "a caller tried something illegal." The `Open -> Completed` bug is
  proof — it existed silently for exactly that reason.

The fix is therefore **narrower than proposed**: make the convergent sites *log* what they swallow,
rather than change what is legal. No transition table changes.

---

## 7. Summary — what is actually wrong

| # | finding | kind | severity |
|---|---|---|---|
| 1 | ~200 leaked hosts; ~131 impossible to dispose by construction | test | high |
| 2 | 61 unguarded `Directory.Delete` in `Dispose`; no async teardown | test | high |
| 3 | 151 `StartAsync` vs 55 `StopAsync`; 33 files never stop | test | high |
| 4 | parallelism unconfigured; Sqlite collection only partly effective | test | medium |
| 5 | majority of poll loops silently fall through on timeout | test | medium |
| 6 | 2 tests pass *because* a timeout wins | test | medium |
| 7 | detector discards evidence via shared `Drain()` | test | medium |
| 8 | `Open -> Completed` caller bug hidden by a blind catch | **code** | medium |
| 9 | `ApplyBranchAsync` has no retry | **code** | low |
| 10 | three disagreeing `TerminalStatuses` definitions | **code** | low |
| 11 | 4 fixed temp paths in Merge.Tests | test | low |

**1–3 are one problem**, and it is the one that produced the recurring flakes: hosts that outlive
their test, deleted out from under themselves. Everything else is secondary.

**Nothing here argues for changing the transition tables.**
