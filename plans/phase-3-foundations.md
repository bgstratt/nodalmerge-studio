# Phase 3 — Multi-Agent Foundations

Phase 2.5 delivers a correct single-worker pipeline. Phase 3 adds the five foundational systems that make parallelism safe and deterministic. Fan-out and multi-agent behavior arrive in Phase 4, but only because these primitives exist.

> "You are not missing more agent types — you are missing control-plane infrastructure."

---

## What Phase 3 adds (and why order matters)

| Gap | System | Slice |
|-----|--------|-------|
| Work is implicit state in a running loop | Work Unit DAG (parent/child, deps, scope) | 10a |
| Orchestrator drives logic; no global queue | Scheduler / Work Queue | 10b |
| Workers share file state; no ownership boundaries | Workspace Isolation | 10c |
| Proposals are floating artifacts with no attribution | Artifact Ownership + Lineage | 10d |
| Orchestration decisions are runtime heuristic; not replayable | Orchestration Decision Log | 10e |

Without these five, Phase 4 fan-out would be parallel workers stomping each other's files, ambiguous merge attribution, and unreproducible orchestration behavior. Build the foundation first.

---

## Slice 10a — Work Unit DAG

Work units gain parent/child relationships, dependency edges, and file scope boundaries. This is the core graph primitive everything else builds on.

### Domain record changes

**`src/NodalMerge.Studio.Contracts/Domain/WorkUnit.cs`**

```csharp
public sealed record WorkUnit(
    string WorkUnitId,
    string Goal,
    string? ParentWorkUnitId,           // null = root
    IReadOnlyList<string> DependsOn,    // WorkUnitIds that must complete first
    IReadOnlyList<string> FileScope,    // files/globs this unit is allowed to touch
    WorkUnitStatus Status,
    string? SuccessCriteria,
    string? RepositoryPath,
    string BranchId,
    string? AssignedAgent,
    DateTimeOffset CreatedAt);
```

`FileScope` is the ownership boundary: workers in parallel must not write outside their declared scope. An empty list means unrestricted (used for root/single-worker runs today).

### DAG store

**`src/NodalMerge.Studio.Storage/WorkUnitService.cs`**
- Add `GetChildrenAsync(string parentId)` — returns immediate children.
- Add `GetDependentsAsync(string workUnitId)` — returns units that list this one as a dependency.
- `CreateAsync` validates: if `ParentWorkUnitId` set, parent must exist.

### Orchestrator changes

**`OrchestratorAgentLoop.cs`**
- Planner output is now parsed for a `plan.json` with explicit slice decomposition (defined in 10b).
- Orchestrator creates child WorkUnits from slices; each child has scope and dependency edges set.
- Current single-worker path remains unchanged: planner produces one slice = one child WorkUnit = existing behavior.

### Success criteria
- Create a work unit; create two children with distinct `FileScope`; query children via `GetChildrenAsync`.
- `GET /studio/workunits/{id}/children` returns the children list.
- Single-worker Quick Spawn still works with no parent/scope set.

---

## Slice 10b — Scheduler / Work Queue

Orchestrator stops driving execution directly. A scheduler polls the work queue, assigns work to available agents, enforces concurrency limits, and expires stale leases.

### Scheduler domain

**`src/NodalMerge.Studio.Core/Services/IWorkScheduler.cs`** (new)

```csharp
public interface IWorkScheduler
{
    Task EnqueueAsync(string workUnitId, string profileId, CancellationToken ct = default);
    Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default);
    Task ReleaseAsync(string workUnitId, bool success, CancellationToken ct = default);
    Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default);
}

public sealed record ScheduledItem(
    string WorkUnitId,
    string ProfileId,
    string? LeasedBy,       // agentId holding the lease
    DateTimeOffset? LeasedAt,
    int AttemptCount);
```

**`src/NodalMerge.Studio.Storage/WorkSchedulerService.cs`** (new)
- Backed by `IStudioNodeStore` at `studio/scheduler/v1`.
- Lease timeout: 5 minutes. `TryAcquireAsync` skips items with valid leases.
- `EnqueueAsync` is idempotent: re-enqueuing an already-pending work unit updates `profileId`, does not duplicate.

