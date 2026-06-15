# Phase 2 — Real Execution

This document captures everything that must happen for one orchestrator + one worker to actually run: calling an LLM, executing work, proposing a merge, and completing a human-reviewed cycle. Phase 1 (slices 0–7g) is all scaffolding. Phase 2 makes it live.

---

## Stub inventory

Everything currently in memory, with no real effect:

| Component | File | What it does today | What it needs to do |
|-----------|------|--------------------|---------------------|
| `InMemoryStudioNodeStore` | `Storage/StudioNodeStore.cs` | `ConcurrentDictionary<(kind,id), json>` — write-through lands here, not in the DAG | Write/read real NodalMerge DAG nodes |
| `InMemoryBranchService` | `Storage/InMemoryBranchService.cs` | Dict of branch names | Create actual DAG branches via `NodalMerge.Host.Abstractions` |
| `InMemoryAgentRuntimeService.SpawnAsync` | `AgentRuntime/InMemoryAgentRuntimeService.cs:66` | Generates GUID, inserts `AgentRecord` in dict | Start a real agent task loop (background `Task`) |
| `InMemoryAgentRuntimeService.PauseAsync/StopAsync` | same | Flips a string | Cancel/pause the running loop's `CancellationTokenSource` |
| `InMemoryAgentRuntimeService.CompareAsync` | same | Returns `"[]"` | Diff two agent branches via DAG |
| `InMemoryWorkUnitService` | `Orchestrator/` | ConcurrentDict + write-through | Write-through already wired; needs real storage behind it |
| `InMemoryTaskService` | `Tasks/` | Same | Same |
| `InMemoryMergeService` | `Merge/` | Same | Same |
| `InMemoryKnownGoodStateService` | `Storage/InMemoryKnownGoodStateService.cs` | Same | Same |
| `AgentProfile.modelHint` | `vscode-extension/src/AgentConfigService.ts` | Free-text label, ignored at runtime | Real model ID forwarded to the agent loop |
| `SpawnAgentBody` | `Host/StudioRestEndpoints.cs:18` | `AgentType + WorkUnitId` only | Add `model`, `baseUrl`, `apiKey` |

Everything state-related is lost on restart. No LLM is ever called. No agent ever does work.

---

## Architectural decision: where does the agent loop run?

**Decision: server-side, inside the .NET host process.**

Rationale:
- AP-1 requires all authoritative state in NodalMerge — the loop must be co-located with the service layer to use DI directly (no serialization round-trips for every tool call)
- MCP tool implementations are already in-process; the agent calls them as method calls, not HTTP
- The API key is short-lived: sent from VS Code on spawn, held in memory for the agent's lifetime, never persisted
- PauseAsync/StopAsync become `CancellationTokenSource` operations on the running task

The agent loop uses a standard agentic pattern: build a message history → call LLM with tool descriptions → on `tool_use` → execute the named MCP tool → append result → repeat until `end_turn` or stop signal.

---

## Slice 8a — LLM API config in agent profiles

**Scope:** Wire the API key, model, and base URL through from VS Code secrets into the spawn call. No actual LLM call yet.

### Files touched

**`vscode-extension/src/AgentConfigService.ts`**
- `AgentProfile` gains: `model: string`, `baseUrl?: string`, `apiKeyRef?: string`
- `AgentConfigService.resolveApiKey(profile, secrets: vscode.SecretStorage): Promise<string | undefined>` — reads from `secrets.get(profile.apiKeyRef)`
- `AgentConfigService.storeApiKey(profile, key, secrets): Promise<void>` — calls `secrets.store()`

**`vscode-extension/src/panels/AgentConfigPanel.ts`**
- Profile form gains: model field (text input), baseUrl field (text input), "Set API Key" button
- "Set API Key" button sends `{ type: 'setApiKey', profileId, key }` → extension calls `secrets.store()`
- Profile table shows model next to label; never shows the key

**`vscode-extension/src/extension.ts`**
- Pass `context.secrets` into `AgentConfigPanel.createOrShow()`
- `AgentConfigPanel` message handler for `setApiKey` calls `agentConfig.storeApiKey()`

**`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`**
- `SpawnAgentBody` gains: `string? Model`, `string? BaseUrl`, `string? ApiKey`
- Pass all three through to `IAgentControlService.SpawnAsync()`

**`src/NodalMerge.Studio.Core/Services/IAgentControlService.cs`**
- `SpawnAsync(string agentType, string workUnitId, string? model, string? baseUrl, string? apiKey, CancellationToken ct)`

