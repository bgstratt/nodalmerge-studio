# Phase 5.5 — Durability & Stub Remediation

Phase 5 made the artifact DAG visible and steerable. While closing out slice 12d, a gap audit
turned up something more serious than UI polish: the production storage path
(`AddNodalMergeStorage`) has **zero integration test coverage** — every test in the suite runs
against `AddInMemoryStorage` instead — and that blind spot let a real correctness bug ship: child
work units never inherit their parent branch's files in production. Phase 5.5 closes that gap,
finishes wiring the durability the embedded NodalMerge engine already provides, and replaces the
remaining no-op stubs (`IReplayService`, `IKnownGoodStateService.CheckoutKnownGoodAsync`) with real
implementations.

This phase does **not** add new storage backends. The engine already supports `Sqlite` (local
file, what `appsettings.json` configures today) and `Mongo` (remote) for node storage, and `File`
(local) or `S3*` (remote) for blobs. We only need the local options — `Sqlite` + `File` — which
are already the configured default; the work here is entirely about Studio's own services
correctly using the durability that's already sitting underneath them.

---

## What the audit found

Researched while wrapping up 12d (see `plans/phase-5-control-plane-ui.md` for that slice's own
as-built notes) by reading the actual production wiring (`StudioServiceCollectionExtensions.cs`,
`appsettings.json`) and the embedded engine's provider composition
(`nodalmerge-host/src/NodalMerge.Host.Composition/ServiceCollectionExtensions.cs`):

**Already durable and correct — no action needed:**
`InMemoryWorkUnitService`, `InMemoryKnownGoodStateService` (state metadata only — see below),
`AgentProfileService`, `ArtifactLineageService`, `ExecutionSessionService`,
`OrchestrationDecisionLogService`, `IntentGraphService`, `InMemoryDeadLetterService` all write to
`IStudioNodeStore` and correctly implement `IRehydratable`, reading themselves back on startup.
The underlying engine's `SqliteNodeStoreProvider` is real (not a stub) and has its own
`ProviderHostRestartDurabilityIntegrationTests.cs` in the engine repo.

**Gaps, in priority order (this phase, per slice):**

| # | Gap | Where | Severity |
|---|-----|-------|----------|
| 1 | `NodalMergeBranchService.CreateBranchAsync` never calls `IFileWorkspaceService.InitBranchAsync` — child branches don't inherit parent files in production | `src/NodalMerge.Studio.Storage/NodalMergeBranchService.cs` | **High — silently breaks fan-out today** |
| 2 | No integration test exercises `AddNodalMergeStorage()` at all | `tests/NodalMerge.Studio.Integration.Tests/` | **High — this is *why* #1 shipped unnoticed** |
| 3 | `WorkSchedulerService`'s queue writes durably but never rehydrates | `src/NodalMerge.Studio.Storage/WorkSchedulerService.cs` | Medium — pending/leased work lost on restart |
| 4 | `ExecutionEventStreamService`'s events write durably but never rehydrate | `src/NodalMerge.Studio.Storage/ExecutionEventStreamService.cs` | Medium — event history (and session index) lost on restart |
| 5 | `WorkspaceOptions.UseLlmProfileSelection` (12d) is an in-memory singleton mutation — toggling it via REST doesn't survive a restart | `src/NodalMerge.Studio.Storage/WorkspaceOptions.cs`, `StudioRestEndpoints.cs` | Low-medium |
| 6 | `KnownGoodState` captures no restorable snapshot reference — `CheckoutKnownGoodAsync` just returns metadata, restores nothing | `src/NodalMerge.Studio.Contracts/Domain/KnownGoodState.cs`, `InMemoryKnownGoodStateService.cs` | Medium — "mark known good" is currently descriptive only |
| 7 | `IReplayService` is a 100%-stub no-op (`RangeAsync`/`RollbackAsync`/`InspectAsync`), wired to real MCP tools that silently do nothing | `ServiceCollectionExtensions.cs` (`StubReplayService`), `McpServer/Tools/ReplayTools.cs` | Medium — misleading to agents/users |
| 8 | `FanOutService.EnsureChildWorkUnitsAsync` has a pre-existing concurrency race — two concurrent fan-out calls for the same parent can each create a child for the same plan slice | `src/NodalMerge.Studio.Orchestrator/FanOutService.cs` | Medium — correctness bug, found while testing 12d |
| 9 | DAG Replay panel has no live stage badges (12c only wired the Artifact Explorer) | `clients/vscode-extension/src/panels/DagReplayPanel.ts` | Low — cosmetic parity |

**Noted, intentionally not actioned this phase:**
- `InMemoryBranchService` (used by `AddInMemoryStorage`, i.e. tests/dev) stays ephemeral — that's
  the correct behavior for a service named "in-memory."
