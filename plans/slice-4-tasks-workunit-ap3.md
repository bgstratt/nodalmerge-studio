# Slice 4 — Tasks + Work Units (AP-3)

Status: **Complete**

## Problem

`ITaskService` had no state machine enforcement: any status change was accepted without checking valid transitions, tasks could be created with a non-existent work unit, and `TaskTools.UpdateAsync` did an O(n) full scan via `ListAsync` to look up a single task.

## Scope

### `TaskTransitions` (Contracts.Domain)

Added `TaskTransitions.CanTransition(from, to)` matching the AP-3 execution model:

| Transition | Allowed |
|-----------|---------|
| Open → InProgress | ✅ |
| InProgress → Blocked | ✅ |
| Blocked → InProgress | ✅ |
| InProgress → Completed | ✅ |
| Any non-terminal → Cancelled | ✅ |
| Open → Completed | ❌ (must go through InProgress) |
| Open → Blocked | ❌ |
| Completed/Cancelled → anything | ❌ |

### `ITaskService.GetAsync` (Core + Tasks)

Added `GetAsync(taskId)` to the interface and implemented via `ConcurrentDictionary.TryGetValue`. Removes the O(n) scan pattern.

### `InMemoryTaskService` enforcement

- `CreateAsync` — validates the referenced `WorkUnitId` exists via `IWorkUnitService.GetAsync`; throws `KeyNotFoundException` if not found (AP-3: tasks must belong to a valid work unit)
- `UpdateAsync` — checks `TaskTransitions.CanTransition(current, new)` before writing; same-status updates (e.g. title/description change) pass through without transition check
- `AssignAsync` — validates that `current.Status → InProgress` is a legal transition before setting agent and status

### `TaskTools.UpdateAsync` (McpServer)

Replaced `ListAsync(...).FirstOrDefault(t => t.TaskId == taskId)` with `GetAsync(taskId)`.

### New test project: `NodalMerge.Studio.Tasks.Tests`

11 tests covering:
- `CreateAsync` rejects missing work unit / succeeds with valid one
- `GetAsync` returns stored task / null for unknown
- `UpdateAsync` allows valid transitions / rejects invalid / passes same-status no-ops
- `AssignAsync` transitions Open → InProgress / rejects Completed
- `ListAsync` orders by priority descending / filters by work unit

### `Core.Tests` additions

11 `TaskTransitionTests` covering the full transition matrix.

## Out of scope

- Work unit completion pre-check (no gate requiring zero open tasks to mark a WU Completed — deferred)
- Task dependency tracking (the `Dependencies` field in `WorkUnitProjectionPayload` remains empty)

## Success criteria

- [x] `TaskTransitions` state machine in domain layer
- [x] Task creation rejects invalid work unit reference
- [x] Task status update enforces valid transitions
- [x] `TaskTools.UpdateAsync` uses `GetAsync` (O(1) lookup)
- [x] 49/49 tests pass across all projects

## Next slice

**Slice 5 — Merge workflow (AP-4):** Human review states, proposal listing through MCP, and ensuring the merge gate cannot be bypassed. Then Slice 6 for Agent Runtime + Orchestrator wiring.