### Orchestrator refactor

**`OrchestratorAgentLoop.cs`**
- **Before**: orchestrator decides and immediately spawns workers.
- **After**: orchestrator calls `IWorkScheduler.EnqueueAsync(childId, profileId)` and returns. Scheduler drives execution.
- Orchestrator loop only runs at root scope; worker execution is entirely queue-driven.

### Agent runtime integration

**`InMemoryAgentRuntimeService.cs`**
- Polling loop (interval: configurable, default 2 s) calls `TryAcquireAsync`.
- On acquire: spawn the matching profile's loop, call `ReleaseAsync(success:true)` on completion, `success:false` on failure.
- Concurrency limit: configurable max-concurrent-workers (default: 3).

### REST endpoints

**`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`**
- `GET /studio/scheduler/pending` — pending queue items (used by dashboard).
- `POST /studio/scheduler/enqueue` — manual enqueue for debug.

### Success criteria
- `EnqueueAsync` twice with the same work unit ID does not duplicate.
- Three items queued; max-concurrent = 2; only two agents spawn at once.
- Agent that exceeds lease timeout has its item re-acquired by the next poll.
- Single-worker run still works: orchestrator enqueues one item; scheduler picks it up.

---

## Slice 10c — Workspace Isolation

Each work unit executes in an isolated **Workspace** — an independent filesystem view with its own branch and a disposable lifecycle. The initial implementation backs each workspace with a Git worktree. The domain model does not expose "worktree" directly; that is an implementation detail of the v1 provider.

This distinction matters for Phase 4: if `IsolationType` is baked into every call site, swapping to containerized sandboxes or remote workspaces later requires a refactor. If it stays inside the workspace service, it is a config change.

### Workspace domain record

**`src/NodalMerge.Studio.Contracts/Domain/AgentWorkspace.cs`** (new)

```csharp
public sealed record AgentWorkspace(
    string WorkspaceId,
    string WorkUnitId,
    WorkspaceIsolationType IsolationType,  // Worktree initially
    string BranchName,
    string BaseRevision,
    string Path,
    DateTimeOffset CreatedAt,
    DateTimeOffset? DestroyedAt);          // null while active

public enum WorkspaceIsolationType
{
    Worktree,
}
```

`AgentWorkspace` is a first-class artifact. Once the workspace itself is tracked in the lineage store (10d), it becomes inspectable, diffable, and replayable alongside goals, plans, and proposals.

### Workspace service

**`src/NodalMerge.Studio.Core/Services/IWorkspaceService.cs`** (new)

```csharp
public interface IWorkspaceService
{
    Task<AgentWorkspace> CreateAsync(string workUnitId, string baseBranch, CancellationToken ct = default);
    Task<AgentWorkspace?> GetAsync(string workspaceId, CancellationToken ct = default);
    Task ArchiveAsync(string workspaceId, CancellationToken ct = default);   // success path
    Task DestroyAsync(string workspaceId, CancellationToken ct = default);   // failure / rejection path
    Task<bool> ValidateWriteAsync(string workspaceId, string path, IReadOnlyList<string> fileScope, CancellationToken ct = default);
}
```

**`src/NodalMerge.Studio.Storage/WorkspaceService.cs`** (new)
- `CreateAsync`: creates a Git worktree at `worktrees/{workUnitId}` on a new branch `exec/{workUnitId}` forked from `baseBranch`; stores the `AgentWorkspace` record via `IStudioNodeStore` at `studio/workspace/v1`.
- `ArchiveAsync`: sets `DestroyedAt`; leaves the worktree on disk so the execution branch is preserved as a proposal source (10d / 10f).
- `DestroyAsync`: sets `DestroyedAt`; removes the worktree directory and deletes the branch.
- `ValidateWriteAsync`: glob-matches `path` against `fileScope`; returns `false` (blocked) if scope is non-empty and no glob matches. No-op (`true`) if scope is empty.

The Git worktree operations use `git worktree add` / `git worktree remove` invoked via `IProcessService` (same pattern as existing git calls). No direct coupling to worktree mechanics anywhere except this service.

### Scheduler / spawn integration

