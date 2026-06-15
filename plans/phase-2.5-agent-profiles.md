# Phase 2.5 — Agent Profiles & Specialized Context

Phase 2 (slices 8a–8f) delivers a working vertical slice: one orchestrator, one worker, any LLM provider, human merge gate, integration tests. Phase 2.5 externalizes the hardcoded behavior from the loop classes into data, so that new agent roles — planners, reviewers, domain specialists — can be configured without code changes. This is the prerequisite for Phase 3 (multi-agent fanout), because once you're spawning several workers you immediately need to know *what kind of worker* each one is.

---

## Pre-2.5: Live validation checklist

Run these four tests (in order) against a real LLM before writing any 2.5 code. They validate that the Phase 2 stack is solid. Use VS Code LM (Copilot) or any OpenAI-compatible key; set up a profile + topology template in the Agent Config panel and hit Quick Spawn.

### Test 1 — Orchestrator creates tasks

Expected loop: `workspace.summary` → `task.create` (× N) → `end_turn`

Pass: dashboard shows ≥ 1 task under the work unit.

### Test 2 — Orchestrator spawns worker

Expected loop: `task.create` → `agent.spawn` (with injected credentials + provider) → `end_turn`

Pass: a second agent appears in the agent list; credentials propagated correctly (check host log).

### Test 3 — Worker completes cleanly

Expected loop: `task.update(InProgress)` → work iterations → `merge.propose` → `merge.validate` → `end_turn`

Pass: Merge Review panel shows a proposal; no loop timeout.

### Test 4 — Process restart + resume

Sequence: Quick Spawn → wait for tasks to appear → kill host → restart host → confirm work unit / tasks still visible.

Pass: state survives restart (AP-1 / AP-5 validation). If this fails, there's a DAG storage issue to fix before 2.5.

---

## What Phase 2.5 adds

| Today | After 2.5 |
|-------|-----------|
| `agentType = "orchestrator" / "worker"` — routing key only | `AgentProfile` record in the DAG with role, systemPrompt, allowedTools, maxIterations |
| System prompts hardcoded in loop classes | System prompt loaded from profile at spawn; defaults unchanged if no profile |
| Loops can call all 32 MCP tools | `allowedTools` in profile filters the tool list the LLM sees (and the dispatcher enforces) |
| Context assembled ad hoc in seed message | `AgentWorkspaceProjection` — a single structured context document injected at turn 0 |
| Orchestrator spawns `agentType="worker"` | Orchestrator spawns `profileId=<implementer>` via rule-based selector |

---

## Concept: two kinds of "profile"

The VS Code extension already has `AgentProfile` (in `AgentConfigService.ts`) which stores **LLM config**: provider, model, baseUrl, apiKey. Those stay in extension settings/secrets — credentials never touch the DAG.

Phase 2.5 adds a **server-side `AgentProfile`** that stores **behavioral config**: role, system prompt, allowed tools, iteration limits. This lives in the DAG (AP-1). The two concepts are related but separate: at spawn time, the extension resolves LLM config and the host resolves behavioral config; both feed into the running loop.

---

## Slice 9a — AgentProfile DAG entity

**Scope:** Add `AgentProfile` as a first-class domain record stored in the DAG. No loop changes yet — just the data model, service, and CRUD endpoints. This lets you create and manage profiles before wiring them to agents.

### New domain record

**`src/NodalMerge.Studio.Contracts/Domain/AgentProfile.cs`** (new)

```csharp
namespace NodalMerge.Studio.Contracts.Domain;

public enum AgentRole
{
    General,
    Orchestrator,
    Planner,
    Implementer,
    Reviewer,
}

public sealed record AgentProfile(
    string AgentProfileId,
    string Name,
    AgentRole Role,
    string SystemPrompt,
    IReadOnlyList<string> AllowedTools,   // empty = all nm.v1.* tools permitted
    int MaxIterations,
    IReadOnlyList<string> Capabilities);  // semantic tags: "code", "docs", "testing"
```

### Storage