- `StudioNodeKind.BranchV1` is a dead constant (`NodalMergeBranchService` writes branch nodes with
  a raw `"studio-branch"` kind string instead, bypassing `IStudioNodeStore` and going straight to
  `INodeStoreProvider`). Worth a one-line cleanup while touching this file in 13a, not a slice of
  its own.
- `StubProjectionManager` is dead code (never registered in any real DI path, kept only for tests
  that want a zero-dependency `IProjectionManager`) — harmless, no action.
- The `orchestratorAgentId = "fanout"` sentinel in 12d's new decision-log entries and LLM profile
  selection's scope (Execute-stage fan-out children only) are accepted design boundaries, not bugs.

---

## Slice 13a — Branch seeding fix + production storage path test coverage

The highest-priority slice: fixes the actual bug and builds the test harness every later slice in
this phase reuses.

### Fix

**`NodalMergeBranchService`** — inject `IFileWorkspaceService`; `CreateBranchAsync` calls
`fileWorkspace.InitBranchAsync(name, fromBranchId, ct)` after persisting the branch metadata node,
exactly mirroring what `InMemoryBranchService` already does correctly. This makes the two
`IBranchService` implementations behaviorally consistent — the only difference should be where
branch *metadata* is tracked (node store vs. in-memory dictionary), not whether file content gets
seeded.

### Test harness: a real "restart" test

Add a test helper that builds a `StudioWebApplication` against a **temp directory** for both the
SQLite db file and `WorkspaceOptions.RootPath`, then builds a **second, independent**
`StudioWebApplication` against the *same* paths to simulate a process restart. This is the
pattern 13b/13c/13d all reuse. Needs:
- A way to point `AddNodalMergeStorage()` at a temp SQLite path instead of the default
  `data/nodalmerge-nodes.db` — via `IConfiguration` overrides passed through
  `StudioWebApplication.Build`'s existing `configureServices` hook, or a small new overload if the
  config has to be set before `AddNodalMergeStorage()` runs internally.
- Each test cleans up its temp directory/db file afterward.

### Tests

- New `ProductionStorageIntegrationTests.cs` (or similar): create a parent work unit via the
  *production* storage path, write a file to its branch, fan out a child seeded from that branch,
  assert the child's branch contains the inherited file (this is the regression test for the bug).
- A genuine restart test: create state (work unit, branch with a file) on app instance #1, dispose
  it, build app instance #2 against the same paths, assert the work unit and branch content are
  both readable — proving the SQLite + file-blob durability path actually round-trips end to end,
  not just that individual services claim to implement `IRehydratable`.

### Success criteria
- A child work unit's branch contains its parent's files when created through `AddNodalMergeStorage()`.
- A second `StudioWebApplication` instance built against the same SQLite/workspace paths sees the
  first instance's work units, branches, and file content.
- Existing `AddInMemoryStorage()`-based tests are unaffected.

### As-built (13a)

- **`NodalMergeBranchService`** now takes `IStudioNodeStore` instead of `INodeStoreProvider`
  directly, and writes/reads branch metadata via `StudioNodeKind.BranchV1` instead of the raw
  `"studio-branch"` payload kind + hand-rolled node-id parsing it used before. This was the
  "one-line cleanup" the audit flagged, but doing it made `ListBranchesAsync`/`GetStatusAsync`
  collapse to single `IStudioNodeStore.ReadAllNodesAsync`/`ReadNodeAsync` calls instead of manually
  walking `INodeStoreProvider` snapshots and parsing node-id prefixes — net simpler, not just
  tidier. `CreateBranchAsync` then calls `IFileWorkspaceService.InitBranchAsync(name, fromBranchId)`
  after persisting the metadata node, exactly mirroring `InMemoryBranchService`.
- **The harness needed a new `StudioWebApplication.Build` parameter, not just the existing
  `configureServices` hook.** `NodalMerge.DotNetHost.HostApplication.Build` already accepted a
  `configureConfiguration: Action<ConfigurationManager>?` parameter, but `StudioWebApplication.Build`
  didn't expose it — and it can't be faked via `configureServices`, because
  `HostApplication.Build` calls `AddNodalMergeHostProviders(builder.Configuration)` (which binds
  `SqliteNodeStorageOptions`/`FileBlobStorageOptions` from config) *before* invoking the
  `configureServices` callback. Added `configureConfiguration` straight through to
  `StudioWebApplication.Build` so tests can call
  `cfg.AddInMemoryCollection(["NodalMerge:Storage:Sqlite:DbPath"] = ..., ["NodalMerge:Storage:FileBlobs:RootPath"] = ..., ["Workspace:RootPath"] = ...)`
  ahead of provider registration.