**`WorkSchedulerService.cs`**
- On acquire: call `IWorkspaceService.CreateAsync(workUnitId, "main")`.
- Pass the resulting `AgentWorkspace.Path` and `AgentWorkspace.BranchName` to the spawned agent loop via the work unit context.
- On `ReleaseAsync(success:true)`: call `ArchiveAsync` — workspace record preserved, execution branch available for proposal diff.
- On `ReleaseAsync(success:false)`: call `DestroyAsync` — worktree and branch removed.

### Worker loop

**`WorkerAgentLoop.cs`**
- All `file.write` / `file.delete` calls route through `IWorkspaceService.ValidateWriteAsync` before dispatch.
- Blocked write returns a structured tool error to the LLM: `"File {path} is outside your declared scope {fileScope}."`
- Worker operates entirely within `AgentWorkspace.Path`; no cross-workspace file access.

### Lifecycle summary

```
Scheduler acquires WorkUnit
    ↓
IWorkspaceService.CreateAsync  →  AgentWorkspace { DestroyedAt: null }
    ↓
Worker executes in workspace path on workspace branch
    ↓
Success: ArchiveAsync          →  branch preserved as proposal source
Failure: DestroyAsync          →  worktree + branch deleted
```

### Success criteria
- Two workers with non-overlapping `FileScope`; both write their files; neither sees the other's changes — each has its own worktree path and branch.
- Worker attempt to write outside scope receives the structured tool error and does not modify the branch.
- `AgentWorkspace` record stored after `CreateAsync`; `DestroyedAt` populated after `ArchiveAsync` or `DestroyAsync`.
- `WorkspaceIsolationType.Worktree` is the only value in scope; no other types are wired up.
- `git worktree list` during a parallel run shows one worktree per active work unit.

---

## Slice 10d — Artifact Lineage Store

Phase 2.5 slice 9d defined `ArtifactRef` and `ArtifactChain` at the contract and write path level. This slice implements the persistent backing store so the full lineage graph survives across sessions and is queryable for branching, replay, and conflict detection.

### Storage

**`src/NodalMerge.Studio.Storage/StudioNodeKind.cs`**
- Add `public const string ArtifactRefV1 = "studio/artifact-ref/v1";`

**`src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`**
- Add `IArtifactLineageService`:

```csharp
public interface IArtifactLineageService
{
    Task RecordAsync(ArtifactRef artifact, CancellationToken ct = default);
    Task<ArtifactRef?> GetAsync(string artifactId, CancellationToken ct = default);
    Task<IReadOnlyList<ArtifactRef>> GetChainAsync(string workUnitId, CancellationToken ct = default);
    Task<IReadOnlyList<ArtifactRef>> GetChildrenAsync(string parentArtifactId, CancellationToken ct = default);
    Task UpdateStatusAsync(string artifactId, ArtifactStatus status, CancellationToken ct = default);
}
```

**`src/NodalMerge.Studio.Storage/ArtifactLineageService.cs`** (new)
- Backed by `IStudioNodeStore`.
- `GetChainAsync` returns all `ArtifactRef` records owned by the work unit, ordered by `CreatedAt`.

### Write path enforcement

Every service that produces an artifact calls `IArtifactLineageService.RecordAsync`:
- **Work unit creation** → `Type = Goal`, `ParentArtifactId = null`
- **Planner loop** → `Type = Plan`, `ParentArtifactId = goalArtifactId`
- **Task creation** → `Type = Task`, `ParentArtifactId = planArtifactId`
- **Worker file changes** → `Type = BranchChangeset`, `ParentArtifactId = taskArtifactId`
- **`nm.v1.merge.propose`** → `Type = MergeProposal`, `ParentArtifactId = branchChangesetArtifactId`
- **Merge apply** → `Type = MergeResult`, `ParentArtifactId = mergeProposalArtifactId`

### FilesTouched on proposals

**`src/NodalMerge.Studio.Contracts/Domain/MergeProposal.cs`**
- Add `IReadOnlyList<string> FilesTouched` (populated from `workspaceChanges` diff at propose time, parsing `+++ b/` lines).
- This field drives conflict pre-detection in 10b scheduler and merge strategy in Phase 4.