**`src/NodalMerge.Studio.Storage/StudioNodeKind.cs`**
- Add `public const string AgentProfile = "studio/agent-profile/v1";`

**`src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`**
- Add `IAgentProfileService`:

```csharp
public interface IAgentProfileService
{
    Task<AgentProfile> CreateAsync(AgentProfile profile, CancellationToken ct = default);
    Task<AgentProfile?> GetAsync(string profileId, CancellationToken ct = default);
    Task<AgentProfile> UpdateAsync(AgentProfile profile, CancellationToken ct = default);
    Task<IReadOnlyList<AgentProfile>> ListAsync(CancellationToken ct = default);
}
```

**`src/NodalMerge.Studio.AgentRuntime/InMemoryAgentProfileService.cs`** (new)
- `ConcurrentDictionary<string, AgentProfile>` — used by integration tests.

**`src/NodalMerge.Studio.Storage/NodalMergeAgentProfileService.cs`** (new)
- Real implementation backed by `IStudioNodeStore` at `StudioNodeKind.AgentProfile`.
- Seeded with four default profiles on first start if store is empty (see below).

### Default profile seed

On host start, if no profiles exist, seed:

| ProfileId | Name | Role | AllowedTools | MaxIterations |
|-----------|------|------|--------------|---------------|
| `orchestrator` | Orchestrator | Orchestrator | *(all)* | 25 |
| `planner` | Planner | Planner | task.create, task.list, workunit.get, workspace.summary | 15 |
| `implementer` | Implementer | Implementer | task.update, task.list, task.assign, merge.propose, merge.validate, branch.create, branch.status, snapshot.get | 20 |
| `reviewer` | Reviewer | Reviewer | merge.validate, merge.review, projection.get, snapshot.compare | 10 |

These replace the hardcoded "orchestrator" / "worker" agentType routing.

### REST endpoints

**`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`**
- `GET /studio/agent-profiles` → `IAgentProfileService.ListAsync`
- `GET /studio/agent-profiles/{id}` → `IAgentProfileService.GetAsync`
- `POST /studio/agent-profiles` → `IAgentProfileService.CreateAsync`
- `PUT /studio/agent-profiles/{id}` → `IAgentProfileService.UpdateAsync`

### Extension panel

**`clients/vscode-extension/src/panels/AgentConfigPanel.ts`**
- Add "Behavior Profiles" tab (alongside Profiles, Templates, Quick Spawn).
- Table: ProfileId | Name | Role | AllowedTools count | MaxIterations | Edit
- Form: editable Name, Role (dropdown), SystemPrompt (textarea), AllowedTools (multi-select or comma-separated), MaxIterations (number), Capabilities (comma-separated tags).
- Backed by REST — reads from `/studio/agent-profiles`, writes via POST/PUT.
- Quick Spawn: profile selector now shows behavior profiles (not just LLM config profiles); user picks one, extension maps it to the correct topology.

### Success criteria
- Four default behavior profiles appear in the Behavior Profiles tab on first launch.
- Create/edit a profile; restart host; profile persists.
- `GET /studio/agent-profiles` returns the list.
- All 82 existing tests still pass (in-memory path unchanged).

---

## Slice 9b — Profile-driven loop configuration

**Scope:** Loops load system prompt, maxIterations, and allowedTools from a resolved `AgentProfile` at spawn time. The hardcoded defaults remain as fallbacks if no profile is specified.

### Spawn signature extension

**`src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`**
- `IAgentControlService.SpawnAsync` gains `string? profileId = null` before the CancellationToken.

**`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`**
- `SpawnAgentBody` gains `string? ProfileId = null`; passed through to `SpawnAsync`.

**`src/NodalMerge.Studio.McpServer/Tools/AgentTools.cs`**
- `nm.v1.agent.spawn` tool definition gains optional `profileId` parameter.
- `AgentSpawnAsync` passes `Str(input, "profileId")` to `agentControl.SpawnAsync`.

**`src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs`**
- `AgentSpawnAsync` call site: pass `Str(input, "profileId")` to service.

### AgentRecord

