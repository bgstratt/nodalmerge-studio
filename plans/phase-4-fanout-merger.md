# Phase 4 — Fan-out, Merge Reduction & Lifecycle

Phase 3 delivers the five foundational systems: Work Unit DAG, Scheduler, Branch Isolation, Artifact Lineage, and Decision Log. Phase 4 uses those foundations to make the pipeline genuinely parallel and add the lifecycle state machine that makes artifact state observable everywhere.

---

## What Phase 4 adds

| Phase 3 | Phase 4 |
|---------|---------|
| One scheduled work unit per goal | Planner decomposes goal into N parallel work units |
| Workers execute sequentially (one at a time or limited by concurrency cap) | Workers execute in true parallel on isolated branches |
| Proposals are individual floating artifacts | N proposals reconciled by Merger/Reducer into one candidate |
| Artifact status is implicit flags | Formal lifecycle state machine per artifact |
| Human always reviews proposals directly | Optional automated reviewer agent as pre-gate |
| Failed agents are silent | Dead-letter escalation with retry from dashboard |

---

## Slice 11a — Artifact Lifecycle State Machine

Formal state machine replaces the implicit status flags on proposals and work units. This is the backbone for event-driven orchestration and the control-plane UI in Phase 5.

### States

```
WorkUnit:
  Created → Planned → Queued → Executing → Proposed → Reviewing → Merged | Rejected | DeadLettered | Retrying

MergeProposal:
  Draft → ReadyForReview → UnderReview → Approved | Rejected | Superseded
```

**`Superseded`** is new: when a merger produces a reconciled proposal, the constituent proposals transition to `Superseded` (not rejected — they were correct, just collapsed).

### Domain changes

**`src/NodalMerge.Studio.Contracts/Domain/WorkUnit.cs`**
- `WorkUnitStatus` enum expanded to include `Planned`, `Queued`, `Executing`, `Proposed`, `Reviewing`, `Merged`, `DeadLettered`, `Retrying`.

**`src/NodalMerge.Studio.Contracts/Domain/MergeProposal.cs`**
- `ProposalStatus` enum expanded to include `Draft`, `UnderReview`, `Superseded`.

### Transition enforcement

**`src/NodalMerge.Studio.Storage/WorkUnitService.cs`**
- `UpdateStatusAsync` validates legal transitions. Illegal transition returns a structured error (not throws).

### Orchestration event log

Every state transition writes an `OrchestrationEvent` (from 10e) with the transition as the `Action`. This gives a full audit trail with timestamps.

### Success criteria
- Work unit status transitions in order: `Created → Queued → Executing → Proposed → Merged`.
- Attempt to transition `Merged → Executing` returns a transition error.
- Dead-lettered work unit has `DeadLettered` status; can transition to `Retrying`.
- `Superseded` proposals visible in API response with `supersededBy: <proposalId>`.

---

## Slice 11b — Fan-out (Planner → N Workers)

Planner produces a structured decomposition; orchestrator creates N child work units and enqueues them all. Scheduler executes them in parallel up to the concurrency limit.

### Planner output contract

`plan.json` on the work unit's execution branch:

```json
{
  "slices": [
    {
      "sliceId": "s1",
      "goal": "Implement Foo.cs",
      "fileScope": ["src/Foo.cs", "src/IFoo.cs"],
      "dependsOn": [],
      "steps": ["Create interface", "Implement class"]
    },
    {
      "sliceId": "s2",
      "goal": "Add Bar.cs",
      "fileScope": ["src/Bar.cs"],
      "dependsOn": ["s1"],
      "steps": ["Implement Bar referencing Foo"]
    }
  ]
}
```

`dependsOn` references sibling `sliceId` values. Orchestrator resolves these to `WorkUnitId` edges before enqueueing (10a dependency graph).

### Orchestrator fan-out

**`OrchestratorAgentLoop.cs`**
1. Read `plan.json` from execution branch after planner completes.
2. Parse slices; create child `WorkUnit` per slice with `fileScope` and `dependsOn` resolved.
3. Call `IWorkScheduler.EnqueueAsync` for all slices with no unsatisfied dependencies.
4. Subscribe (or poll) for child completions; as each completes, re-evaluate which dependents can now be enqueued.

Dependency-aware enqueue: slice `s2` above is only enqueued after `s1` transitions to `Proposed` or `Merged`.

### Success criteria
- Goal with two independent file changes; planner produces two-slice `plan.json`; orchestrator creates two child work units; both appear in the scheduler pending queue simultaneously.
- Two parallel workers execute; each writes only to files in its `fileScope`; neither sees the other's branch files.
- Both proposals appear in Merge Review panel under the parent work unit.
- Dependent slice (`dependsOn: ["s1"]`) is not enqueued until `s1` completes.

---

## Slice 11c — Merger/Reducer

Takes all approved (or ready-for-review) proposals for a parent work unit and produces a single reconciled candidate. Human reviews the reconciled proposal, not N individual ones.

### New profile

Seed a `merger` profile:
- `Stage = Merge`
- `AllowedTools`: `merge.list`, `merge.read`, `merge.apply`, `file.read`, `file.write`, `projection.get`, `snapshot.compare`
- `MaxIterations`: 15
- System prompt: instructs agent to read all ready-for-review proposals in the work unit, detect overlapping `filesTouched`, produce either a reconciled proposal or a conflict report.

### Reconciliation strategy

Merger runs on a dedicated `merge/{workUnitId}` branch (created from `main`).

**Non-overlapping proposals**: merger copies each proposal's file changes in dependency order → produces one reconciled `MergeProposal` on the merge branch. Constituent proposals transition to `Superseded`.

