> **Superseded** — replaced by [phase-3-foundations.md](./phase-3-foundations.md) (foundational systems) and [phase-4-fanout-merger.md](./phase-4-fanout-merger.md) (fan-out behavior). Fan-out without the Work Unit DAG, Scheduler, Branch Isolation, Artifact Lineage, and Decision Log was identified as building the outcome before the prerequisites. Kept for reference.

# Phase 3 — Multi-Agent Fan-out & Merge Reduction (Superseded)

Phase 2.5 delivers a working single-worker pipeline: Orchestrate → Execute → Propose → Human Review → Apply. Phase 3 makes that pipeline horizontal — N workers run in parallel on isolated branches — and adds the infrastructure to reconcile their output before human review.

---

## What Phase 3 adds

| Phase 2.5 | Phase 3 |
|-----------|---------|
| 1 worker per work unit | N workers, each on an isolated branch |
| Orchestrator spawns one Execute-stage agent | Orchestrator fans out to N Execute-stage agents |
| Human reviews one proposal at a time | Merger/Reducer reconciles proposals before human gate |
| Review is always human | Automated Reviewer agent as optional pre-gate |
| Pipeline state not visible in UI | Stage badges on each work unit in DAG replay panel |
| Failed agent → silent stall | Dead-letter entry → human escalation path |

---

## Slice 10a — Branch fan-out

Orchestrator spawns multiple workers, each on its own isolated branch, based on the plan produced at the Plan stage.

### Planner output contract

A planner at the Plan stage produces a structured plan that the orchestrator can decompose into parallel work slices. Each slice targets a disjoint set of files where possible (the orchestrator is responsible for partitioning; overlaps are handled at Merge stage).

Plan format (written to branch as `plan.json`):
```json
{
  "slices": [
    { "sliceId": "s1", "files": ["src/Foo.cs"], "goal": "...", "steps": ["..."] },
    { "sliceId": "s2", "files": ["src/Bar.cs"], "goal": "...", "steps": ["..."] }
  ]
}
```

### Orchestrator changes

**`OrchestratorAgentLoop.cs`**
- After reading `plan.json` from the artifact chain, decompose slices and spawn one worker per slice with:
  - `profileId = worker`
  - `branchId = <new isolated branch per slice>`
  - Slice goal + slice file scope injected into worker's seed context
- Track spawned worker IDs in the work unit's DAG state.

### Branch isolation