- **The "restart" doesn't start the host — it calls `IRehydratable.RehydrateAsync()` directly.**
  No existing integration test calls `app.StartAsync()`/`RunAsync()` anywhere in the suite (a
  separate, smaller gap than the one this slice targets — every `IHostedService`, including
  `StudioStateRehydrationService`, was previously untested even in `AddInMemoryStorage` tests).
  Starting the full host would also start `InMemoryAgentRuntimeService`'s scheduler poll loop and
  require binding Kestrel to a port. The new restart test instead resolves
  `app.Services.GetServices<IRehydratable>()` on the second `StudioWebApplication` instance and
  awaits each `RehydrateAsync()` directly — the exact call `StudioStateRehydrationService.StartAsync`
  would make, without the unrelated side effects.
- **`Microsoft.Data.Sqlite` connection pooling holds the db file open on Windows** even after every
  `SqliteConnection` used by `SqliteNodeStoreProvider` is disposed (it opens/closes a fresh
  connection per call, never holds one open itself — the pool does). The restart test's first
  `StudioWebApplication` has to be disposed and `SqliteConnection.ClearAllPools()` called before
  the temp directory cleanup in `Dispose()`, or deleting the temp dir throws `IOException`.
- New `ProductionStorageIntegrationTests.cs` covers both success criteria above; the rest of the
  suite (96 integration tests total, plus all other test projects) was re-run unchanged to confirm
  no regression from switching `NodalMergeBranchService` off `INodeStoreProvider`.

---

## Slice 13b — `WorkSchedulerService` queue rehydration

### Design

`WorkSchedulerService` implements `IRehydratable`: on `RehydrateAsync`, read all
`StudioNodeKind.SchedulerV1` nodes and repopulate `_queue`. Any rehydrated item that has a
non-null `LeasedBy`/`LeasedAt` had its lease held by an agent process that's gone now that the
host restarted — clear those two fields on rehydrate so the item becomes acquirable again rather
than permanently stuck. `AttemptCount` is preserved (it's meaningful retry history, not
lease state).

Register `WorkSchedulerService` as `IRehydratable` alongside its existing `IWorkScheduler`
registration.

### Success criteria
- Enqueue several items (some leased, some not) on app instance #1; on app instance #2 (same
  store), all items are present, previously-leased items are acquirable, attempt counts are preserved.

### As-built (13b)

- `WorkSchedulerService` now implements `IRehydratable`. `RehydrateAsync` reads every
  `StudioNodeKind.SchedulerV1` node and deserializes each payload as a `ScheduledItem`, but has to
  filter out one thing the design didn't call out: `ReleaseAsync` writes a terminal
  `{"status":"completed"}` / `{"status":"failed"}` payload (not a `ScheduledItem`) as the *last*
  write for a given `workUnitId` once it's removed from `_queue`, and `IStudioNodeStore.ReadAllNodesAsync`
  only returns the latest payload per entity id. Deserializing that terminal payload as a
  `ScheduledItem` doesn't throw — `System.Text.Json` just leaves every property at its default,
  so `WorkUnitId` comes back `null`. Rehydration skips any record where `WorkUnitId is null` rather
  than re-queuing a dead entry. Items with a stale `LeasedBy`/`LeasedAt` get those two fields
  cleared (`item with { LeasedBy = null, LeasedAt = null }`) before being added back to `_queue`;
  `AttemptCount` passes through unchanged.
- **DI registration changed shape, not just grew.** `IWorkScheduler` was previously registered as
  a standalone `services.AddSingleton<IWorkScheduler, WorkSchedulerService>()` in both
  `AddNodalMergeStorage` and `AddInMemoryStorage`. To forward `IRehydratable` to the same instance
  (the pattern every other rehydratable service in `AddRehydratableServices` already follows), that
  line moved into `AddRehydratableServices` as the usual three-registration group (concrete
  singleton, `IWorkScheduler` forwarded to it, `IRehydratable` forwarded to it) — removing the
  duplicate line from both call sites since `AddRehydratableServices` runs in both.
- New `WorkSchedulerRehydrationTests.cs` (same dual-`StudioWebApplication`-instance harness as
  13a, against the production storage path) enqueues two items, leases one, restarts, and asserts:
  both items survive, the previously-leased item's lease is cleared (and is actually acquirable
  again — verified by acquiring both items on the second instance), and `AttemptCount` is preserved
  on the item that was leased once. Full solution build + all 221 tests across every test project
  (97 integration, 124 unit) pass unchanged otherwise.

---

## Slice 13c — `ExecutionEventStreamService` rehydration

