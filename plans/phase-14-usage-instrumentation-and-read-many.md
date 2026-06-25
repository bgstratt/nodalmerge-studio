# Phase 14 — Workspace Usage Instrumentation + `workspace_read_many`

Phase 13 gave Worker/Planner/Reviewer `nm_v1_workspace_search` and gave Worker
`nm_v1_workspace_replace`. That plan's "Future work" section deferred a batched
`workspace_read_many` and Roslyn/LSP-backed symbol search. Separately,
[phase-12-file-ownership-leasing.md](./phase-12-file-ownership-leasing.md) has its own deferred list —
region-level leasing, lease timeout/expiry, `produces`/`consumes` dependency inference,
release-on-proposal, multi-file joint waiting — none of which were ever justified by an observed
problem; they were written down as "if this turns out to matter" notes, not a backlog.

The two deferred lists are coupled in a specific direction: `workspace_search` changes how Workers
find files, which changes which files multiple Workers converge on, which changes how much the
file-lease queue actually gets contended. Building region-level leasing or lease timeouts now would
be designing against imagined contention. Phase 14 ships the cheap, low-risk capability item still on
the table (`workspace_read_many`) and adds just enough instrumentation to observe real usage and real
lease contention going forward — so any future Phase-12 coordination work is evidence-driven rather
than speculative. **No Phase-12-deferred item is implemented here.**

## Track A — `workspace_read_many` (capability)

`nm_v1_workspace_read_many(branchId|workUnitId, paths: string[])` →
`{ files: [{ path, content, found }], branchId }`. Batches the search → read → read → read pattern
into one round trip; a missing path comes back as `found: false` in its own slot rather than failing
the whole call, mirroring `ReadAsync`'s existing null-means-not-found convention. `paths` is clamped to
1–50 entries at the dispatcher.

Given to the same three agents that got `workspace_search` in Phase 13 — Worker, Planner, Reviewer —
as an `AllowedTools` addition only, **not** a `RequiredToolsByStage` entry: unlike `workspace_search`/
`artifact_query`, this is a round-trip-count optimization, not a correctness or governance mechanism.

Roslyn/LSP-backed symbol search remains deferred, unchanged from Phase 13 — nothing in the repo
references Microsoft.CodeAnalysis/Roslyn/any language server, and a "stub" wouldn't provide real
signal over plain `workspace_search`.

## Track B — Usage instrumentation bridge

Three new `ExecutionEventKind` values feed a new on-demand aggregation service — no new persistence,
events ride the existing `ExecutionEventStreamService` event log:

- `WorkspaceSearchExecuted` — emitted by `McpToolDispatcher.WorkspaceSearchAsync` on every search
  call (`Query`, `MatchedPaths`, `MatchCount`, `Truncated`).
- `WorkspaceReadExecuted` — emitted by both `WorkspaceReadAsync` and `WorkspaceReadManyAsync`
  (`Paths`, one or many).
- `FileLeaseContended` — emitted by `CheckFileLeaseAsync` whenever `TryAcquireOrEnqueueAsync` reports
  a conflict (`Path`, `RequestingWorkUnitId`, `HolderWorkUnitId`).

Deliberately not added: `FileLeaseGranted`/`FileLeaseReleased`. Contention pressure is fully visible
from `FileLeaseContended` alone (a per-path count tells you what's hot); granted/released events would
mostly restate "wrote without contention" and require instrumenting four separate release call sites
(`InMemoryMergeService`'s apply and reject paths, `InMemoryAgentRuntimeService.StopAsync`,
`InMemoryDeadLetterService`) for marginal signal. Revisit only if `FileLeaseContended` counts alone
prove insufficient.

`IExecutionEventStream` gained `GetEventsByKindAsync(kinds, since?)` — `GetSessionEventsAsync` is
scoped to one session, but cross-session aggregation ("top hit files across all workspaces") needs to
scan by kind regardless of session. Implemented as an in-memory filter over the event log already held
in `ExecutionEventStreamService`.

`IWorkspaceUsageMetricsService` (`WorkspaceUsageMetricsService`) computes, on demand:
- `GetTopFileHitsAsync` — paths ranked by combined search-match + read count.
- `GetLeaseContentionHotSpotsAsync` — paths ranked by `FileLeaseContended` count, with contending
  work-unit ids.
- `GetSearchUsageAsync` — search count / total matches / truncated count, optionally scoped to one
  work unit.

Exposed read-only, no dashboard panel (matching `/studio/file-leases`'s precedent):
`GET /studio/usage-metrics/file-hits`, `/lease-contention`, `/search-activity` (all accept `topN`/
`sinceHours` query params as applicable).

## Slices

| Slice | Focus | Status |
|---|---|---|
| 14a | `workspace_read_many` — contract, `FileSystemWorkspaceService` impl, dispatcher, tool defs on Worker/Planner/Reviewer, profile `AllowedTools` | Done |
| 14b | New event kinds/payloads; `sessionId` threaded through search/read/read-many/write/replace/delete dispatcher handlers; emission at `WorkspaceSearchAsync`/read paths/`CheckFileLeaseAsync` | Done |
| 14c | `IExecutionEventStream.GetEventsByKindAsync`; `IWorkspaceUsageMetricsService` + `WorkspaceUsageMetricsService` | Done |
| 14d | `/studio/usage-metrics/*` REST endpoints | Done |
| 14e | Tests — `FileSystemWorkspaceServiceReadManyTests`, `WorkspaceUsageMetricsServiceTests`, end-to-end search→read-many event emission (`WorkspaceUsageInstrumentationTests`), lease-contention event in the existing two-sibling collision integration test | Done |

## Explicitly out of scope for this phase

Every item in phase-12-file-ownership-leasing.md's "Explicitly deferred" section remains deferred —
this phase produces the data to evaluate them, it does not act on it. Also out of scope: any
dashboard/UI panel for the new metrics (REST JSON only), `FileLeaseGranted`/`FileLeaseReleased` events
(see Track B), and Roslyn/LSP symbol search (see Track A).