### Conflict pre-detection at enqueue

**`IWorkScheduler.EnqueueAsync`**
- Before enqueuing: query `IArtifactLineageService.GetChainAsync` for the parent work unit.
- Extract all `MergeProposal` refs; resolve their `FilesTouched`.
- Compute overlap with new work unit's `FileScope`.
- If overlap detected: attach `ConflictWarning` to the scheduled item (does not block — merger resolves it in Phase 4).

### REST

- `GET /studio/artifacts/{artifactId}` → single artifact ref
- `GET /studio/workunits/{id}/artifacts` → full chain for work unit
- `GET /studio/artifacts/{artifactId}/children` → direct children

### Success criteria
- Complete a run; `GET /studio/workunits/{id}/artifacts` returns the full chain: `[Goal → Plan → Task → BranchChangeset → MergeProposal]`.
- Each ref has correct `ParentArtifactId`; chain is traversable root-to-leaf.
- `FilesTouched` populated on proposal; two overlapping proposals trigger `ConflictWarning` on the next enqueue.
- `UpdateStatusAsync` on a proposal transitions it from `Active` to `Approved`; reflected in next `GetChainAsync`.

---

## Slice 10e — Orchestration Decision Log

Every routing decision the orchestrator makes is persisted as an immutable log entry. This makes multi-agent orchestration replayable and debuggable without relying on runtime memory.

### Domain record

**`src/NodalMerge.Studio.Contracts/Domain/OrchestrationEvent.cs`** (new)

```csharp
public sealed record OrchestrationEvent(
    string EventId,
    string WorkUnitId,
    string OrchestratorAgentId,
    PipelineStage InputStage,           // stage the artifact chain was at
    string InputProjectionSnapshot,     // serialized AgentWorkspaceProjection at decision time
    OrchestrationAction Action,         // what was decided
    IReadOnlyList<string> SpawnedIds,   // child work unit or agent IDs created
    string? Reason,                     // LLM-provided or heuristic reason tag
    DateTimeOffset OccurredAt);

public enum OrchestrationAction
{
    SpawnPlanner,
    SpawnWorker,
    Enqueue,
    AwaitReview,
    ApplyMerge,
    Escalate,
    NoOp,
}
```

Stored in DAG at `studio/orchestration-event/v1`.

### Orchestrator loop

**`OrchestratorAgentLoop.cs`**
- After every routing decision, write an `OrchestrationEvent` before taking the action.
- `InputProjectionSnapshot` = serialized JSON of the `AgentWorkspaceProjection` at that moment.
- `Reason` = extracted from LLM output if using LLM routing (9e), or a fixed tag for heuristic routing.

### REST endpoint

**`GET /studio/workunits/{id}/orchestration-events`** — ordered list of events for a work unit.

### DAG replay panel

Add an "Orchestration Log" expandable section per work unit node: shows the event sequence with timestamps, input stage, and action taken. This is the debugger for multi-agent divergence.

### Success criteria
- Quick Spawn a goal; after completion, `GET /studio/workunits/{id}/orchestration-events` returns at least one event per routing decision.
- Each event has a non-null `InputProjectionSnapshot`.
- DAG replay panel shows the event log for a completed work unit.
- Replaying events in order reproduces the same agent spawn sequence.

---

## Slice 10f — Proposal DAG & Artifact Branching

This is the strategic inflection point. After this slice, the answer to "what can I do after a run completes?" stops being "look at logs" and becomes: inspect, branch, replay, and compare every artifact.

The data model is a DAG of workspace states connected by proposals:

```
S0 (base state)
 ├── Proposal A (worker-1, profile-X) → S1a
 └── Proposal B (worker-2, profile-Y) → S1b

Merger Proposal C (reconciles A + B) → S2
```

### Workspace primitive

**`src/NodalMerge.Studio.Core/Services/IWorkspaceService.cs`** (from 10c)
- Add `CreateFromProposalBaseAsync(string proposalId, string newWorkUnitId)` — creates a new `AgentWorkspace` at the exact state that existed *before* the proposal's changes were applied. This is `S0` in the model above.

