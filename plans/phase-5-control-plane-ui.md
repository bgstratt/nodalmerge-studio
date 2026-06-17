# Phase 5 — Control-Plane UI

Phase 4 delivers a correctly parallel, isolated, attributed pipeline. Phase 5 makes the durable artifact DAG visible, navigable, and steerable from the extension — directly answering the strategic question from [VISION.md](./VISION.md):

> "What can I do after an agent run completes?"

The answer after Phase 5: inspect every artifact, branch from any point, replay with a different model, compare competing proposals, audit the full decision log, steer work in progress.

---

## What Phase 5 adds

| Phase 4 | Phase 5 |
|---------|---------|
| Artifacts are durable in the DAG store | Artifacts are visible and navigable in the extension |
| Proposal DAG queryable via API | Proposal DAG rendered visually per work unit |
| "Branch from here" API exists (10f) | "Branch from here" is one click in the artifact explorer |
| Orchestration decision log in DAG store | Decision log rendered inline per work unit node |
| Stage state tracked per work unit | Stage badges stream in real-time to all panels |
| Orchestrator routes deterministically | Optional LLM-driven profile selection |

---

## Slice 12a — Artifact Explorer

The primary user surface of Phase 5. A new extension panel where every artifact in the DAG is visible, inspectable, and actionable. This panel is also the "homepage" the user described: paste a goal, see it decompose into a live work unit DAG as the orchestrator runs.

### Panel layout

**`clients/vscode-extension/src/panels/ArtifactExplorerPanel.ts`** (new)

Three zones:

**Left: Work Unit DAG**
- Scrollable tree/graph of work units for the active session (or a selected root work unit).
- Each node shows: goal (truncated), lifecycle status badge (Phase 4 states), current pipeline stage badge, agent assigned, proposal count.
- Real-time updates via `/ws/runtime` — no polling.
- Clicking a node selects it and populates the right zones.

**Center: Artifact Timeline**
- Vertical timeline of artifacts produced by the selected work unit:
  - Plan (link to plan text)
  - Tasks (collapsed list)
  - Branch changes (link to diff)
  - Proposals (each with status badge, "View diff", "Approve/Reject", "Branch from here", "Compare with...")
  - Orchestration events (collapsed log from 10e)
- Ordered by `OccurredAt` ascending.

**Right: Inspector**
- Context-sensitive detail pane:
  - Select a proposal: shows the diff (from 9a), origin, `filesTouched`, status, and available actions.
  - Select an orchestration event: shows input projection snapshot, action taken, spawned IDs, reason.
  - Select a work unit: shows full goal, file scope, dependencies, assigned agent, lifecycle state.

### Goal input

At the top of the panel: text area + "Run" button. Sends a new root work unit to the API (same as Quick Spawn). Work unit node appears immediately in the DAG and populates as the orchestrator produces artifacts.

### Three replay modes (surfaced here)

The Artifact Explorer is where all three replay semantics become user-facing. They must be treated as distinct actions — conflating them produces confusing UX:

**Event replay** — click the "Orchestration Log" section on any work unit node; walk the event sequence with timestamps, input projection snapshots, and decisions. No re-execution. Pure audit.

**Workspace replay** — right-click any proposal → "Restore workspace to this state"; calls `CheckoutProposalBaseAsync` (10f) and opens the resulting branch in the file diff view. No new agent run.

**Agent replay** — right-click any proposal → "Branch from here"; opens goal input modal; submits a new work unit with this proposal's base workspace as the starting point. A new agent run begins. Use case: rerun with a different model or profile and compare outputs.

### Manual plan revision

