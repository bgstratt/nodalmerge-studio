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

## Slice 11a — Artifact Lifecycle State Machine ✅ (scoped — fan-out itself, 11b-11e, is not built yet)

> **As-built note:** Built as part of a Phase 4 kickoff alongside the orchestrator re-invocation fix (see `plans/phase-3-foundations.md`'s Deferred Work Tracker), since fan-out (11b) assumes both exist. Several deviations from the literal spec below, each for a concrete reason:
>
> - **Enums expanded additively, not renamed.** `WorkUnitStatus` keeps its original six values (`Created, Active, Waiting, Completed, Failed, Cancelled`) — they're still live in the legacy direct-spawn path (`IAgentControlService.SpawnAsync("worker", ...)`, used by `FullAgentCycleTests`), which never touches the scheduler and so never reaches the new states. `Queued, Executing, Proposed, Reviewing, Merged, DeadLettered, Retrying` were added alongside, not in place of, the originals. `Planned` was **not** added — there's still no separate planner stage to produce it, the same gap that's kept `ArtifactType.Plan` defined-but-unrecorded since 10d. The spec's own diagram and prose enum list disagree on `Rejected` (diagram shows it, the addition list below doesn't); the prose list was followed, so `WorkUnitStatus.Rejected` does not exist.
> - **`MergeProposalStatus` gained `UnderReview` and `Superseded`**, but only `Superseded` has wired transitions (`ReadyForReview/Approved → Superseded`) — defined now so 11c's merger won't need to touch `MergeProposalTransitions` later, exactly like `ArtifactStatus.Superseded` already existed with no producer before this slice. `UnderReview` is wired to nothing; nothing produces it until 11d's automated-reviewer pre-gate.
> - **Throw-based internal pattern kept, not refactored to "structured error, not throws."** Every call site at every boundary — REST (`StudioRestEndpoints.cs` `/studio/merges/{id}/validate|review|apply`) and MCP tools (`MergeTools.cs`) — already wraps these calls in try/catch and converts `InvalidOperationException` to a structured error response. The literal spec's file reference (`src/NodalMerge.Studio.Storage/WorkUnitService.cs`) also doesn't exist — the real implementation is `InMemoryWorkUnitService.cs` in the `NodalMerge.Studio.Orchestrator` project (a Phase 3 as-built fact, not new to this slice).
> - **Transitions are NOT logged through `OrchestrationEvent`/`OrchestrationAction`.** `OrchestrationAction` (`SpawnPlanner/SpawnWorker/Enqueue/AwaitReview/ApplyMerge/Escalate/NoOp`) represents orchestrator *routing decisions*, not state-pair transitions, and most new transitions (lease acquired, merge applied) have no orchestrator decision behind them at all. Instead, two new `ExecutionEventKind` values — `WorkUnitStatusChanged` and `MergeProposalStatusChanged`, with `*StatusChangedPayload(Id, PreviousStatus, NewStatus)` records — were added on the existing `IExecutionEventStream`, mirroring the shape of the already-defined-but-dormant `ArtifactStatusChanged`/`ProposalSuperseded` kinds from 10b.2. `InMemoryWorkUnitService.UpdateStatusAsync` emits `WorkUnitStatusChanged` centrally (gained a new optional `sessionId` parameter for this — threaded through `McpToolDispatcher.WorkUnitUpdateAsync`; the MCP-server tool surface `WorkUnitTools.cs` has no session concept and always passes `null`, consistent with the rest of that surface). `InMemoryMergeService` emits `MergeProposalStatusChanged` from `ValidateAsync`/`ReviewAsync`/`ApplyAsync` via its existing `IExecutionEventStream` dependency.
> - **Where transitions actually get wired — the real value of this slice.** `WorkSchedulerService` never called `IWorkUnitService.UpdateStatusAsync` at all before this slice; `WorkUnit.Status` was inert in the queue-driven pipeline. Now: `EnqueueAsync` (`Created → Queued`, first enqueue only) and `TryAcquireAsync` (`Queued → Executing`, lease acquired) resolve `IWorkUnitService` lazily via the existing optional `_serviceProvider` (same pattern as `DetectConflictAsync`'s `IWorkUnitService` lookup — no new constructor parameter). `McpToolDispatcher.MergeProposeAsync` drives `Executing → Proposed` (best-effort, try/catch-swallowed on illegal transition — the legacy path never reaches `Executing`, so this silently no-ops there by design, not error). `InMemoryMergeService.ApplyAsync` drives `Proposed/Reviewing → Merged` the same way, via a **new** optional `IServiceProvider? serviceProvider = null` constructor parameter (no direct `IWorkUnitService` injection — `InMemoryWorkUnitService` already depends on `IMergeService`, so a direct dependency would cycle, same shape `WorkSchedulerService` already avoids). `WorkSchedulerService.ReleaseAsync`'s failure branch drives `Executing → Retrying`.
> - **`Reviewing` and `DeadLettered` remain defined but unreachable** — same status as `SpawnPlanner`/`Escalate` in `OrchestrationAction` (10e) and `ArtifactType.Plan`/`BranchChangeset` (10d): there's no automated-reviewer pre-gate (11d) or dead-letter mechanism (11e) yet to produce them.
>
> Implementation: [WorkUnit.cs](../src/NodalMerge.Studio.Contracts/Domain/WorkUnit.cs), [MergeProposal.cs](../src/NodalMerge.Studio.Contracts/Domain/MergeProposal.cs), [ExecutionEvent.cs](../src/NodalMerge.Studio.Contracts/Domain/ExecutionEvent.cs)/[ExecutionEventPayloads.cs](../src/NodalMerge.Studio.Contracts/Domain/ExecutionEventPayloads.cs), [InMemoryWorkUnitService.cs](../src/NodalMerge.Studio.Orchestrator/InMemoryWorkUnitService.cs), [InMemoryMergeService.cs](../src/NodalMerge.Studio.Merge/InMemoryMergeService.cs), [WorkSchedulerService.cs](../src/NodalMerge.Studio.Storage/WorkSchedulerService.cs), [McpToolDispatcher.cs](../src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs). Tests: extended `DomainTests.cs`, `InMemoryMergeServiceTests.cs`, `ControlPlaneIdempotencyTests.cs`; new `WorkUnitLifecycleTests.cs`.

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

### Carried over from Phase 3 — due now that real fan-out exists

These were deferred in `plans/phase-3-foundations.md`'s Deferred Work Tracker specifically until this slice; resolve them as part of 11b, not as a separate later pass:

- **Record `ArtifactType.Plan`** (10d) — `Type = Plan, ParentArtifactId = goalArtifactId` — when the planner produces `plan.json`, before child WorkUnits are created. Previously there was no separate planner stage to produce this artifact; this slice is what creates one.
- **Emit `OrchestrationAction.SpawnPlanner`** (10b.2/10e), not `SpawnWorker`, when the orchestrator's decision is to invoke the planner agent.
- **Record `ArtifactType.BranchChangeset` per child work unit** (10d) — at `WorkSchedulerService.ReleaseAsync(success: true)`, just before `IAgentWorkspaceService.ArchiveAsync`: diff the work unit's branch against its seed (via `seedFromBranchId`, when present) using `IFileWorkspaceService.DiffAsync`, record `Type = BranchChangeset, ParentArtifactId = taskArtifactId`. Switch `MergeProposal.ParentArtifactId` from `workUnitId` to this changeset's ID once it exists.
- **Verify the 10f.5 conflict-warning path with a real two-worker race**, then decide whether region locking (Option B) or optimistic execution + revalidation (Option C) is actually needed, or whether the existing advisory `ConflictWarning` is sufficient now that real concurrency exists to test it against. If Option B/C ships, also build the planner/agent revalidation loop (structured "your intent was queued/rejected, refine it" message) — it has nothing to queue or reject against until then.
- *(Non-blocking aside, 10c)* Real git worktree backing (`IWorkspaceBackingStore` with `DirectoryCopyWorkspace`/`GitWorktreeWorkspace`) remains optional even with real parallel workers — the plain-text diff/branch-copy isolation already prevents two workers from seeing each other's files (see Workspace Isolation success criteria below). Only revisit if containerized/remote isolation becomes a real requirement.

### Success criteria
- Goal with two independent file changes; planner produces two-slice `plan.json`; orchestrator creates two child work units; both appear in the scheduler pending queue simultaneously.
- Two parallel workers execute; each writes only to files in its `fileScope`; neither sees the other's branch files.
- Both proposals appear in Merge Review panel under the parent work unit.
- Dependent slice (`dependsOn: ["s1"]`) is not enqueued until `s1` completes.
- `Goal` artifact chain includes a `Plan` artifact; `OrchestrationEvent` log includes a `SpawnPlanner` action; each child work unit's chain includes a `BranchChangeset` artifact after completion.

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

### Carried over from Phase 3 — due now that a merger exists

- **Populate `reconciledFrom`** (10f) in the `proposal-dag` response's `mergeProposal` section once the merger produces a reconciled candidate. Previously `proposal-dag` returned proposals and branches only, with no merger/reducer section, because single-worker proposals don't get reconciled — they get reviewed directly. This slice is what gives the field something to point at.

### Success criteria
- Two workers produce non-overlapping changes; merger combines them into one proposal; human sees one unified diff.
- Two workers modify the same file at overlapping lines; merger writes a conflict report; work unit enters `Reviewing` state with the conflict report visible.
- Constituent proposals show `Superseded` status after reconciliation.
- `GET /studio/workunits/{id}/proposal-dag` includes a populated `mergeProposal.reconciledFrom` list once a merger has run.

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

### Carried over from Phase 3 — due now that failure-classification exists

- **Emit `OrchestrationAction.Escalate`** (10b.2/10e), not just a written `DeadLetterEntry`, when the new failure-classification logic decides a work unit needs human intervention rather than another automatic retry. Previously there was no classification logic to decide what counts as escalation-worthy — this slice is what creates it.

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