The base state is the proposal's source workspace branch at the time `merge.propose` was called. Since 10c archives (not destroys) the workspace on success, the execution branch is still present and can be snapshotted here.

### Artifact branching API

**`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`**
- `POST /studio/proposals/{id}/branch` — creates a new root work unit starting from this proposal's base state.
  - Body: `{ "goal": "...", "profileId": "..." }`
  - Creates a child work unit with the proposal's base workspace as its starting point.
  - Returns the new work unit ID.

This is the "checkout S0, try with a different model" operation. The new work unit runs through the scheduler (10b) like any other.

### Proposal comparison API

**`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`**
- `GET /studio/proposals/compare?ids=A,B` — returns a side-by-side diff of two proposals that share the same base state.
  - Response: `{ proposalId: "A", proposalId: "B", overlappingFiles: [...], diffA: "...", diffB: "..." }`
  - Only valid when both proposals have the same parent work unit (same S0).

### Lineage query

**`GET /studio/workunits/{id}/proposal-dag`** — returns the full proposal DAG for a work unit:
```json
{
  "workUnitId": "...",
  "baseState": "exec/{workUnitId}",
  "proposals": [
    { "proposalId": "A", "status": "Approved", "origin": { ... }, "producedState": "exec/A" },
    { "proposalId": "B", "status": "Superseded", "origin": { ... }, "producedState": "exec/B" }
  ],
  "mergeProposal": { "proposalId": "C", "reconciledFrom": ["A", "B"] }
}
```

### Merge Review panel

- Add "Branch from here" button on any proposal card: calls `POST /studio/proposals/{id}/branch`, opens a goal input modal, then opens the new work unit in the DAG replay panel.
- Add "Compare with..." button: shows a proposal picker (filtered to same-parent proposals), then displays the side-by-side diff from the comparison API.

### Success criteria
- Complete a run; `GET /studio/workunits/{id}/proposal-dag` returns the full proposal DAG with base state and produced states.
- "Branch from here" on a proposal: new work unit created with correct base state; agent runs on that state.
- Two competing proposals from the same base: "Compare with..." shows overlapping vs. non-overlapping files.
- Branched work unit run completes successfully; its proposal shows in the parent work unit's proposal DAG.

---

## Slice 10f.5 — Intent Graph & Conflict Resolver

This slice introduces a pre-execution coordination primitive that prevents uncontrolled concurrent edits to overlapping semantic regions. It shifts the model from "agents edit files" to "agents publish change intents" and the system decides safe parallelism before any workspace write occurs.

### Motivation
Fan-out without intent coordination leads to race conditions, wasted recomputation, nondeterministic outputs, and merge storms. The right correctness frontier is not a better merger — it's preventing overlapping semantic edits or containing them with scheduling and revalidation.

### Key concepts
- **Change Intent**: a structured declaration of intent produced by the planner, e.g. `{ intent: "modify", target: "Foo.cs", region: "method:CalculateTax", type: "semantic_patch", baseSnapshot: "hash" }`.
- **Intent Graph**: a per-work-unit index of all intents (local and enqueued) used to detect overlaps before execution.
- **Conflict Warning / Lock**: scheduler metadata indicating overlapping intents; used to serialize or reroute execution.

### Domain additions
**`src/NodalMerge.Studio.Contracts/Domain/ChangeIntent.cs`** (new)

```csharp
public sealed record ChangeIntent(
    string IntentId,
    string WorkUnitId,
    string IntentType,         // modify | create | delete | rename
    string TargetPath,         // file path or logical id
    string RegionDescriptor,   // lines, AST node id, or semantic tag
    string BaseSnapshotHash,   // snapshot hash used for optimistic strategies
    IReadOnlyList<string>? FilesTouchedHint,
    DateTimeOffset CreatedAt);
```

**`src/NodalMerge.Studio.Core/Services/IIntentGraphService.cs`** (new)

```csharp
public interface IIntentGraphService
{
    Task RecordIntentAsync(ChangeIntent intent, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeIntent>> QueryIntentsAsync(string workUnitId, CancellationToken ct = default);
    Task<IReadOnlyList<ChangeIntent>> QueryOverlappingAsync(ChangeIntent intent, CancellationToken ct = default);
    Task RemoveIntentAsync(string intentId, CancellationToken ct = default);
}
```