**Overlapping proposals**: merger writes `merge-conflict-report.md` to the work unit's DAG state, transitions the work unit to `Reviewing`, and escalates to human. Conflict report includes: overlapping files, which proposals conflict, and merger's suggested resolution.

### Orchestrator routing update

After all child work units reach `Proposed`:
1. Orchestrator enqueues a `merger` agent via the scheduler.
2. Merger runs on the isolated merge branch.
3. Human reviews the single reconciled proposal (or the conflict report).

### Merge Review panel update

- Show "Reconciled from N proposals" label when `origin.stage = Merge`.
- Show constituent proposal IDs and their `Superseded` status.
- If conflict report exists, show it inline with a "Resolve manually" prompt.

### Success criteria
- Two workers produce non-overlapping changes; merger combines them into one proposal; human sees one unified diff.
- Two workers modify the same file at overlapping lines; merger writes a conflict report; work unit enters `Reviewing` state with the conflict report visible.
- Constituent proposals show `Superseded` status after reconciliation.

---

## Slice 11d — Automated Reviewer Agent (Optional Pre-gate)

An automated review pass before the human gate. Catches obvious issues without involving a human.

### New profile

Seed a `reviewer` profile:
- `Stage = Review`
- `AllowedTools`: `merge.read`, `merge.validate`, `merge.review`, `projection.get`, `file.read`
- `MaxIterations`: 10
- System prompt: instructs agent to evaluate the proposal against the original goal, check that `filesTouched` matches `fileScope`, flag anything obviously wrong.

### Reviewer output

Reviewer calls `nm.v1.merge.review` with decision `Approved` or `Rejected` and a `verificationResults` note. If rejected: orchestrator can route back to `Queued` (retry from Execute stage) or escalate to dead-letter after N retries.

### Merge Review panel update

When `verificationResults` is populated, show it in the review panel (field already exists; just needs rendering):
- "Automated Review: Approved" (green) or "Automated Review: Rejected: {reason}" (red).

### Reviewer toggle

**`AgentConfigPanel.ts`** — checkbox in Quick Spawn: "Run automated review before human gate". Sends `autoReviewProfileId: "reviewer"` in spawn body. If omitted, proposals go directly to human gate (existing behavior).

### Success criteria
- Goal produces a correct proposal; automated reviewer approves it; human sees "Automated Review: Approved" in the panel.
- Intentionally broken goal (missing required file per plan); reviewer rejects with reason; proposal shows `Rejected` status with reason before reaching human.
- Automated review disabled: proposals reach human gate without reviewer spawning.

---

## Slice 11e — Dead-letter & Failure Escalation

Failed agents (timeout, loop error, iteration limit) are captured as dead-letter entries with structured escalation. Human can retry from the dashboard.

### Domain record

**`src/NodalMerge.Studio.Contracts/Domain/DeadLetterEntry.cs`** (new)

```csharp
public sealed record DeadLetterEntry(
    string EntryId,
    string WorkUnitId,
    string AgentId,
    PipelineStage Stage,
    string ProfileId,
    string Reason,
    string? LastProjectionSnapshot,    // state at time of failure
    int AttemptCount,
    DateTimeOffset OccurredAt);
```

Stored in DAG at `studio/dead-letter/v1`.

### Agent runtime

**`InMemoryAgentRuntimeService.cs`**
- On loop failure: write `DeadLetterEntry`; transition work unit to `DeadLettered` (11a lifecycle).
- `AttemptCount` increments on retry; after 3 attempts, work unit stays `DeadLettered` and requires human intervention.

### Workspace Dashboard panel

- "Failed" section listing dead-letter entries: goal, stage that failed, reason, attempt count, "Retry" button.
- "Retry" button re-enqueues the work unit via `IWorkScheduler.EnqueueAsync` with `AttemptCount` incremented.
- Work unit transitions to `Retrying` state (11a).

### Success criteria
- Agent that hits max iterations: dead-letter entry written; dashboard shows it; work unit status = `DeadLettered`.
- "Retry" button: new agent spawns at the same stage with the same profile; work unit transitions `Retrying → Executing`.
- After 3 failed attempts: retry button disabled; entry marked "Max attempts reached."

---

## Slice ordering

11a → 11b → 11c → 11d → 11e

- **11a first**: lifecycle state machine is referenced by every subsequent slice.
- **11b before 11c**: merger needs child proposals to exist; fan-out must work first.
- **11c before 11d**: automated reviewer runs on a proposal; merger must produce one first.
- **11d before 11e**: dead-letter captures stage at failure; reviewer is the last new stage, so it must exist.
- **11e last**: dead-letter is failure-path hardening; build the happy path first.

---

## Files not touched in Phase 4

| File | Reason |
|------|--------|
| `LlmClient.cs` | Provider abstraction complete |
| `InMemoryMergeService.cs` | Write-back already fixed in 2.5 |
| `IWorkScheduler.cs` | Only call sites change; interface is complete from 10b |
| `IFileWorkspaceService.cs` | `CreateBranchFromAsync` from 10c is sufficient |
| `AgentConfigPanel.ts` (profiles tab) | New profiles appear automatically |

---

## Phase 5 pointer

After Phase 4, the system executes parallel work correctly. Phase 5 adds the control-plane visibility layer:

- **Plan Breakdown Panel**: extension panel where a user pastes (or the orchestrator populates) a plan and sees it decomposed into a live WorkUnit DAG, with agent state and proposal state per node.
- **Projection diffing**: orchestrator reads what changed between cycles instead of re-deciding from scratch.
- **Pipeline stage streaming**: real-time stage badges on work unit nodes in DAG replay panel.
- **LLM-driven profile selection**: orchestrator asks an LLM which profile fits a task (replaces deterministic routing from 9e).
