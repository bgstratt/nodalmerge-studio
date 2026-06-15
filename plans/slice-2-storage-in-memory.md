# Slice 2 — Storage: in-memory foundation

Status: **Complete**

## Problem

Slice 1 left Branch, KnownGoodState, and WorkspaceSummary as stubs returning empty data. Before wiring real NodalMerge node persistence, the authoritative entities need to be functional in-memory so the full MCP surface works end-to-end within a VS Code session.

## Decision: in-memory first

NodalMerge Studio runs embedded in-process alongside the NodalMerge host. For v1, the process lifetime is the session lifetime — there is no cross-session durability requirement yet. In-memory storage is therefore correct and sufficient.

The `IStudioNodeStore` interface is the future seam for NodalMerge node-backed persistence. The service implementations (`InMemoryWorkUnitService`, `InMemoryTaskService`, etc.) do not write through to it yet; that wiring lands when the storage layer advances to NodalMerge room operations.

## Branch context

Workers operate with one active branch — the `WorkUnit.BranchId` set at creation by `IBranchService.CreateBranchAsync`. A worker may create sub-branches off its own work branch (for isolated experiments) via `CreateBranchAsync(name, fromBranchId: workUnit.BranchId)`. NodalMerge peer replication handles convergence at the room/CRDT layer beneath this.

`BranchContext` does not need to be a DI ambient scope. Branch identity flows through the entity graph (`WorkUnit.BranchId`, `Task.WorkUnitId → WorkUnit`) and through explicit MCP parameters where needed.

## Scope

| Area | Change |
|------|--------|
| `InMemoryStudioNodeStore` | Swapped `Dictionary` for `ConcurrentDictionary` (thread-safety) |
| `InMemoryBranchService` | Replaced stub — tracks branches with parent link for sub-branch topology |
| `InMemoryKnownGoodStateService` | Replaced stub — stores KGS keyed by `StateId`, queryable by `BranchId` |
| `IMergeService.ListAsync` | Added to Core interface; implemented in `InMemoryMergeService` |
| `WorkspaceSummary` | Now returns real active WUs, failures, pending merges, and KGS |

## Out of scope

- Writing authoritative entities through `IStudioNodeStore` (services keep their own `ConcurrentDictionary`)
- NodalMerge room/map operations for entity persistence
- `ActiveAgents` in `WorkspaceSummary` (deferred until `IAgentControlService` gains a `ListActiveAsync` API)
- `IReplayService` — stays as a stub; replay delegates to the NodalMerge engine and has no in-memory equivalent

## Success criteria

- [x] Solution builds with 0 errors
- [x] `IBranchService.ListBranchesAsync` returns branches created via `CreateBranchAsync`
- [x] `IKnownGoodStateService.FindKnownGoodAsync` returns states stored via `MarkKnownGoodAsync`
- [x] `IWorkspaceService.GetSummaryAsync` populates `ActiveWorkUnits`, `Failures`, `PendingMerges`, `KnownGoodStates`

## Next slice

**Slice 3 — Projection Manager:** materialize real `WorkUnitProjection`, `TaskProjection`, and `ExecutionSnapshotProjection` from the in-memory authoritative state, replacing `StubProjectionManager`.