### Scheduler integration
- On planner output, the orchestrator produces `ChangeIntent` entries instead of raw file edits.
- `IWorkScheduler.EnqueueAsync` consults `IIntentGraphService.QueryOverlappingAsync` to build a conflict map at enqueue time.
- If overlaps exist the scheduler can: attach a `ConflictWarning` to the scheduled item, acquire region locks, serialize execution, or signal the planner for intent refinement.

### Scheduling strategies
- **Option A — Strict partitioning**: detected overlap → do not run in parallel; enqueue preserves ordering. Simplest and deterministic.
- **Option B — Region locking (recommended default)**: scheduler acquires region locks (e.g., `lock(Foo.cs:method:CalculateTax)`); competing intents are queued or rerouted. Balances safety and parallelism.
- **Option C — Optimistic execution**: allow execution with `BaseSnapshotHash`, validate after run; on divergence, rebase or re-run agent. Maximizes parallelism at cost of recomputation.

### Planner / Agent revalidation loop
- If the scheduler rejects or queues an intent due to conflict, the planner/agent receives a structured revalidation request with the updated projection and the overlapping region metadata.
- The agent can: refine intent (narrow region), split work into smaller intents, rebase against the latest snapshot, or accept serialization.

### Relationship to CRDT / RGA
- RGA/CRDTs remain the execution layer for composing concurrent text or artifact changes, not the coordination layer. The Intent Graph ensures coordination decisions are made before RGA patch application.

### Artifact Lineage & UI signals
- Record `ChangeIntent` artifacts via `IArtifactLineageService.RecordAsync` so intent history is queryable and replayable.
- Expose `GET /studio/workunits/{id}/intents` and `GET /studio/scheduler/pending?includeIntentGraph=1` for dashboard and pre-execution visualization.
- In the DAG UI, show overlapping intents as a conflict overlay with buttons: "Queue", "Branch & Replan", "Refine Intent".

### Success criteria
- Planner outputs `ChangeIntent` records for a generated plan; `GET /studio/workunits/{id}/intents` returns them.
- Enqueueing a work unit with overlapping intents produces a `ConflictWarning` and either region locks or a queued item depending on scheduler config.
- Two workers with non-overlapping intents run in parallel; overlapping intents are serialized or cause replanning according to selected strategy.
- Revalidation loop: when an intent is queued due to overlap, the agent receives a structured revalidation message and produces a refined intent that can be enqueued and executed.

This slice completes the pre-execution conflict detection layer that Phase 3 has been missing. With `10f.5` in place, Phase 4 fan-out becomes safe, deterministic, and tractable for real distributed workloads.

---

## Slice 10g — Knowledge Artifacts

Agents currently rediscover context on every run. A `Research` artifact from run 1 that says "the codebase targets .NET 8 and has no Redis dependency" is thrown away — run 2 re-discovers it. A `Constraint` that says "auth middleware must not store session tokens" exists only in the LLM's context window, not in the workspace.

Knowledge artifacts close this gap. They make discovered facts, architectural decisions, and invariant constraints durable, queryable, and automatically inherited by descendant work units.

### New MCP tools

**`src/NodalMerge.Studio.McpServer/Tools/ArtifactTools.cs`** (new)

`nm.v1.artifact.record`
- Parameters: `workUnitId`, `type` (`Research | Decision | Constraint`), `title`, `body`, `parentArtifactId?`
- Writes an `ArtifactRef` via `IArtifactLineageService.RecordAsync`.
- Returns the new `artifactId`.

`nm.v1.artifact.query`
- Parameters: `workUnitId`, `type?`, `keywords?`
- Returns all matching `ArtifactRef` records from `GetChainAsync`, filtered by type and basic keyword match on `title + body`.
- Walks up the `ParentWorkUnitId` chain to include inherited artifacts (primarily `Constraint` type).

`nm.v1.artifact.list`
- Parameters: `workUnitId`, `includeAncestors?` (default: true for Constraint type)
- Returns the full chain including inherited knowledge.

