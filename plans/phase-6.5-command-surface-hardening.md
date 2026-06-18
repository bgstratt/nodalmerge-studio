# Phase 6.5 — Command Surface Hardening

MCP is disabled outright on at least one enterprise dev machine in active use on this project, and
likely on others — a third-party policy toggle this project has zero control over. Today, MCP tool
classes in `NodalMerge.Studio.McpServer/Tools/` are not just *a* transport, in places they are
materially different in behavior from the other two paths into the same backend:

* `StudioRestEndpoints.cs` (REST, already the VS Code extension's only transport — proven to work
  everywhere the extension does)
* `McpToolDispatcher.cs` (in-process dispatch used only by the agent loops themselves —
  `OrchestratorAgentLoop`/`WorkerAgentLoop`/`PlannerAgentLoop`/`ReviewerAgentLoop` — never reachable
  from outside the process)

The three have drifted. Concretely:

| Command | MCP tool | REST | Dispatcher (agent-internal) |
|---|---|---|---|
| `workunit.create` | no `parentWorkUnitId`/`dependsOn`/`fileScope` | has all three | no `parentWorkUnitId`/`dependsOn`/`fileScope` |
| `task.create` | no artifact-lineage record | endpoint doesn't exist | **records artifact lineage** |
| `task.update` / `task.assign` | exists | endpoint doesn't exist | exists |
| `merge.propose` | no diff, no lineage, no event, no status transition | no diff, no lineage, no event, no status transition | **does all four** |
| `agent.spawn` | no `autoReviewProfileId` | has it | no `autoReviewProfileId` |
| `agent.status` | exists | endpoint doesn't exist | exists |
| `scheduler.enqueue` | has `model`/`baseUrl`/`apiKey`/`provider` | drops them | has them |
| `artifact.record/query/list` | exists | endpoint doesn't exist | exists |
| `branch.checkout` / `branch.status` | exists | endpoint doesn't exist | exists |

This phase converges all externally-reachable transports (MCP, REST) onto one shared implementation
per command, sourced from whichever of the three existing implementations is most complete today
(usually the dispatcher's, since it's what real agent runs have exercised). REST becomes the
practical fallback when MCP is unavailable, with true feature parity rather than "REST exists but is
missing things." A generic `ICommandBus`/CLI transport is explicitly **not** part of this phase —
revisit only if/when a CLI is actually built (see Phase 7 pointer addition below). Workspace
file-op tools (`workspace.read/write/delete/list/diff/exists`) and `intent.record` are also out of
scope: they're invoked only by an agent's own in-loop tool-calling, never by an external MCP/REST/CLI
caller, so there's no fallback gap to close.

Each command group below gets a shared `*CommandService` (new interface in
`NodalMerge.Studio.Core/Services/ServiceContracts.cs`, implementation living alongside the domain
logic it composes — `Orchestrator`/`Merge` projects, not a new project). MCP tool classes, REST
lambda bodies, and `McpToolDispatcher` all become thin adapters calling the same service.

---

## Slice 15a — Branch & State parity (template slice, no new abstraction)

Establishes that "parity gap" doesn't always mean "extract a shared service" — sometimes the
service call underneath is already identical and only the route is missing.

* Add `POST /studio/branches/{branchId}/checkout` and `GET /studio/branches/{branchId}/status` to
  `StudioRestEndpoints.MapBranchEndpoints` — both are direct passthroughs to `IBranchService`,
  matching `BranchTools.CheckoutAsync`/`StatusAsync` exactly.
* Audit `MapStateEndpoints` against `StateTools.cs` — already at parity (mark/find/checkout all
  present); no code change, just a regression test that locks this in.

**Files:** `src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`,
`tests/NodalMerge.Studio.Integration.Tests/`

**Success criteria:** REST and MCP produce identical results for all four branch operations against
the same fixture branch.

---

## Slice 15b — Work unit command consolidation

* New `IWorkUnitCommandService.CreateAsync(WorkUnitCreateCommand)` in
  `NodalMerge.Studio.Core/Services/ServiceContracts.cs`, covering the union of params: `goal`,
  `owner`, `branchId`, `successCriteria`, `parentWorkUnitId`, `dependsOn`, `fileScope`,
  `repositoryPath`. Implementation in `NodalMerge.Studio.Orchestrator` wrapping
  `IOrchestratorService.CreateWorkUnitAsync` + the `BranchId` override currently duplicated in both
  `WorkUnitTools.CreateAsync` and `McpToolDispatcher.WorkUnitCreateAsync`.
* `WorkUnitTools.CreateAsync` (MCP), `StudioRestEndpoints` `POST /studio/workunits`, and
  `McpToolDispatcher.WorkUnitCreateAsync` all call it.
* Closes gap: MCP and the agent-loop dispatcher gain `parentWorkUnitId`/`dependsOn`/`fileScope`
  support they're currently missing relative to REST.

**Files:** `src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`,
`src/NodalMerge.Studio.Orchestrator/`, `src/NodalMerge.Studio.McpServer/Tools/WorkUnitTools.cs`,
`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`,
`src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs`

---

## Slice 15c — Task command consolidation

* New `ITaskCommandService` with `CreateAsync` (folds in the artifact-lineage record currently only
  written by `McpToolDispatcher.TaskCreateAsync`, lines 130-138), `UpdateAsync`, `AssignAsync`,
  `ListAsync`.
* Add missing REST endpoints: `POST /studio/tasks`, `PUT /studio/tasks/{taskId}`,
  `POST /studio/tasks/{taskId}/assign` (REST today is read-only — `GET` list/get only).