### Design

Implement `IRehydratable`: read all `StudioNodeKind.ExecutionEventV1` nodes, repopulate `_events`,
and rebuild `_sessionIndex` by grouping rehydrated events by `SessionId`, sorted by `OccurredAt`
(or whatever ordering field `ExecutionEvent` already carries) so session-scoped queries return
events in the same order they would have during the original run.

### Success criteria
- Append events across a couple of sessions on app instance #1; on app instance #2, both
  `GetByIdAsync` and the session-scoped query return the same events in the same order.

### As-built (13c)

- `ExecutionEventStreamService` now implements `IRehydratable`. `RehydrateAsync` reads every
  `StudioNodeKind.ExecutionEventV1` node, deserializes each as an `ExecutionEvent`, sorts the
  whole batch by `OccurredAt` *before* adding anything to `_events`/`_sessionIndex` — unlike
  13b's scheduler queue, there's no terminal/non-`ScheduledItem` payload shape to filter out here
  (every write to this node kind is a real `ExecutionEvent`), but `ReadAllNodesAsync` still makes
  no ordering guarantee across entity ids, and `IndexEvent` simply appends to each session's list
  in whatever order it's called — so sorting first is what makes the rehydrated session index
  match the original append order rather than the store's iteration order.
- Same DI registration shape change as 13b: `IExecutionEventStream` moved from a standalone
  `services.AddSingleton<IExecutionEventStream, ExecutionEventStreamService>()` in both
  `AddNodalMergeStorage`/`AddInMemoryStorage` into `AddRehydratableServices`'s usual three-line
  group, forwarding `IRehydratable` to the same singleton.
- New `ExecutionEventStreamRehydrationTests.cs` (same harness as 13a/13b) appends events across
  two sessions on instance #1, restarts, and asserts both `GetAsync(eventId)` and
  `GetSessionEventsAsync(sessionId)` come back identical — including order — on instance #2. Full
  solution build + all 222 tests across every test project (98 integration, 124 unit) pass
  unchanged otherwise.

---

## Slice 13d — Durable runtime settings

### Design