### Storage

Knowledge artifacts use the same `IArtifactLineageService.RecordAsync` path as execution artifacts. No new storage schema needed — they are `ArtifactRef` records with `Type = Research | Decision | Constraint`.

Body content is stored as a plain string (markdown). No structured schema enforcement in Phase 3; that is Phase 6 (Policy/Validator layer).

### Profile update

**Default `worker` profile AllowedTools** (from 9b):
- Add `artifact.record`, `artifact.query`, `artifact.list`.

**Default `planner` profile AllowedTools**:
- Add `artifact.record`, `artifact.query`, `artifact.list`.

This allows planners to record `Research` and `Decision` artifacts during the planning phase, and workers to record `Research` and `Constraint` artifacts during execution.

### Projection integration

**`AgentWorkspaceProjection`** (from 9d):
- `InheritedConstraints` accessor already defined in 9d — this slice implements the population logic in the projection builder.
- Walk up `ParentWorkUnitId` chain; collect all `Type == Constraint` artifacts; inject into projection.
- Agent's turn-0 context includes: "The following constraints apply to all work in this session: [...]"

### Success criteria
- Worker records a `Research` artifact ("Codebase uses .NET 8; no Redis present"); it appears in `GET /studio/workunits/{id}/artifacts` with `Type = Research`.
- New child work unit of the same parent: `AgentWorkspaceProjection` includes the parent's `Research` artifact in the chain.
- Worker records a `Constraint`; grandchild work unit's projection includes it in `InheritedConstraints` (ancestor walk works across two levels).
- `nm.v1.artifact.query` with `type: "Constraint"` returns constraints from the work unit and its ancestors.
- Planner records a `Decision` ("Use event sourcing for audit log"); `nm.v1.artifact.query` in a subsequent worker run returns it.

---

## Slice ordering

10a → 10b → 10c → 10d → 10e → 10f → 10g

- **10a first**: the DAG is what scheduler, isolation, and lineage all reference.
- **10b before 10c**: scheduler drives workspace creation on acquire and workspace teardown on release; isolation lifecycle is owned by the scheduler.
- **10c before 10d**: artifact lineage includes the `AgentWorkspace` record; the workspace must exist before it can be recorded in the lineage store.
- **10d before 10e**: orchestration events record which spawned IDs were created; attribution requires 10d.
- **10e before 10f**: branching needs the decision log to attribute the branch event.
- **10f before 10g**: knowledge artifacts need the lineage store (10d) and the ancestor walk from the Work Unit DAG (10a); the proposal DAG (10f) establishes that the lineage model is complete before knowledge is layered on top.
- **10g last**: knowledge artifacts are the highest-value slice — they reduce LLM work across runs. They must be added before Phase 4 fan-out or parallel workers will each rediscover the same things independently.

---

## Files not touched in Phase 3

| File | Reason |
|------|--------|
| `LlmClient.cs` | Provider abstraction complete |
| `InMemoryMergeService.cs` | Write-back already fixed in 2.5 |
| Phase 2.5 profiles | Additive only — scheduler reads existing profile IDs |
| `AgentConfigPanel.ts` | New scheduler endpoints appear in dashboard; no config panel changes needed |
| Fan-out logic | Phase 4 — now that the foundations exist, fan-out is straightforward |

---

## Phase 4 pointer

After Phase 3, the system has a correct, isolated, attributable, replayable, branchable artifact platform with durable knowledge. The answer to "what can I do after a run?" is now: inspect, branch, replay, compare, and reuse prior knowledge. Phase 4 makes it genuinely parallel:

- Fan-out: planner decomposes goal → N child work units → N scheduler entries → N isolated workers in parallel
- Artifact lifecycle state machine: formal states per artifact replacing the implicit status flags
- Merger/Reducer: N proposals → 1 reconciled candidate using `FilesTouched` conflict map from 10d
- Automated reviewer agent (Stage = Review) as optional pre-gate
- Dead-letter escalation using the lifecycle state machine

Knowledge artifacts (10g) pay off here: parallel workers each query `nm.v1.artifact.query` at turn-0 and inherit the same constraints — they don't rediscover independently.
