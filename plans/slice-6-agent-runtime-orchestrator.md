# Slice 6 — Agent Runtime + Orchestrator

Status: **Complete**

## Problem

The agent lifecycle was half-wired:

1. `InMemoryAgentRuntimeService` had no agent→workUnit tracking and no `ListActiveAsync` — `WorkspaceSummary.ActiveAgents` was always `[]`.
2. `PauseAsync`, `ResumeAsync`, and `StopAsync` silently accepted unknown agent IDs (no `KeyNotFoundException`).
3. `SpawnAsync` in the MCP layer did not validate the work unit exists before spawning.
4. All `AgentTools` MCP methods propagated raw exceptions instead of returning structured error envelopes.

## Design decision — no circular DI dependency

The obvious fix would have been to inject `IWorkUnitService` into `InMemoryAgentRuntimeService` for spawn validation. That would create a circular dependency:

```
InMemoryWorkUnitService → IAgentControlService → InMemoryAgentRuntimeService → IWorkUnitService → InMemoryWorkUnitService
```

Instead, work-unit validation is performed at the MCP tool layer (`AgentTools.SpawnAsync`), which already has access to both `IWorkUnitService` and `IAgentControlService`. The runtime service stays pure — it tracks agent state, not domain entities.

## Changes

### `IAgentControlService` + `AgentInfo` (Core)

Added `AgentInfo` record:
```csharp
public sealed record AgentInfo(string AgentId, string WorkUnitId, string Status);
```

Added to interface:
```csharp
Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default);
```

### `InMemoryAgentRuntimeService` (AgentRuntime)

| Change | Detail |
|--------|--------|
| `_agentStatus` dict replaced by `_agents: ConcurrentDictionary<string, AgentRecord>` | Tracks `AgentId`, `WorkUnitId`, `Status` per agent |
| `SpawnAsync` | Stores `AgentRecord`; no `IWorkUnitService` injection needed |
| `PauseAsync / ResumeAsync / StopAsync` | Call `GetRequired(agentId)` → `KeyNotFoundException` for unknown agents |
| `GetStatusAsync` | Reads from `_agents`, returns "unknown" for never-spawned agents |
| `ListActiveAsync` | Returns all agents with `Status == "active"` as `AgentInfo` list |

### `InMemoryWorkUnitService` (Orchestrator)

- Injected `IAgentControlService` via constructor
- `GetSummaryAsync` now calls `ListActiveAsync()` and filters by `branchId` via work-unit lookup
- `ActiveAgents` in `WorkspaceSummary` is now populated

### `AgentTools` (McpServer)

| Method | Change |
|--------|--------|
| `SpawnAsync` | Validates work unit exists via `IWorkUnitService.GetAsync`; returns `McpJson.Error` if not found; `branchId` in response now comes from the work unit's actual branch |
| `PauseAsync` | Catches `KeyNotFoundException` → `McpJson.Error` |
| `ResumeAsync` | Catches `KeyNotFoundException` → `McpJson.Error` |
| `StopAsync` | Catches `KeyNotFoundException` → `McpJson.Error` |
| `StatusAsync` | No change needed — `GetStatusAsync` returns "unknown" for unknown agents, which is a valid response |

### New test project: `NodalMerge.Studio.AgentRuntime.Tests`

15 tests covering:

| Area | Tests |
|------|-------|
| `SpawnAsync` | agentId prefix, active status on spawn, workUnit tracked in ListActive |
| `PauseAsync` | pauses agent, throws for unknown |
| `ResumeAsync` | resumes paused agent, throws for unknown |
| `StopAsync` | stops agent, throws for unknown |
| `GetStatusAsync` | returns "unknown" for never-spawned |
| `ListActiveAsync` | excludes paused/stopped, empty when no agents |
| `RecordActionAsync` + `GetSnapshotAsync` | action appended, empty snapshot for unknown |

## Out of scope

- Agent lifecycle state machine (e.g., blocking Resume of a stopped agent) — currently any status can transition to any other
- Persisting agent identity across host restarts (in-memory only, VS Code session lifetime)
- Agent→task assignment (task.AssignedAgent field exists; wiring it via spawn is Slice 7 / extension UX)
- Snapshot compression / compaction (deferred to when agents produce real output)

## Success criteria

- [x] `PauseAsync` / `ResumeAsync` / `StopAsync` throw `KeyNotFoundException` for unknown agents
- [x] `SpawnAsync` MCP tool returns `McpJson.Error` if the work unit does not exist
- [x] `WorkspaceSummary.ActiveAgents` is populated from live agent state
- [x] `branchId` in spawn response comes from the work unit's actual branch
- [x] All agent MCP tools return structured errors, not raw exceptions
- [x] All tests pass

## Next slice

**Slice 7 — VS Code extension:** TypeScript Control Tower — branches panel, replay timeline, agent spawn/pause/resume, projection inspector, merge proposal review + approval, rollback to KnownGoodState.