Generalize past just the one 12d toggle: add a `StudioNodeKind.RuntimeSettingsV1` node kind. A
small new service (or folded into `WorkspaceOptions`'s existing DI factory) writes a settings
payload to the node store whenever `/studio/options` POST mutates `WorkspaceOptions`, and reads it
back at startup — before the `WorkspaceOptions` singleton is handed to any other service — to seed
the in-memory values. Keep the shape minimal: just the fields that are runtime-mutable via REST
today (`UseLlmProfileSelection`); the file-path/byte-limit fields stay config-file-only as they
are now.

### Success criteria
- `POST /studio/options { useLlmProfileSelection: true }`, restart the host (same store), `GET
  /studio/options` still returns `true`.

### As-built (13d)

- New `StudioNodeKind.RuntimeSettingsV1` node kind and a small new `RuntimeSettingsService`
  (`src/NodalMerge.Studio.Storage/RuntimeSettingsService.cs`) — not folded into `WorkspaceOptions`'s
  DI factory, since that factory only runs once at first resolution (binding config), whereas this
  needs to fire on every `/studio/options` POST. It takes `IStudioNodeStore` and the
  `WorkspaceOptions` singleton directly, and exposes two methods: `PersistAsync` (serializes the
  one runtime-mutable field, `UseLlmProfileSelection`, into a `RuntimeSettingsSnapshot` record and
  writes it under a single fixed entity id, `"singleton"` — there's only ever one `WorkspaceOptions`
  per host) and `RehydrateAsync` (implements `IRehydratable`; reads that node back, if present, and
  sets the property directly on the same `WorkspaceOptions` instance).
- **No "before any other service sees it" ordering trick was actually needed.** The design notes
  worried about seeding `WorkspaceOptions` "before the `WorkspaceOptions` singleton is handed to
  any other service," but that's moot: `WorkspaceOptions` is a mutable class with plain
  get/set properties, every consumer (e.g. `LlmProfileSelectionService`) reads
  `options.UseLlmProfileSelection` at call time rather than snapshotting it at construction, and
  all consumers share the *same* singleton instance regardless of resolution order. Mutating that
  shared instance during `RehydrateAsync` — which already runs before the host accepts traffic via
  the existing `StudioStateRehydrationService` — is sufficient; no special registration ordering
  beyond what 0a already established was required.
- `StudioRestEndpoints.cs`'s `/studio/options` POST handler now takes `RuntimeSettingsService` as
  an extra minimal-API parameter and calls `PersistAsync` right after mutating
  `options.UseLlmProfileSelection`, before returning the response.
- `RuntimeSettingsService` is registered in `AddRehydratableServices` as a concrete singleton plus
  an `IRehydratable` forwarding registration — no domain interface to forward, since nothing
  consumes it through an abstraction (only the REST endpoint calls `PersistAsync` directly).
- New `RuntimeSettingsRehydrationTests.cs` (same harness as 13a/13b/13c) calls the exact
  mutate-then-persist sequence the REST handler makes (rather than going over real HTTP — no test
  in this suite starts the full host, per 13a's as-built note) and asserts the toggle is `true` on
  a second `StudioWebApplication` instance built against the same paths. Full solution build +
  all 223 tests across every test project (99 integration, 124 unit) pass unchanged otherwise.

---

## Slice 13e — Real known-good-state snapshot + restore

### Design

**`KnownGoodState`** gains a new field, e.g. `SnapshotBranchId`. `MarkKnownGoodAsync` creates a
real point-in-time copy by calling `IBranchService.CreateBranchAsync($"knowngood/{stateId}",
fromBranchId: branchId)` — which, after 13a's fix, correctly seeds the snapshot branch with the
source branch's current file content — and records the resulting branch id on the
`KnownGoodState`.

**`CheckoutKnownGoodAsync(stateId)`** stops being read-only metadata lookup: it copies the
snapshot branch's file content back onto the target branch (reusing
`FileSystemWorkspaceService`'s existing directory-copy logic, exposed as a small new
`IFileWorkspaceService` method if it isn't already public, e.g. `RestoreFromAsync(targetBranchId,
sourceBranchId)`), then returns the `KnownGoodState` as before so existing callers (the
`/studio/state/checkoutKnownGood` REST endpoint, `nm_v1_state_checkoutKnownGood` MCP tool) don't
need contract changes.

### Success criteria
- Mark a branch known-good, modify a file on that branch afterward, call checkout — the file
  reverts to its known-good content.
- Existing `/studio/state/markKnownGood` and `/studio/state/checkoutKnownGood` REST/MCP contracts
  are unchanged (only the implementation behind them gets real).

### As-built (13e)

- `KnownGoodState` gained `string? SnapshotBranchId = null` as a new *trailing* positional
  parameter — appending rather than inserting kept both existing call sites
  (`StudioRestEndpoints.cs`'s `/studio/state/markKnownGood` handler and `StateTools.MarkKnownGoodAsync`,
  both of which construct the record positionally) unchanged. Old persisted records without the
  field deserialize with `SnapshotBranchId = null`, which `CheckoutKnownGoodAsync` treats as
  "nothing to restore from" rather than throwing.
- **No new `IFileWorkspaceService.RestoreFromAsync` method was needed.** The design anticipated
  adding one, but `FileSystemWorkspaceService.ApplyBranchAsync(sourceBranchId, targetBranchId, ct)`
  already does exactly that — delete target files absent from source, then copy every source file
  over — it's just named for its other caller (merge-proposal apply). `CheckoutKnownGoodAsync`
  calls it directly as `ApplyBranchAsync(state.SnapshotBranchId, state.BranchId, ct)`.
- `InMemoryKnownGoodStateService` (the name predates this slice and is now slightly misleading —
  it owns durable metadata via `IStudioNodeStore` same as every other rehydratable service, "in
  memory" only describes its `ConcurrentDictionary` cache, same as `ArtifactLineageService` etc.;
  left as-is, not worth a rename) gained two constructor dependencies, `IBranchService` and
  `IFileWorkspaceService`. No circular-dependency concern: neither depends back on
  `IKnownGoodStateService`.
- `MarkKnownGoodAsync` now calls `IBranchService.CreateBranchAsync($"knowngood/{state.StateId}",
  state.BranchId, ct)` *before* persisting — `state.StateId` is generated by the caller (REST/MCP)
  ahead of the call, so it's available to name the snapshot branch deterministically.
  `CreateBranchAsync` returns the same name it was given (confirmed in both
  `NodalMergeBranchService` and `InMemoryBranchService`), so the returned id is recorded as-is on
  `SnapshotBranchId`.
- New `KnownGoodStateSnapshotTests.cs` (against `AddInMemoryStorage`, since
  `InMemoryKnownGoodStateService` itself doesn't vary by storage backend — only `IBranchService`
  does, and 13a already covers that production/in-memory parity) covers both that the snapshot
  branch is seeded with the source's current file content at mark time, and that checkout reverts
  an edited file *and* removes a file added after the mark (proving real directory-sync restore,
  not just an overwrite-only copy). Full solution build + all 225 tests across every test project
  (101 integration, 124 unit) pass unchanged otherwise.

---

## Slice 13f — Real `IReplayService`

Built on top of 13e's now-real snapshot/restore primitive and the existing artifact-lineage /
decision-log query surfaces — no new generic node-store range-scanner.

### Design

- **`RollbackAsync(branchId, knownGoodStateId)`** — thin wrapper: validates the known-good state's
  `BranchId` matches, then delegates to `IKnownGoodStateService.CheckoutKnownGoodAsync`.
- **`RangeAsync(branchId, fromNode?, toNode?)`** — returns the chronological list of artifacts
  (`IArtifactLineageService`) and orchestration events (`IOrchestrationDecisionLogService`) for
  the work unit(s) owning that branch, optionally bounded between `fromNode`/`toNode` ids, as a
  human-readable JSON timeline — reusing structure that already exists rather than scanning raw
  node-store rows.
- **`InspectAsync(branchId, nodeId?)`** — given a `nodeId`, look it up across artifacts /
  orchestration events / known-good states (whichever matches) and return a readable summary; with
  no `nodeId`, return a branch-level summary (current known-good state if any, artifact count,
  last event).

### Success criteria
- `nm_v1_replay_rollback` actually restores a branch to a prior known-good state, not a no-op note.
- `nm_v1_replay_range`/`nm_v1_replay_inspect` return real, populated data for a branch with
  history, not a `"not yet wired"` placeholder.

### As-built (13f)

- New `ReplayService` (`src/NodalMerge.Studio.Storage/ReplayService.cs`) replaces
  `StubReplayService` (deleted) in both `AddNodalMergeStorage` and `AddInMemoryStorage`. Built
  entirely on existing query surfaces — `IWorkUnitService.ListAsync(branchId)` to resolve which
  work unit(s) own a branch, `IArtifactLineageService.GetChainAsync`,
  `IOrchestrationDecisionLogService.GetEventsAsync`, and 13e's now-real
  `IKnownGoodStateService` — no new generic node-store range-scanner, per the design.
- `RollbackAsync(branchId, knownGoodStateId)` validates via `FindKnownGoodAsync(branchId)`
  that the state actually belongs to that branch *before* delegating to
  `CheckoutKnownGoodAsync(knownGoodStateId)` — `CheckoutKnownGoodAsync` itself doesn't take a
  branch id at all, so without this check a caller could pass a known-good state that belongs to
  an unrelated branch and have it silently restore that *other* branch instead of failing.
- `RangeAsync` merges artifacts and orchestration events for the branch's owning work unit(s) into
  one chronological list of `{ kind, nodeId, description, occurredAt }` entries, then optionally
  trims to between `fromNode`/`toNode` (by finding each id's index in the sorted list and
  slicing) — a node id not found in the timeline is treated as "no bound," not an error.
- `InspectAsync` with no `nodeId` returns a branch-level summary (current known-good state id,
  total artifact count, most recent timeline entry); with a `nodeId`, it checks artifacts, then
  orchestration events, then known-good states (in that order) across the branch's owning work
  units and returns whichever matches, or an `error` field if none do.
- All three methods serialize with `PropertyNamingPolicy = JsonNamingPolicy.CamelCase` plus
  `JsonStringEnumConverter` — matching the casing/enum-as-string shape every other JSON surface in
  the Studio Host already uses (`ConfigureHttpJsonOptions` in `StudioServiceCollectionExtensions.cs`),
  since these results flow to the same MCP/agent consumers.
- New `ReplayServiceTests.cs` exercises all three methods against real artifact/orchestration-event/
  known-good-state history, including the cross-branch rollback rejection (asserting the *real*
  branch's file content is untouched when rejected).
- **Found and fixed a latent flaky-test bug in 13b's `WorkSchedulerRehydrationTests.cs` while
  re-running the full suite for this slice** — unrelated to replay, but caught because the full
  suite is run after every slice. That test tried to force a specific one of two enqueued items to
  end up leased by re-enqueuing whichever item `TryAcquireAsync` happened to return first if it
  wasn't the intended one — but `EnqueueAsync` leaves already-leased items unchanged (`existing.LeasedBy
  is not null ? existing : ...`), so the "release it back" re-enqueue was a no-op, and the retry's
  `TryAcquireAsync` just re-acquired the same item via its own "reacquire" fast path. This only
  surfaced as a failure when `ConcurrentDictionary` enumeration order picked the other item first —
  passed by luck on every prior run in this session, then failed during this slice's full-suite
  run. Fixed by labeling "leased" / "unleased" by which item actually got acquired rather than
  presupposing it; verified deterministic across 10 repeated runs after the fix.
- Full solution build + all 228 tests across every test project (104 integration, 124 unit) pass.

---

## Slice 13g — `FanOutService` concurrency fix

### Design

`FanOutService.ProcessAsync` gains a per-parent-work-unit async guard (e.g. a
`ConcurrentDictionary<string, SemaphoreSlim>` keyed by `parentWorkUnitId`, acquired for the
duration of `EnsureChildWorkUnitsAsync` + the enqueue loop) so two concurrent fan-out calls for the
same parent — one from an explicit caller, one from the orchestrator loop's own
post-turn fan-out — serialize instead of racing. This was hit directly while writing 12d's tests
(`LlmProfileSelectionTests.cs`'s comments document the workaround); this slice fixes the
underlying race instead of continuing to work around it in tests.

### Success criteria
- Two concurrent `TryFanOutFromPlanAsync` calls for the same parent + plan produce exactly one
  child per slice, not two.
- `LlmProfileSelectionTests.cs`'s workaround (writing `plan.json` before spawning the orchestrator,
  never calling `TryFanOutFromPlanAsync` directly) can be simplified or left as-is — either is
  fine, but the underlying race it was dodging no longer exists.

### As-built (13g)

- `FanOutService` gained a `ConcurrentDictionary<string, SemaphoreSlim> _parentGates`, keyed by
  `parentWorkUnitId`. `ProcessAsync` acquires the parent's gate around the section the design
  specified — `BuildSliceMapAsync` (the existing-children snapshot), `EnsureChildWorkUnitsAsync`,
  and the enqueue loop — released in a `finally`. The snapshot read had to move *inside* the gate
  (it was being built just before the gate in an earlier draft): a caller that waited on the gate
  needs to see the children the previous holder just created, not a snapshot taken before either
  acquired it, or the fix wouldn't actually close the race.
- Left as-is, not touched: `LlmProfileSelectionTests.cs`'s workaround comment (writing `plan.json`
  before spawning the orchestrator, never calling `TryFanOutFromPlanAsync` directly) — both
  options the success criteria offered are fine, and this one needed no change to keep passing.
- New `FanOutConcurrencyTests.cs` fires 5 concurrent `TryFanOutFromPlanAsync` calls at the same
  parent + a 3-slice plan and asserts exactly 3 children (one per slice), not up to 15. Getting
  this test to actually exercise the race took a second pass: every operation behind
  `AddInMemoryStorage()` completes synchronously (`Task.FromResult`/`Task.CompletedTask`, no real
  I/O), and awaiting an already-completed `Task` doesn't yield back to the scheduler — so without a
  genuine async gap, "concurrent" calls just ran to completion one after another and never
  overlapped, regardless of whether the race was fixed (confirmed: the test passed even with the
  gate artificially disabled). Fixed by adding a small `IWorkUnitService` decorator
  (`DelayedChildrenWorkUnitService`, registered via `configureServices` to override the
  DI-resolved instance for this test only) that inserts a real `Task.Delay` — a genuine yield
  point — around `GetChildrenAsync`, the exact read `BuildSliceMapAsync` uses, widening the race
  window enough for `Task.WhenAll`'s concurrent calls to actually interleave there. Verified the
  test fails deterministically (15 children, every time) with the gate disabled and passes
  deterministically (3 children, every time) with it enabled, across repeated runs both ways.
- Full solution build + all 229 tests across every test project (105 integration, 124 unit) pass.

---

## Slice 13h — DAG Replay panel stage-badge parity

### Design

`DagReplayPanel.ts` already tracks `workUnitId`/`branchId` per node (per its existing message
types). Extend it to open the same `/ws/runtime` `work-unit-stage-changed` subscription the
Artifact Explorer uses (12c), and badge the matching branch/commit node by `workUnitId` the same
way `ArtifactExplorerPanel.ts` badges its work-unit nodes. Needs a short investigation spike at
the start of this slice into `dag-replay/main.ts`'s exact node-rendering model (which wasn't fully
mapped during 12c) to confirm node-to-`workUnitId` association is reliable for every node type the
panel renders, not just the ones explicitly created via "Branch from here."

### Success criteria
- Opening DAG Replay during an active run shows the same live stage badges the Artifact Explorer
  shows, on the matching branch/commit nodes.

### As-built (13h)

- **Investigation spike finding**: `dag-replay/main.ts`'s node model (`branchReplay.ts`'s
  `ReplayNode`/`BranchStream`) has no `workUnitId` field at all — nodes and lanes are keyed by
  `branchId` only. That's fine because the association is already 1:1 and already exists: every
  work unit owns exactly one branch, and `DagReplayPanel.ts`'s one-time `init` payload (and the
  `branchCreated` message for manually-created branches) already carries
  `{ workUnitId, branchId, goal }` triples — the same shape `goals` (branchId → goal, used for lane
  labels) was already built from. So "badge the node by workUnitId" became "track stage by
  branchId, fed by a workUnitId → branchId lookup built from data the panel already receives,"
  not a new wiring problem.
- **Found a real bug while tracing the message path, not just a gap**: `wsClient.ts`'s
  `WsClient.connect()` treats *any* incoming `/ws/runtime` frame that isn't `type: 'pack'` as a
  live DAG op (`append-runtime-event`, which adds it as a graph node). Subscribing to
  `work-unit-stage-changed` without special-casing it first would have made every stage change
  show up as a spurious fake commit node on the graph. Added an `onStageChange` callback to
  `WsClient`'s constructor and a `msg.type === 'work-unit-stage-changed'` branch ahead of the
  `pack` check that routes to it instead of falling through.
- `dag-replay/main.ts` tracks `stages: Record<branchId, stage>` and `workUnitIdToBranchId`,
  seeded from `WorkUnit.currentStage` in the `init` payload (so a panel opened mid-run shows
  current stages immediately, not just stages that change after it opens) and kept live via the
  new `onStageChange` callback. `DagReplayPanel.ts`'s `WorkUnit` interface and `init` payload
  gained `currentStage` (the REST `/studio/workunits` response already included it —
  `StudioRestEndpoints.ToWorkUnitResponse` — `DagReplayPanel.ts` just wasn't reading it).
- `dagRenderer.ts`'s `renderDag` gained a `stages` parameter and draws a small colored badge chip
  (first letter of the stage, e.g. "P"/"E"/"R"/"M") above each branch's *head* node only — the
  stage describes the work unit as a whole, not any one historical node, so badging every node in
  a lane would be misleading. Same color palette as `ArtifactExplorerPanel.css`'s
  `.badge.stage.*` rules, so a work unit reads as the same color in both views.
- **Pre-existing gap, not fixed here, out of scope**: `DagReplayPanel.ts` never refreshes its
  work-unit list after the initial `init` (no polling, unlike `ArtifactExplorerPanel`'s 2s poll)
  — so a child work unit created by the orchestrator *after* the DAG Replay panel was already open
  has no `workUnitId → branchId` entry, and its stage badges (and even its lane label/goal) won't
  appear until the panel is reopened. This already affected `goals` before 13h; stage badges
  inherit the same limitation rather than introducing a new one.
- Verified via `tsc --noEmit` (two pre-existing unrelated errors in `LmApiProxy.ts` and `main.ts`'s
  `setStatus`, confirmed present before this slice's changes too via `git stash`) and `npm run
  compile` (esbuild bundle builds clean). Not verified in a running VS Code Extension Host — no
  browser/VS Code automation tool was available in this session to drive the actual webview, so
  this is a code-level verification only, not a confirmed visual check.

---

## Slice ordering

13a → 13b → 13c → 13d → 13e → 13f → 13g → 13h

- **13a first**: fixes the highest-severity bug and builds the dual-instance "restart" test
  harness that 13b/13c/13d all reuse.
- **13b/13c/13d** (queue, events, settings rehydration) are independent of each other and of
  13e–13g; ordered together because they're the same shape of fix and reuse 13a's harness.
- **13e before 13f**: real rollback needs the real snapshot/restore primitive to exist before
  `IReplayService.RollbackAsync` can delegate to it.
- **13g** (fan-out race) is independent of the durability work but grouped near the end since it's
  a correctness fix discovered as a side effect of 12d, not part of the durability narrative.
- **13h last**: cosmetic UI parity, lowest severity, no dependencies on anything else in this phase.

---

## Files not touched in Phase 5.5

| File | Reason |
|------|--------|
| `InMemoryBranchService.cs` | Intentionally ephemeral (test/dev mode) — correct as-is |
| Remote providers (`MongoNodeStoreProvider`, `S3*` blob providers) | Out of scope — local SQLite + File only, per phase goal |
| `StubProjectionManager.cs` | Dead code, harmless, not worth the churn |
| 12d's LLM profile selection scope (Execute-stage children only) | Accepted design boundary, not a bug |

---

## After Phase 5.5

A restart of the Studio Host should lose nothing: every work unit, branch (with correctly-seeded
file content), scheduler queue item, event, runtime setting, and known-good state survives and
rehydrates. `nm_v1_replay_*` tools do what their descriptions say instead of silently no-op'ing.
The production storage path has real test coverage going forward, so this class of bug — a service
behaving correctly in `AddInMemoryStorage` tests while silently broken in `AddNodalMergeStorage`
production — can't ship unnoticed again.

Only then does a Phase 6 planning pass make sense — there's no point designing the policy/validator
layer or cross-repo work units on top of a foundation that loses state on restart.