**`src/NodalMerge.Studio.AgentRuntime/InMemoryAgentRuntimeService.cs`**
- `AgentRecord` gains `string? ProfileId` and resolved `AgentProfile? Profile`.
- `SpawnAsync`: if `profileId` is provided, call `IAgentProfileService.GetAsync(profileId)` and store the snapshot in the record.

### Loop constructors

**`src/NodalMerge.Studio.AgentRuntime/OrchestratorAgentLoop.cs`**
- Constructor accepts `AgentProfile? profile`.
- System prompt: `profile?.SystemPrompt ?? OrchestratorSystemPrompt.Default`
- Max iterations: `profile?.MaxIterations ?? 25`
- Tool list passed to `LlmClient.SendAsync`: filtered to `profile.AllowedTools` if non-empty; full list otherwise.
- `InjectSpawnCredentials`: also injects `"profileId"` when orchestrator knows which profile to give a worker (resolved by selector in 9d; for now injects the same orchestrator profileId as a passthrough).

**`src/NodalMerge.Studio.AgentRuntime/WorkerAgentLoop.cs`**
- Same pattern: `AgentProfile?` in constructor.

### Tool enforcement in dispatcher

**`src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs`**
- Constructor gains `IReadOnlyList<string>? allowedTools = null`.
- `DispatchAsync`: if `_allowedTools` is non-empty and `toolName` is not in the set, return `ToError($"Tool '{toolName}' is not permitted for this agent's profile.")`.
- This is a safety net; the LLM tool list is already filtered at loop construction.

### Success criteria
- Spawn an orchestrator with `profileId=orchestrator`; loop uses that profile's system prompt (verify via log).
- Spawn a `reviewer` profile; attempt to call `nm.v1.task.create` from it; dispatcher returns a permission error.
- No change to integration tests — they pass null profileId and get default behavior.

---

## Slice 9c — AgentWorkspaceProjection

**Scope:** Replace ad hoc context assembly in loop seed messages with a single structured projection that any agent can call on any turn. This standardizes what context agents see and makes it tunable without changing loop code.

### New projection type

**`src/NodalMerge.Studio.Contracts/Projections/ProjectionContracts.cs`**
- Add `AgentWorkspace` to `ProjectionType` enum.
- Add payload record:

```csharp
public sealed record AgentWorkspaceProjectionPayload(
    string AgentId,
    string AgentRole,
    WorkUnitProjectionPayload WorkUnit,
    IReadOnlyList<StudioTask> AssignedTasks,
    IReadOnlyList<string> PendingProposals,
    IReadOnlyList<string> AvailableTools,      // profile.AllowedTools or all tools
    IReadOnlyList<string> Capabilities);
```

### Projection materialization

**`src/NodalMerge.Studio.Projections/ProjectionManager.cs`**
- Handle `ProjectionType.AgentWorkspace` in `GetAsync`.
- Assemble from: `IWorkUnitService.GetAsync(request.WorkUnitId)`, `ITaskService.ListAsync(request.WorkUnitId)`, `IMergeService.ListAsync()` (filtered to pending), available tools from profile (passed via a new `GetAgentWorkspaceAsync` overload or resolved from DI-injected `IAgentProfileService`).
- `ProjectionRequest.AgentId` (already on the record) identifies which agent's context to build.

### Loop changes

**`src/NodalMerge.Studio.AgentRuntime/OrchestratorAgentLoop.cs`**
- Turn 0 seed message: instead of `"Begin orchestrating work unit {workUnitId}..."`, call `projectionManager.GetAsync(new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal, workUnitId, AgentId: agentId))` and serialize the result into the seed message.
- This gives the orchestrator a complete picture upfront: goal, existing tasks, pending proposals, available tools.

**`src/NodalMerge.Studio.AgentRuntime/WorkerAgentLoop.cs`**
- Same: seed with `AgentWorkspaceProjection` so worker sees its assigned tasks immediately.

### MCP tool change

`nm.v1.projection.get` already accepts `ProjectionType` and `agentId` in `ProjectionRequest`. No signature change needed — just the new enum value handled in `ProjectionManager`.

