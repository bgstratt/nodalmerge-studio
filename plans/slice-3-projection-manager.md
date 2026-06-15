# Slice 3 — Projection Manager

Status: **Complete**

## Problem

`StubProjectionManager` returned hardcoded stub payloads regardless of request type or scope. Agents calling `nm.v1.projection.get` received no real data about work units, tasks, or proposals.

## Scope

Replaced `StubProjectionManager` with `ProjectionManager` that materializes real projections from the in-memory authoritative service layer.

### Projection types implemented

| Type | Data source | Scope parameters |
|------|-------------|-----------------|
| `WorkUnit` | `IWorkUnitService` + `ITaskService` | `workUnitId`, `branchId` |
| `Task` | `ITaskService` | `workUnitId` |
| `MergeProposal` | `IMergeService` | `branchId` (filters on `SourceBranch`) |
| `ExecutionSnapshot` | `IAgentRuntimeService` | `agentId` + `workUnitId` (both required) |
| `AuthoritativeState` | `IWorkUnitService` (completed WUs as merged state) | `branchId` |

### Compression levels

Each type applies level-based compaction:

| Level | Behavior |
|-------|----------|
| `Full` / `Normal` | Typed payload record with all fields |
| `Compact` | Key IDs + counts; long lists omitted |
| `Emergency` | Absolute minimum: status, counts, next action |

## Design notes

- `ProjectionManager` injects only services it actually calls: `IWorkUnitService`, `ITaskService`, `IMergeService`, `IAgentRuntimeService`. `IKnownGoodStateService` is not needed at the projection layer.
- `CompactAsync` delegates to `GetAsync` at the target level — no separate compaction path.
- `TaskStatus` ambiguity with `System.Threading.Tasks.TaskStatus` resolved with a `using` alias in both the implementation and test files.
- `StubProjectionManager` retained as `internal` for tests requiring a zero-dependency `IProjectionManager`.

## Out of scope

- NodalMerge DAG query primitives as projection input (deferred; currently projections read from in-memory service state)
- Projection caching and invalidation (the `IProjectionManager.CompactAsync` hook exists; invalidation triggers deferred to Slice 5+)
- `Dependencies` field in `WorkUnitProjectionPayload` (no dependency tracking yet; returned as empty list)

## Success criteria

- [x] `nm.v1.projection.get` returns real work unit, task, merge, snapshot, and authoritative state data
- [x] All four compression levels produce distinct outputs
- [x] 14 projection tests pass (routing + payload shape + level reduction)
- [x] Full test suite: 26/26 pass

## Next slice

**Slice 4 — Tasks + Work Units (AP-3):** Tighten the execution model — task priority ordering, work unit lifecycle transitions, task→work-unit coupling, and ensuring the MCP `task.*` and `workunit.*` tools enforce correct state machine rules end-to-end.