* All three transports route through the shared service; lineage recording on task creation becomes
  universal instead of agent-loop-only.

**Files:** `src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`,
`src/NodalMerge.Studio.Orchestrator/`, `src/NodalMerge.Studio.McpServer/Tools/TaskTools.cs`,
`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`,
`src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs`

---

## Slice 15d — Merge command consolidation (highest value, highest risk)

The one slice in this phase with an observable behavior change for existing callers, not just
plumbing — needs the most test coverage.

* Extract `McpToolDispatcher.MergeProposeAsync`'s full logic (diff generation via
  `IFileWorkspaceService.DiffAsync`, `ParseFilesTouched`, fallback file listing, artifact lineage
  record, `ExecutionEventKind.ArtifactProposed` append, best-effort `WorkUnitStatus.Proposed`
  transition) into `IMergeCommandService.ProposeAsync`, parameterized so it still works when
  `workUnitId`/`sessionId` are absent (the REST/external-MCP case today).
  - This brings the description text for `nm_v1_merge_propose` in `MergeTools.cs:13` (currently
    described as a thin "submit a proposal" call) and `docs/contracts/mcp-v1-contract.md`'s example
    response in line with what it will now actually do — update the tool description.
* `MergeTools.ProposeAsync` (MCP) and `StudioRestEndpoints` `POST /studio/merges` both switch to
  the shared service, including its idempotency-key handling (currently two near-identical but
  separately-implemented `commandId`/`X-Command-Id` cache checks — consolidate into one).
* `Validate`/`Review`/`Apply` are already near-identical across all three paths — light wrapping,
  low risk; do these as part of the same slice since the service class is already open.

**Files:** `src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`, `src/NodalMerge.Studio.Merge/`,
`src/NodalMerge.Studio.McpServer/Tools/MergeTools.cs`,
`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`,
`src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs`, `docs/contracts/mcp-v1-contract.md`

**Success criteria:** a merge proposal created via REST or external MCP has the same
`workspaceChanges`/`filesTouched`/artifact lineage entry/execution event/status transition as one
created by an in-process agent run, proven by an integration test that proposes via each of the
three paths against the same fixture branch and diffs the resulting `MergeProposal` records.

---

## Slice 15e — Agent command consolidation

* `IAgentCommandService.SpawnAsync` taking the full param set REST already exposes
  (`profileId`, `autoReviewProfileId`) — MCP tool and dispatcher currently drop
  `autoReviewProfileId` entirely.
* Add `GET /studio/agents/{agentId}/status` to REST (list/spawn/pause/resume/stop exist; no
  single-agent status read, unlike MCP/dispatcher).

**Files:** `src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`,
`src/NodalMerge.Studio.McpServer/Tools/AgentTools.cs`,
`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`,
`src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs`

---

## Slice 15f — Scheduler & Artifact command consolidation

* `ISchedulerCommandService.EnqueueAsync` with the full param set (`model`/`baseUrl`/`apiKey`/
  `provider`) that MCP and the dispatcher already pass but REST's `EnqueueBody` drops.
* `IArtifactCommandService` (`Record`/`Query`/`List`, including the shared
  `CollectChainWithAncestorsAsync` walk currently copy-pasted verbatim between `ArtifactTools.cs`
  and `McpToolDispatcher.cs`) — and add REST endpoints for all three; REST today only exposes
  read-only lineage (`GetAsync`/`GetChildrenAsync`), with no route at all for knowledge artifacts.

**Files:** `src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`, `src/NodalMerge.Studio.Merge/`,
`src/NodalMerge.Studio.McpServer/Tools/SchedulerTools.cs`,
`src/NodalMerge.Studio.McpServer/Tools/ArtifactTools.cs`,
`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`,
`src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs`

---

## Explicitly deferred from this phase

* **Projection/Replay/Snapshot REST parity** (`nm_v1_projection_*`, `nm_v1_replay_*`,
  `nm_v1_snapshot_*`) — these are read-only introspection tools with no REST route today. Lower
  urgency than 15a-15f because they're not state-changing and `workspace-summary` (already on REST)
  covers most of the same ground for the dashboard use case. Revisit if an external MCP-disabled
  caller is found to actually need raw projection/replay/snapshot access, not preemptively.
* **`ICommandBus` / CLI transport** — the shared `*CommandService` classes from 15a-15f are the
  substrate a CLI would call into; build the actual CLI process and a generic dispatch abstraction
  only once there's a concrete reason (e.g. REST itself turns out to be blocked by something, such
  as local-port policy, which hasn't been observed yet).
* **`mcp-v1-contract.md` full refresh** — the doc is stale beyond the dot/underscore naming (missing
  `scheduler.*`, `intent.*`, `artifact.*`, `workspace.*` entirely). Worth a pass once 15a-15f settle
  the actual shape of each command's request/response, so the doc isn't rewritten twice.

---

## Verification checklist (whole phase)

* [ ] Every command group has exactly one implementation of its business logic; MCP tool / REST
  lambda / dispatcher case are each ≤ a few lines of param marshaling calling the shared service.
* [ ] Integration test per consolidated command proving REST and MCP produce identical results
  against the same fixture state (extend `tests/NodalMerge.Studio.Integration.Tests/`).
* [ ] `merge.propose` via REST now produces a `MergeProposal` with `WorkspaceChanges`/
  `FilesTouched`/lineage/event-stream entries populated — previously REST/external-MCP callers got
  none of these.
* [ ] No regression in existing agent-loop integration tests (`McpToolDispatcher` behavior is now
  delegated, not reimplemented — same observable output for in-process callers).