**`src/NodalMerge.Studio.AgentRuntime/InMemoryAgentRuntimeService.cs`**
- `AgentRecord` gains `Model`, `BaseUrl`, `ApiKey` fields
- `SpawnAsync` stores them (no loop yet)

### Success criteria
- VS Code: profile form has model/baseUrl/key fields; key stored in secrets, never in `settings.json`
- POST `/studio/agents/spawn` with `{ agentType, workUnitId, model, baseUrl, apiKey }` → 200 with `agentId`
- GET `/studio/agents` reflects the new agent

---

## Slice 8b — Real DAG storage

**Scope:** Replace `InMemoryStudioNodeStore` and `InMemoryBranchService` with implementations backed by the actual NodalMerge DAG. State survives restarts. This unlocks AP-1 and AP-5.

### Files touched

**`src/NodalMerge.Studio.Storage/NodalMergeStudioNodeStore.cs`** (new)
- Implement `IStudioNodeStore` against `NodalMerge.Host.Abstractions` node write/read APIs
- `WriteNodeAsync` → calls `INodalMergeHost.AppendNode(kind, entityId, payloadBytes)` (or equivalent)
- `ReadNodeAsync` → queries the DAG for the latest node matching `(kind, entityId)`

**`src/NodalMerge.Studio.Storage/NodalMergeBranchService.cs`** (new)
- Implement `IBranchService` against the real DAG branch APIs
- `CreateBranchAsync` → `INodalMergeHost.CreateBranch(name, fromBranchId?)`
- `ListBranchesAsync`, `GetStatusAsync` → real DAG queries

**`src/NodalMerge.Studio.Storage/ServiceCollectionExtensions.cs`**
- Add `AddNodalMergeStorage()` extension that registers `NodalMergeStudioNodeStore` and `NodalMergeBranchService`
- Keep `AddInMemoryStorage()` for test fixtures (tests keep using `InMemoryStudioNodeStore`)

**`src/NodalMerge.Studio.Host/`**
- Switch DI registration from in-memory to real store in `Program.cs` / `StudioServiceCollectionExtensions.cs`
- Wire with `NodalMergeUseNuGetPackages` flag already understood by the project

### Success criteria
- Work units / tasks / merge proposals survive a host restart
- Branches visible in the DAG viewer match what the service reports
- All 81 existing tests still pass (they use in-memory store directly)

---

## Slice 8c — Orchestrator agent loop

**Scope:** `SpawnAsync` for an orchestrator-type agent starts a real background task that calls an LLM. The orchestrator reads the work unit's goal and breaks it into tasks using `nm.v1.task.create`.

### Architecture

```
SpawnAsync(agentType="orchestrator", workUnitId, model, baseUrl, apiKey)
  → starts Task.Run(OrchestratorLoop(workUnitId, model, baseUrl, apiKey, cts.Token))
  → returns agentId immediately
  
OrchestratorLoop:
  1. Build system prompt (orchestrator persona + nm.v1.* tool list)
  2. Seed message: "Work unit goal: <goal>"
  3. loop:
       response = LlmClient.ChatAsync(messages, tools)
       if response.StopReason == "end_turn" → break
       for each tool_use block:
         result = McpToolDispatcher.Execute(toolName, toolInput)
         append tool_result to messages
  4. On exit: UpdateStatusAsync(workUnitId, Completed/Failed)
```

### Files touched

**`src/NodalMerge.Studio.AgentRuntime/LlmClient.cs`** (new)
- Thin wrapper for Anthropic API (or OpenAI-compatible via `baseUrl`)
- Sends `POST /v1/messages` with `model`, `system`, `tools`, `messages[]`
- Returns structured `LlmResponse { StopReason, Content[] }`
- HTTP-only — no SDK dependency needed for v1; raw `HttpClient` + `System.Text.Json`

**`src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs`** (new)
- Holds a map of `nm.v1.*` tool name → `Func<JsonElement, CancellationToken, Task<object>>`
- Registered at startup by injecting all `IMcpTool` implementations from `NodalMerge.Studio.McpServer`
- `Execute(toolName, inputJson)` → dispatch + serialize result as `JsonElement` for `tool_result`

**`src/NodalMerge.Studio.AgentRuntime/OrchestratorAgentLoop.cs`** (new)
- `RunAsync(workUnitId, goal, llmClient, dispatcher, workUnits, ct)`
- System prompt: orchestrator persona, available tools summary
- Tool call handling: delegates to `dispatcher`

**`src/NodalMerge.Studio.AgentRuntime/InMemoryAgentRuntimeService.cs`**
- `AgentRecord` gains `CancellationTokenSource Cts`
- `SpawnAsync`: if `agentType == "orchestrator"` → start `OrchestratorAgentLoop.RunAsync` as `Task.Run`
- `PauseAsync` / `StopAsync` → `Cts.Cancel()`; pause re-creates a new CTS on resume
- `GetStatusAsync` → check if task is running/faulted/completed

