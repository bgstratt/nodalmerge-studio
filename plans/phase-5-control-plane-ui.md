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