Right-click a work unit node:
- **Split** — opens a modal to decompose the node into two children with scope partitioning; creates child work units via the API.
- **Re-run** — re-enqueues the work unit via the scheduler (agent replay from the work unit's original start, not a proposal base).
- **Branch from proposal** — shortcut to agent replay from the most recent proposal's base.

### Success criteria
- Open Artifact Explorer with a completed multi-step run; full work unit DAG visible with artifact timeline.
- Click a proposal node; diff renders with +/- coloring (reusing 9a logic); "Branch from here" button present.
- Submit a new goal from the panel; work unit appears, stage badge updates in real-time as orchestrator runs.
- Click an orchestration event; input projection snapshot and action reason visible.

### As-built (12a)

- **`ArtifactExplorerPanel.ts`** (new) replaces the Slice 0 Home placeholder; follows the same `getFragment()`/`activate()`/`handleMessage()` view convention as the other 4 panels. Message types are prefixed `explorer*` so the shell's broadcast-to-all-views dispatch can't collide with `MergeReviewPanel`'s existing `openDiff`/`approve`/`reject` types.
- **Session picker is genuinely new wiring, not just UI.** `GET /studio/sessions` and `GET /studio/sessions/{id}/workunits` already existed (the latter's code comment explicitly anticipated "the Studio Shell's session picker" — Slice 0a), but nothing in the extension created or read an `ExecutionSession` before this slice. The goal-input "Run" flow is now the first place a session gets created: `POST /studio/workunits` → `POST /studio/sessions` → `POST /studio/agents/spawn`, mirroring `AgentConfigPanel`'s existing Quick Spawn sequence plus the new session-creation step.
- **No `/ws/runtime` yet (that's 12c)** — the DAG/session panes poll every 2s, same `POLL_INTERVAL_MS` convention as `WorkspaceDashboardPanel.ts`.
- **`CheckoutProposalBaseAsync` was never built** (referenced aspirationally in this doc and `VISION.md`, but doesn't exist in code). "Restore workspace" instead forks a durable branch from the already-existing `base/{proposalId}` snapshot via `IBranchService.CreateBranchAsync` (new endpoint: `POST /studio/merges/{proposalId}/restore-workspace`), then opens the proposal's already-cached before-content (`GET /studio/merges/{proposalId}/file-changes`) as read-only documents — no new file-read path needed.
- **Inspector delegates rather than duplicates.** Proposal diff/Approve/Reject still live only in `MergeReviewPanel`; the explorer's "Open in Merge Review →" button calls the existing `nodalmerge.openMergeReview` command. `MergeReviewPanel` itself gained two new action-bar buttons — **Branch from here** and **Restore workspace** — so both work from either surface without duplicating diff-rendering logic.
- **Split/Re-run/Branch-from-latest-proposal** use native `vscode.window.showInputBox`/`showQuickPick` prompts (consistent with `WorkspaceDashboardPanel`'s `createWorkUnit` flow), not a custom webview modal.
- Tests: `ProposalBranchingTests.cs` gained 3 cases covering the restore-workspace branch-fork semantics, including a regression guard for `FileSystemWorkspaceService`'s fixed `RootPath` (proposal/branch ids must be unique per test run, since `InitBranchAsync` no-ops once a branch directory exists on disk).

---

## Slice 12b — Projection Diffing

Orchestrator reasons incrementally — what changed since the last cycle — instead of re-reading the full state. Also enables stall detection: if no artifact changed after N cycles, escalate.

### Delta record

**`src/NodalMerge.Studio.Contracts/Projections/ProjectionContracts.cs`**

```csharp
public sealed record ProjectionDelta(
    string WorkUnitId,
    AgentWorkspaceProjectionPayload Previous,
    AgentWorkspaceProjectionPayload Current,
    IReadOnlyList<ArtifactRef> AddedArtifacts,      // new refs since last cycle
    IReadOnlyList<ArtifactRef> RemovedArtifacts,    // refs whose status changed to terminal
    IReadOnlyList<ArtifactRef> StatusChangedArtifacts, // same id, different status
    IReadOnlyList<string> CompletedTaskIds,
    bool AnyChange);                                 // false = stall; PlanChanged derivable from AddedArtifacts
```

`PlanChanged` is removed as a first-class field — it is derivable as `AddedArtifacts.Any(a => a.Type == ArtifactType.Plan)`. The delta is computed by diffing `Previous.Artifacts` and `Current.Artifacts` on `ArtifactId` and `Status`.

### Orchestrator loop

**`OrchestratorAgentLoop.cs`**
- Store the last projection snapshot in agent state.
- Each cycle: compute `ProjectionDelta`; pass only the delta to LLM context ("here is what changed since last cycle").
- If `!delta.AnyChange` for `StallDetectionCycles` consecutive cycles: write dead-letter entry with reason "Stall: no artifact change after N cycles."

`WorkspaceOptions.StallDetectionCycles` defaults to 2.

### Success criteria
- Two cycles with no artifact change: stall dead-letter written; dashboard shows it.
- Three cycles with incremental change: no stall.
- Orchestrator LLM context contains delta JSON, not full projection snapshot, after the first cycle.

### As-built (12b)

- **A "cycle" is one LLM iteration within a single orchestrator session**, not a cross-reinvocation
  concept. The scheduler reinvokes the orchestrator with a brand-new `OrchestratorAgentLoop`
  instance and a new `agentId` each time a child completes
  (`WorkSchedulerService.cs` → `InMemoryAgentRuntimeService.ReinvokeOrchestratorAsync`), so nothing
  survives between reinvocations today without a new persisted field. State for diffing/streak
  tracking is just local variables (`lastProjection`, `stallStreak`) on the loop instance for the
  duration of one `RunAsync` call — out of scope to make it durable across reinvocations.
- **`ProjectionDelta.Compute`** lives directly on the `ProjectionDelta` record in
  `ProjectionContracts.cs` (no dependencies, pure diff over `ArtifactRef.ArtifactId`/`Status`).
  `RemovedArtifacts` means "previously active, now non-Active" — artifacts are never deleted from
  the chain (append/replace lineage), so nothing is ever actually removed from the list.
- **Delta is pushed, not pulled.** `OrchestratorAgentLoop.RunAsync` fetches the AgentWorkspace
  projection itself at the top of every iteration and folds the delta into the conversation —
  the LLM no longer has to call `nm_v1_projection_get` to stay current (the tool still exists for
  pulling a full snapshot on demand; `ProjectionManager`/`ProjectionTools` are unchanged).
- **Gotcha that broke two existing integration tests until caught**: `LlmClient` serializes a
  message with a single `NmText` content block as a plain string (`content: "..."`), the Anthropic
  shorthand — existing fake LLM handlers (`ScheduledReinvocationLlmHandler`) rely on that shorthand
  to parse the kickoff message via `.GetProperty("content").GetString()`. Appending the delta as a
  *second* content block turned that into an array and broke the parse. Fix: when the outgoing
  message is `[NmText]`, the delta is folded into that same `NmText` (one combined string) instead
  of appended as a second block; only multi-block messages (tool-result turns) get the delta
  appended as an extra block.
- Stall dead-letters reuse the exact `IDeadLetterService.RecordFailureAsync` path as
  `MaxIterationsExceeded` (`InMemoryAgentRuntimeService.StartOrchestratorLoop`), just with a
  `"Stall: no artifact change after N cycles."` reason — the dashboard needed zero changes.
- `AgentRuntime` gained a `ProjectReference` to `Storage` (no cycle — `Storage` only depends on
  `Core`) so it can resolve `WorkspaceOptions.StallDetectionCycles` from DI.
- Tests: `ProjectionDeltaTests.cs` (unit, Contracts.Tests) covers the diff math directly;
  `ProjectionDiffingIntegrationTests.cs` covers stall-fires (reusing `ExhaustingLlmHandler`) and
  stall-does-not-fire-on-real-progress (reusing `ScheduledReinvocationLlmHandler`).

---

## Slice 12c — Pipeline Stage Streaming

Real-time stage badges on every work unit node in the Artifact Explorer (and existing DAG replay panel), so the user always knows where in the pipeline each unit is without polling.

### Work unit stage field

**`WorkUnit` domain record** — add `PipelineStage? CurrentStage` (null = not yet started).

Stage transitions set by orchestrator and scheduler:
- Scheduler acquires + spawns planner → `Plan`
- Scheduler acquires + spawns worker → `Execute`
- Worker calls `merge.propose` → `Review`
- Merger runs → `Merge`
- Human approves + apply completes → stage cleared, status = `Merged`

### WebSocket event

**`/ws/runtime`** — add `work-unit-stage-changed` event:
```json
{ "type": "work-unit-stage-changed", "workUnitId": "...", "stage": "Execute" }
```

### Badge colors

| Stage | Color |
|-------|-------|
| `Plan` | blue |
| `Execute` | amber |
| `Review` | purple |
| `Merge` | teal |
| `Merged` (complete) | green |
| `DeadLettered` | red |

### Success criteria
- Artifact Explorer shows live stage badge updates as a goal runs end-to-end.
- Work unit shows `Review` (purple) while a proposal awaits human approval.
- Stage transitions arrive within 1 second of the server-side event (WebSocket latency).

### As-built (12c)

- **`WorkUnit.CurrentStage`, the `PipelineStage` enum, and every stage transition in this slice's
  list were already in place** before this slice started — `WorkSchedulerService.SetCurrentStageAsync`
  (Plan/Execute on lease acquisition), `McpToolDispatcher.ProposeAsync` (Review, on `merge.propose`),
  `MergeReconciliationService.TryReconcileAsync` (Review on conflict, Merge on successful
  reconciliation), and `InMemoryMergeService.ApplyAsync` (clears the stage, sets `Merged`) all
  call `IWorkUnitService.SetCurrentStageAsync` already. The only genuinely missing piece was
  getting those changes onto the wire — there was no `/ws/runtime` *server-side* implementation
  in this repo at all (it lives in the embedded `NodalMerge.DotNetHost` engine — see ADR 001 —
  referenced via the conditional `NodalMergeDotNetHostProject` project reference in
  `NodalMerge.Studio.Host.csproj`), and nothing in Studio ever called its `RuntimeRoomBroker`.
- **New `IRuntimeEventBroadcaster`** (`NodalMerge.Studio.Core/Services/ServiceContracts.cs`) is a
  single-purpose interface (`BroadcastWorkUnitStageChangedAsync`), not a generic event bus — no
  other event needed pushing for this slice, so a generic abstraction would have been
  speculative. `RuntimeRoomEventBroadcaster` (new, `NodalMerge.Studio.Host`) is the only
  implementation; it wraps `RuntimeRoomBroker.BroadcastAsync` against the hardcoded room id
  `"studio-main"` — the same room the extension's existing DAG Replay WebSocket client
  (`wsClient.ts`) already connects to, so there's exactly one live room per Studio Host process.
- **Wired into the single choke point, not each call site.** `InMemoryWorkUnitService.SetCurrentStageAsync`
  is the only place `WorkUnit.CurrentStage` is ever written, so the broadcast call lives there
  (one `if (_broadcaster is not null)` after the node-store write) rather than at each of the four
  call sites above — they all get live updates for free.
- **Optional collaborator, resolved via `IServiceProvider.GetService`**, following the same
  pattern `WorkSchedulerService` already uses for `IMergeService`/`IIntentGraphService`:
  `IRuntimeEventBroadcaster` is only registered when running inside the real Studio Host
  (`StudioWebApplication.Build`, where `RuntimeRoomBroker` actually exists in the DI container).
  Unit/integration tests construct `InMemoryWorkUnitService` directly and never supply one —
  `SetCurrentStageAsync` works exactly as before when `_broadcaster` is null.
- **Client wiring lives entirely in `ArtifactExplorerPanel.ts`'s inline webview script**, not a
  shared TS module — that pane's `EXPLORER_JS`/`EXPLORER_CSS` are plain string literals (the
  `getFragment()` convention shared with the other shell panes), so it can't `import` the
  dag-replay webview's `wsClient.ts`. The extension host sends the WS URL once via a new
  `explorerWsInit` message (computed from `baseUrl`, same `http→ws` + `/ws/runtime` substitution
  DagReplayPanel already does); the inline script opens the socket itself, sends the same `hello`
  handshake shape (`room: 'studio-main'`), and on `work-unit-stage-changed` patches the matching
  node in `state.workUnits` in place and re-renders — no extra REST round-trip. A naive 2s
  reconnect-on-close loop is included since the panel is meant to stay live for the life of the
  shell.
- **Badge colors only needed adding for the 4 in-flight stages** (`Plan`/`Execute`/`Review`/`Merge`)
  — `Merged` (green) and `DeadLettered` (red) from the plan's color table were already covered by
  the existing `WorkUnitStatus` badge classes (`.badge.merged`, `.badge.deadlettered`), since
  `CurrentStage` is null by the time a work unit reaches either of those terminal statuses.
- **Scoped to the Artifact Explorer only** — the plan's "(and existing DAG Replay panel)" aside
  wasn't pursued: `DagReplayPanel` renders branch/commit lineage nodes, not work units, and has no
  stage-bearing data to badge today. The success criteria only call out the Artifact Explorer, so
  extending DAG Replay's rendering would have been scope beyond this slice.
- Tests: `WorkUnitLifecycleTests.cs` gained 3 cases (broadcast fires with the right
  workUnitId/stage, broadcasts `null` when the stage is cleared, and `SetCurrentStageAsync` still
  works with no broadcaster configured) using a recording fake `IRuntimeEventBroadcaster`.

---

## Slice 12d — LLM-Driven Profile Selection

Orchestrator asks an LLM which profile best fits a given task, replacing the deterministic heuristic routing from 9e. Heuristic routing stays as the fallback and as the default.

### Profile selection

**`OrchestratorAgentLoop.cs`**
- `SelectProfileAsync(WorkUnit childUnit, IReadOnlyList<AgentProfile> profiles)`:
  - Lightweight LLM call: work unit goal + file scope + available profile names/stages/prompt excerpts.
  - LLM returns a `profileId`; if unknown or timeout → heuristic fallback.
- Orchestration event records reason: `"LLM selected {profileId}: {explanation}"`.

### Toggle

**Artifact Explorer** — settings gear icon: "Use LLM profile selection" checkbox. Default: off. When off, heuristic routing only.

### Success criteria
- Toggle on; goal clearly maps to `worker`; LLM selects `worker`; orchestration event shows reason.
- LLM returns unknown profile: fallback to heuristic; no error surfaced.
- Toggle off: no LLM call for profile selection; all integration tests pass (toggle is off by default in tests).

### As-built (12d)

- **There was no pre-existing `SelectProfileAsync` heuristic to "replace".** The only profile
  routing in the codebase before this slice was `FanOutService.EnqueueChildWorkerAsync`
  hardcoding `profileId = "worker"` for every child fanned out from a plan — confirmed by
  grepping the whole repo for `profileId`/heuristic routing before starting. This slice's
  heuristic fallback reproduces that exact default rather than replacing a function that never
  existed.
- **New seam: `IProfileSelectionService`** (`ServiceContracts.cs`), implemented by
  `LlmProfileSelectionService` in `NodalMerge.Studio.AgentRuntime` (internal, like
  `OrchestratorAgentLoop`/`LlmClient`) and consumed by `FanOutService` (`NodalMerge.Studio.Orchestrator`).
  This split exists because `LlmClient` lives in AgentRuntime, which already has a
  `ProjectReference` to Orchestrator (not the other way around) — Orchestrator can only depend on
  the Core-level interface, never on AgentRuntime's concrete implementation. DI registration in
  `AddStudioAgentRuntime` is the only place that wires the two together.
- **`WorkspaceOptions.UseLlmProfileSelection`** (default `false`) gates the whole feature; when
  off, `LlmProfileSelectionService.SelectProfileAsync` returns the heuristic immediately with zero
  LLM calls — verified by a call-counting fake handler in `LlmProfileSelectionTests.cs`. Same
  fallback path (with a reason string explaining why) covers missing credentials, an LLM response
  that isn't parseable JSON, an unknown `profileId`, and HTTP failure/timeout (10s linked-token
  timeout around the call).
- **Every child enqueue now gets an `OrchestrationEvent`, not just the LLM-selected ones.**
  Fan-out child enqueue previously had *no* decision-log entry at all — it happens outside any
  LLM tool call, so `OrchestratorAgentLoop.RecordToolDecisionAsync` never saw it. Recording one
  unconditionally in `FanOutService.EnqueueChildWorkerAsync` (reusing `OrchestrationAction.Enqueue`,
  with `orchestratorAgentId` set to the sentinel `"fanout"` since no live agent id is available at
  that call site) makes the selected profile and reason auditable from the Artifact Explorer
  regardless of whether the toggle is on — simpler than special-casing only the LLM path.
- **`/studio/options` (GET/POST)** added to `StudioRestEndpoints.cs` as the first endpoint that
  reads/mutates a `WorkspaceOptions` field at runtime (previously config-file-only, e.g.
  `StallDetectionCycles` from 12b). Mutates the singleton instance directly — no persistence
  layer, matching how the rest of `WorkspaceOptions` already behaves.
- **Artifact Explorer settings gear** (`ArtifactExplorerPanel.ts`): a gear button toggles a small
  panel with the "Use LLM profile selection" checkbox; `activate()` now also calls `sendSettings()`
  (GET `/studio/options`) alongside the existing `sendTemplates()`/`sendWsInit()` calls, and the
  checkbox's `change` event posts straight to `/studio/options` — no extra round-trip through a
  dedicated config service.
- **Gotcha that shaped the test design**: seeding `OrchestratorCredentials` (required for the LLM
  call) only happens via `IAgentControlService.SpawnAsync("orchestrator", ...)`, which — whenever
  real-looking credentials are supplied — *always* starts a live background `OrchestratorAgentLoop`
  (`InMemoryAgentRuntimeService.StartOrchestratorLoop`), and that loop unconditionally calls
  `IFanOutService.TryFanOutFromPlanAsync` itself once its turn ends. `FanOutServiceTests.cs`'s
  pattern of spawning the orchestrator *and then also* calling `TryFanOutFromPlanAsync` directly
  from the test races the test's call against the loop's own call — both can reach
  `EnsureChildWorkUnitsAsync` before either's new child is visible to the other, creating two
  children for the same plan slice (a pre-existing concurrency gap in `FanOutService`, out of
  scope to fix here). Worked around in `LlmProfileSelectionTests.cs` by writing `plan.json`
  *before* spawning the orchestrator and only ever letting the background loop trigger fan-out
  once, then polling the decision log for the resulting event — never calling
  `TryFanOutFromPlanAsync` directly.
- Tests: `LlmProfileSelectionTests.cs` (new, Integration) — toggle on + LLM selects a known
  profile (reason recorded), toggle on + LLM selects an unknown profile (heuristic fallback, no
  error), toggle off by default (zero LLM calls for selection). A `ProfileSelectionLlmHandler`
  fake distinguishes the lightweight selection call from the orchestrator loop's own LLM traffic
  by checking the request body for the selection system prompt's marker text — robust to both
  the Anthropic and OpenAI-compatible wire formats since both serialize the system prompt into
  the JSON body. Full suite (94 integration tests + all unit tests) passes.

---

## Slice ordering

12a → 12b → 12c → 12d

- **12a first**: the Artifact Explorer is the primary deliverable of Phase 5 and what makes every other investment visible to the user.
- **12b before 12c**: projection diffing feeds stall detection which becomes visible via 12c stage streaming.
- **12c before 12d**: stage streaming makes LLM profile selection observable; no point building it in the dark.
- **12d last**: additive enhancement to routing; heuristic routing is already working.

---

## Files not touched in Phase 5

| File | Reason |
|------|--------|
| `InMemoryMergeService.cs` | Write-back complete since 2.5 |
| `IWorkScheduler.cs` | Complete from Phase 3 |
| `IFileWorkspaceService.cs` | Complete from Phase 3 |
| Phase 2.5 profiles | Additive only |

---

## The answer after Phase 5

After Phase 5, the answer to the strategic question is complete:

> What can I do after an agent run completes?

- **Inspect**: every artifact in the timeline (plan, tasks, diffs, proposals, orchestration decisions)
- **Branch**: one click from any proposal to spawn alternate execution from the same base state
- **Replay**: re-run a proposal with a different model or profile by branching and submitting a goal
- **Compare**: two proposals from the same base state shown side-by-side with overlap detection
- **Audit**: full orchestration decision log per work unit with input projection snapshot + reason
- **Steer**: split, re-order, or re-run any work unit node from the panel

That is the durable artifact platform. Everything before this was infrastructure to make that answer possible.

---

## Phase 6 pointer (future)

- **Policy / Validator layer**: cross-cutting gate between stages enforcing schema, invariants, repo rules — not an agent, a pipeline primitive.
- **Cross-repo work units**: `SeedRepositoryPath` arrays for work spanning multiple repositories.
- **Persistent branch history**: branches survive server restart (real blob store, not `WsOnly`).
- **AST-level conflict detection**: syntax-aware diff in the merger stage (11c), not line-level.
- **Collaborative steering**: multiple humans editing the work unit DAG simultaneously (CRDT / OT).