**`src/NodalMerge.Studio.AgentRuntime/ServiceCollectionExtensions.cs`**
- Inject `McpToolDispatcher` as singleton

### Success criteria
- POST `/studio/agents/spawn` with orchestrator type + real API key
- Orchestrator calls LLM, LLM calls `nm.v1.task.create` at least once
- Dashboard shows new task(s) created by the orchestrator
- Stop endpoint cancels the loop without crashing the host

---

## Slice 8d — Worker agent loop

**Scope:** Orchestrator spawns a worker (via `nm.v1.agent.spawn` MCP tool). Worker reads its assigned task, does work, and creates a merge proposal. Merge proposal goes through the existing AP-4 human gate in the Merge Review panel.

### Files touched

**`src/NodalMerge.Studio.AgentRuntime/WorkerAgentLoop.cs`** (new)
- `RunAsync(workUnitId, taskId, llmClient, dispatcher, tasks, mergeService, ct)`
- System prompt: worker persona — "execute the task, produce changes, propose a merge when done"
- Worker calls `nm.v1.task.update` (InProgress), does LLM-driven work iterations, calls `nm.v1.merge.propose` when complete

**`src/NodalMerge.Studio.AgentRuntime/InMemoryAgentRuntimeService.cs`**
- `SpawnAsync`: dispatch `"worker"` type to `WorkerAgentLoop`
- Worker needs to know its task assignment — either pass `taskId` in spawn metadata or worker calls `nm.v1.task.list` and picks its own

**`src/NodalMerge.Studio.McpServer/Tools/`**
- Verify `nm.v1.agent.spawn` MCP tool forwards `model/baseUrl/apiKey` from the spawning agent's profile to the new agent — the orchestrator passes these through
- This is the mechanism by which the orchestrator delegates the same LLM config to workers

### Success criteria
- Full end-to-end: create work unit → orchestrator spawns → tasks created → worker spawns → work done → merge proposed
- Merge Review panel shows the proposal; human clicks Approve → merge applied
- All AP principles exercised: AP-1 (state in DAG), AP-2 (agents use projections), AP-3 (WorkUnit = goal + branch), AP-4 (human gate), AP-5 (append-only)

---

## Slice 8e — End-to-end integration test

**Scope:** An automated test (or repeatable manual script) that exercises the full loop without a human needed for every run. Uses a test API key and a mock merge reviewer.

### Approach
- `docker-compose` or test harness: start host, seed a work unit, call spawn, wait for merge proposal
- `ApplyAsync` called programmatically (bypassing the UI gate) in the test only
- Assert: DAG contains nodes for `WorkUnit`, `Task`, `MergeProposal` at each kind path
- Assert: projection reflects the completed work unit

### Files touched
- `tests/NodalMerge.Studio.Integration.Tests/` (new project)
- `docker-compose.yml` or `run-integration.ps1` script at repo root

---

## Phase 3 — Multi-agent / Multi-LLM (future)

After one orchestrator + one worker reliably completes a cycle:

| Capability | What changes |
|------------|-------------|
| Multiple workers | Orchestrator calls `nm.v1.agent.spawn` N times; each worker gets an isolated branch via `NodalMergeBranchService.CreateBranchAsync` |
| Per-worker LLM routing | Each worker agent profile has its own `model`/`baseUrl`/`apiKeyRef` — orchestrator selects profile by domain when spawning |
| Branch isolation | Workers write to their own branch; NodalMerge peer replication merges results (AP-5: append-only; conflicts surface as merge proposals) |
| Failure recovery | Dead letter queue: agents that fault post a `FailedWorkUnit` node to the DAG; orchestrator picks it up and retries or routes to human |
| Streaming status | WebSocket `/ws/runtime` already exists (7d); push agent loop step events so the DAG replay panel animates in real time |
| Agent memory | Workers persist reasoning context as DAG nodes (a `studio/agent-memory/v1` kind); replay gives them continuity across sessions |

---

## Slice ordering rationale

8a → 8b → 8c → 8d → 8e

- **8a first** because the agent loop needs to know the model/key before anything fires
- **8b before 8c** because once the loop runs, you want state to survive restarts; debugging a live agent against ephemeral memory is painful
- **8c before 8d** because the orchestrator drives worker spawning; worker loop has no entry point until the orchestrator calls `nm.v1.agent.spawn`
- **8e last** because the integration test validates the full stack assembled in 8a–8d