### Success criteria
- Orchestrator's first LLM message includes structured JSON with workUnit goal, task list, and tool names (visible in host log).
- Worker's first message includes the task assigned to it.
- `nm.v1.projection.get` with `type: "AgentWorkspace"` returns the payload via MCP.

---

## Slice 9d — Rule-based profile selection

**Scope:** Orchestrator selects a worker profile by role/capabilities rather than hardcoding `agentType="worker"`. This is the minimal step that makes Phase 3 (multiple specialized workers) compositional instead of ad hoc.

### Selector interface

**`src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`**

```csharp
public interface IAgentProfileSelectorService
{
    Task<AgentProfile?> SelectAsync(
        AgentRole role,
        IReadOnlyList<string>? requiredCapabilities = null,
        CancellationToken ct = default);
}
```

### Rule-based implementation

**`src/NodalMerge.Studio.AgentRuntime/RuleBasedAgentProfileSelector.cs`** (new)

Selection order:
1. Find profiles with `Role == role` whose `Capabilities` are a superset of `requiredCapabilities`.
2. If none, find any profile with `Role == role`.
3. Fallback: `Role == AgentRole.General`.

For Phase 2.5 this is enough. Phase 3 can replace or augment with LLM-driven selection.

### Orchestrator integration

**`src/NodalMerge.Studio.AgentRuntime/OrchestratorAgentLoop.cs`**
- Inject `IAgentProfileSelectorService`.
- When orchestrator calls `nm.v1.agent.spawn`, apply selector before injecting credentials: `var workerProfile = await selector.SelectAsync(AgentRole.Implementer)`.
- `InjectSpawnCredentials` injects `"profileId": workerProfile.AgentProfileId` alongside model/baseUrl/apiKey/provider.

### Topology templates in extension

**`clients/vscode-extension/src/AgentConfigService.ts`**
- `TopologyTemplate.workers[].profile` already holds a string ID; rename to `behaviorProfileId` to distinguish it from the LLM config profile.
- Quick Spawn: send `profileId` in the spawn body using the topology template's `behaviorProfileId`.

### Success criteria
- Create a topology template that references `implementer` as the worker behavior profile.
- Quick Spawn: orchestrator spawns, then spawns a worker with `profileId=implementer`; worker's tool list is restricted to implementer's `allowedTools`; confirm via dispatcher log.
- Changing the topology to `reviewer` profile routes to a different set of tools without code changes.

---

## Slice ordering rationale

9a → 9b → 9c → 9d

- **9a first** because every subsequent slice depends on `AgentProfile` existing as a domain entity.
- **9b before 9c** because the loop constructors need profile config before the projection can reference `AvailableTools`.
- **9c before 9d** because the workspace projection is what makes the orchestrator's selection decision visible to the worker on turn 0 — the worker needs the context to know what it's supposed to do.
- **9d last** because profile selection is only useful once the profiles are wired into the loops.

---

## Files not touched in 2.5

| File | Reason |
|------|--------|
| `McpToolNames.cs` | No new MCP tools needed — profiles are admin-managed via REST, not queried by agents |
| `LlmClient.cs` | Provider abstraction is complete; no changes |
| `mcp-v1-contract.md` | `nm.v1.agent.spawn` gains optional `profileId` — additive, backward compatible |
| Integration tests | Pass `null` for `profileId`; default behavior unchanged |

---

## Phase 3 pointer

After 9d, spawning five specialized workers becomes:

```
OrchestratorAgentLoop
  → selector.SelectAsync(AgentRole.Implementer, ["code"])   → profileId=implementer
  → nm.v1.agent.spawn × N (each with profileId=implementer, isolated branch)
  → selector.SelectAsync(AgentRole.Reviewer, ["code"])       → profileId=reviewer
  → nm.v1.agent.spawn (reviewer, waits on all implementer merges)
```

Phase 3 concerns: branch isolation per worker, merge fan-in strategy, streaming agent status to the DAG replay panel, failure recovery (dead letter → human escalation). None of those require changes to the profile or projection system.