**`IFileWorkspaceService.CreateBranchFromAsync`** (if not already present)
- Creates a new branch as a copy of a source branch (snapshot the current main/plan branch state as the worker's starting point).

### Success criteria
- Quick Spawn a goal with two disjoint file changes; orchestrator spawns two workers; both produce proposals on separate branches.
- Workers complete independently; neither sees the other's changes.
- Both proposals appear in the Merge Review panel.

---

## Slice 10b — Merger/Reducer stage

A new pipeline stage that takes all approved (or ready-for-merge) proposals from a work unit and produces a single reconciled candidate.

### New profile

Seed a `merger` profile:
- `Stage = Merge`
- AllowedTools: `merge.list`, `merge.apply`, `projection.get`, `snapshot.compare`, `file.read`, `file.write`
- System prompt: instructs agent to read all proposals, detect overlapping file changes, produce a reconciled patch or a conflict report.

### Merger output

Merger produces one of:
- A **reconciled proposal** (new `MergeProposal` on a dedicated `merge/` branch) — if no conflicts detected.
- A **conflict report** (written to the work unit's DAG state) — if overlapping changes can't be auto-resolved, escalates to human.

### Orchestrator routing update

After all worker proposals reach `ReadyForReview`:
1. Orchestrator spawns a `merger` agent.
2. Merger reads proposals, produces reconciled output.
3. Human reviews the single reconciled proposal (not N individual ones).

### Success criteria
- Two workers produce non-overlapping changes; merger combines them into one proposal; human sees one diff.
- Two workers modify the same file at overlapping lines; merger writes a conflict report; human sees the conflict and can intervene.

---

## Slice 10c — Automated Reviewer agent (optional pre-gate)

An automated review pass before the human gate. Catches obvious issues (missing files referenced in the plan, malformed diffs, spec violations) without involving a human.

### New profile

Seed a `reviewer` profile:
- `Stage = Review`
- AllowedTools: `merge.validate`, `merge.review`, `projection.get`, `snapshot.compare`, `file.read`
- System prompt: instructs agent to evaluate the proposal against the original goal, check that changed files match the plan, flag anything obviously wrong.

### Reviewer output

Reviewer calls `nm.v1.merge.review` with decision `Approved` or `Rejected` and attaches a `verificationResults` note. If rejected, orchestrator can route back to Execute stage (retry) or escalate.

### Merge Review panel update

When a proposal has `verificationResults`, show them in the panel (already a field in the `MergeProposal` type; just needs to not be hidden when populated).

### Success criteria
- Goal produces a correct proposal; automated reviewer approves it; human sees "Reviewer: Approved" in the panel.
- Intentionally broken proposal (missing required file); reviewer rejects with reason; proposal shows `Rejected` status before reaching human.

---

## Slice 10d — Pipeline stage streaming to DAG replay panel

Make the current pipeline stage visible per work unit in the DAG replay panel, so the user can see where in the pipeline each task is without polling.

### Work unit stage tracking

**`WorkUnit` domain record** gains `PipelineStage? CurrentStage` (nullable — null means not yet started).

Stage transitions happen in the orchestrator:
- On spawn planner → stage = `Plan`
- On spawn worker → stage = `Execute`
- On merge proposed → stage = `Review`
- On merge applied → stage complete (work unit `Completed`)

### DAG replay panel

Stage badge per work unit node: color-coded chip showing the current stage. Updates in real-time via the existing WebSocket runtime path.

### Success criteria
- DAG replay panel shows stage badges updating as the pipeline progresses.
- Work unit shows `Review` while a proposal is pending human approval.
- Stage `Apply` briefly visible before work unit completes.

---

## Slice 10e — Dead-letter & failure escalation

Failed agents (timeout, loop error, LLM refusal) leave no visible trace today. Phase 3 surfaces them so humans can intervene.

### Dead-letter entry

**`DeadLetterEntry` domain record** (new):
```csharp
public sealed record DeadLetterEntry(
    string EntryId,
    string WorkUnitId,
    string AgentId,
    PipelineStage Stage,
    string Reason,
    DateTimeOffset OccurredAt);
```

Stored in DAG at `studio/dead-letter/v1`.

### Agent runtime

**`InMemoryAgentRuntimeService.cs`**
- On loop failure (uncaught exception or iteration limit exceeded without reaching a terminal state), write a `DeadLetterEntry`.
- Work unit stage set to a new `Failed` state.

### Extension dashboard

Workspace Dashboard panel shows a "Failed" section listing dead-letter entries with: work unit goal, stage that failed, reason, and a "Retry" button that re-spawns the agent at the same stage.

### Success criteria
- Agent that hits max iterations writes a dead-letter entry; dashboard shows it.
- "Retry" button spawns a new agent at the same stage with the same profile.
- Work unit resumes normally after retry.

---

## Slice ordering

10a → 10b → 10c → 10d → 10e

- **10a first**: fan-out is the core Phase 3 primitive; everything else builds on it.
- **10b before 10c**: merger runs before reviewer — no point reviewing if proposals can't be reconciled.
- **10c before 10d**: stage streaming is more useful once all stages exist.
- **10e last**: dead-letter is infrastructure hardening, not a core feature.

---

## Files not touched in Phase 3

| File | Reason |
|------|--------|
| `LlmClient.cs` | Provider abstraction complete |
| `InMemoryMergeService.cs` | Write-back logic already fixed in 2.5 |
| Phase 2.5 profiles | Additive only: merger and reviewer profiles are new seeds |
| Extension `AgentConfigPanel.ts` | New profiles appear automatically; no UI changes needed for 10a–10b |

---

## Phase 4 pointer

After Phase 3, the pipeline handles parallelism, conflict resolution, and automated quality gates. Phase 4 concerns:
- **Policy / Validator layer**: schema enforcement, invariant checking, repo rules — applied as a cross-cutting gate between stages rather than as an agent.
- **LLM-driven profile selection**: orchestrator asks an LLM which profile best fits a task's requirements (replaces the deterministic routing in 9e).
- **Cross-repo work units**: work that spans more than one `SeedRepositoryPath`.
- **Persistent branch history**: branches survive server restart (requires replacing `blob=WsOnly` with a real blob store).
