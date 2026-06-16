# Phase 3 — Multi-Agent Foundations

Phase 2.5 delivers a correct single-worker pipeline. Phase 3 adds the foundational systems that make parallelism safe and deterministic — eleven slices in the end, not the five originally scoped, as control-plane backbone work (10b.1–10b.4) and conflict coordination (10f.5) grew the list. **All eleven are now complete** (see Progress below). Fan-out and multi-agent behavior arrive in Phase 4, but only because these primitives exist.

> "You are not missing more agent types — you are missing control-plane infrastructure."

---

## Progress

- [x] 10a — Work Unit DAG
- [x] 10b — Scheduler / Work Queue
- [x] 10b.1 — ExecutionSession Boundary
- [x] 10b.2 — Unified Execution Event Stream
- [x] 10b.3 — Control-Plane Idempotency
- [x] 10b.4 — State Reconstruction API
- [x] 10c — Workspace Isolation (see implementation note in that section — backed by the existing branch/file-copy isolation, not a real git worktree)
- [x] 10d — Artifact Lineage Store (see implementation note in that section — only Goal/Task/MergeProposal/MergeResult are wired; Plan/BranchChangeset have no producer yet)
- [x] 10e — Orchestration Decision Log (see implementation note — also wires the dormant 10b.2 `OrchestrationDecision` event hook; UI panel deferred, see tracker below)
- [x] 10f — Proposal DAG & Artifact Branching (see implementation note — base-state snapshot at propose time, branching via existing `seedFromBranchId`/`Metadata`, no new workspace service method needed)
- [x] 10f.5 — Intent Graph & Conflict Resolver (see implementation note — only Option A strict-partitioning is built; region locking, optimistic execution, and the revalidation loop need real concurrent execution from Phase 4)
- [x] 10g — Knowledge Artifacts (see implementation note — `Title`/`Body` added to `ArtifactRef`, `InheritedConstraints` and ancestor-walk are genuinely new, not pre-existing as the plan claimed)

---

## Deferred Work Tracker

Every as-built note above documents a deviation from the original spec. Most are permanent (the spec was wrong or aspirational). Items that had a specific *future* trigger point have been moved directly into the plan for the slice that resolves them — see `plans/phase-4-fanout-merger.md`'s "Carried over from Phase 3" subsections in Slices 11b, 11c, and 11e — rather than living here as a second list to cross-check. Only items with **no fixed slice** (truly open-ended, or already actionable with nothing left to wait for) remain in this table.

| Item | Introduced in | Currently | Status |
|------|----------------|-----------|--------|
| `OrchestrationAction.AwaitReview` | 10b.2 (defined) / 10e (still unreachable) | ✅ **Resolved** (Phase 4 kickoff) — emitted | `OrchestratorAgentLoop.RunAsync`'s `end_turn` branch now checks the artifact chain for a `MergeProposal` ref with `Status == ArtifactStatus.Active` (the only "not yet decided" status) and logs `AwaitReview` instead of `NoOp` when found — built alongside the re-invocation fix below since both touch the same `end_turn` decision point. |
| Orchestrator re-invocation after worker completion | Implied by `OrchestratorAgentLoop`'s own system prompt ("The system will re-invoke you after the worker completes") | ✅ **Resolved** (Phase 4 kickoff) — see [phase-4-fanout-merger.md](./phase-4-fanout-merger.md)'s Slice 11a as-built note | `IAgentControlService.ReinvokeOrchestratorAsync` (new), called from `WorkSchedulerService.ReleaseAsync` on both success and failure via the existing lazy `_serviceProvider` lookup. `InMemoryAgentRuntimeService` registers each spawned orchestrator's credentials/profile keyed by `workUnitId` at `SpawnAsync` time; re-invocation is a no-op if none was registered (e.g. work enqueued directly via the debug endpoint). **Known accepted gap**: re-invoking on failure as well as success means a persistently-failing worker can cause unbounded re-invocation (no double-execution — `EnqueueAsync`/`TryAcquireAsync` are already idempotent/CAS-protected — but no backstop either). Deferred to 11e's dead-letter/attempt-count escalation, not fixed here. Tests: `InMemoryAgentRuntimeServiceTests.cs`, `ControlPlaneIdempotencyTests.cs`, `SchedulerReinvocationTests.cs` (new — proves convergence end-to-end via the real scheduler queue, not just the legacy direct-spawn path `FullAgentCycleTests.cs` exercises). |
| "DAG replay panel" / "Orchestration Log" / "Merge Review panel" / intent conflict overlay / knowledge artifact browser UI work | 10e, 10f, 10f.5, 10g (and implicitly every slice since 10b.2 that added a debugger-relevant REST endpoint) | **Not implemented — and ready to start any time.** No fixed slice trigger — Phase 3's backend has been complete since before Phase 4 started, so this isn't waiting on anything else to land. | `clients/web-dashboard` is an empty placeholder; `clients/vscode-extension/src/panels/DagReplayPanel.ts` exists but predates Phase 3 — it visualizes the older NodalMerge DAG, not `ExecutionEvent`/`ArtifactRef`/`OrchestrationEvent`/`ChangeIntent`. Bundle the event stream (10b.2), state reconstruction (10b.4), workspace isolation (10c), artifact lineage + knowledge artifacts (10d/10g), orchestration log (10e), proposal branching/comparison (10f), and intent graph (10f.5) REST endpoints into one panel redesign rather than rebuilding the webview eight separate times. |

---

## What Phase 3 adds (and why order matters)

| Gap | System | Slice |
|-----|--------|-------|
| Work is implicit state in a running loop | Work Unit DAG (parent/child, deps, scope) | 10a |
| Orchestrator drives logic; no global queue | Scheduler / Work Queue | 10b |
| No session boundary; runs are anonymous and not resumable | ExecutionSession boundary | 10b.1 |
| Events from scheduler, orchestrator, and artifacts are unrelated streams | Unified Execution Event Stream | 10b.2 |
| Scheduler retries can duplicate control-plane actions | Control-plane idempotency | 10b.3 |
| No way to reconstruct system state at a point in time | State reconstruction API | 10b.4 |
| Workers share file state; no ownership boundaries | Workspace Isolation | 10c |
| Proposals are floating artifacts with no attribution | Artifact Ownership + Lineage | 10d |
| Orchestration decisions are runtime heuristic; not replayable | Orchestration Decision Log | 10e |

Without these nine, Phase 4 fan-out would be parallel workers stomping each other's files, ambiguous merge attribution, and unreproducible orchestration behavior. The four control-plane backbone slices (10b.1–10b.4) are the missing connective tissue: they give every event a session identity, a causal home, and a safe retry story before branching and lineage are layered on top.

---

## Slice 10a — Work Unit DAG ✅

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

## Slice 10b — Scheduler / Work Queue ✅

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

## Slice 10b.1 — ExecutionSession Boundary ✅

A session is the outermost causal unit. Every scheduler event, orchestration decision, artifact, and workspace belongs to exactly one session. Sessions can be paused, resumed, branched (spawning a child session from any point in the causal history), and replayed in the UI. Without a session boundary every run is anonymous: there is no safe way to reconnect to in-progress work after a restart, resume a paused run, or let a user spin up a second independent session without the two colliding.

### Domain record

**`src/NodalMerge.Studio.Contracts/Domain/ExecutionSession.cs`** (new)

```csharp
public sealed record ExecutionSession(
    string SessionId,
    string RootWorkUnitId,
    ExecutionSessionStatus Status,        // Active | Paused | Completed | Abandoned
    string? ParentSessionId,              // non-null when branched from another session
    string? ParentEventId,                // the event in the parent session from which this was branched
    DateTimeOffset StartedAt,
    DateTimeOffset? PausedAt,
    DateTimeOffset? CompletedAt,
    string ModelConfigSnapshotJson,       // serialized model/provider config at session start
    IReadOnlyList<string> ProfileIdSet);  // profile IDs active in this session

public enum ExecutionSessionStatus { Active, Paused, Completed, Abandoned }
```

`ParentSessionId` + `ParentEventId` enable the "come back later and add on" workflow: branch a new session from any point in the parent's event history without touching the parent's state.

### Session service

**`src/NodalMerge.Studio.Core/Services/IExecutionSessionService.cs`** (new)

```csharp
public interface IExecutionSessionService
{
    Task<ExecutionSession> CreateAsync(string rootWorkUnitId, string modelConfigJson,
        IReadOnlyList<string> profileIds, string? parentSessionId = null,
        string? parentEventId = null, CancellationToken ct = default);
    Task<ExecutionSession?> GetAsync(string sessionId, CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionSession>> ListAsync(CancellationToken ct = default);
    Task SetStatusAsync(string sessionId, ExecutionSessionStatus status, CancellationToken ct = default);
}
```

**`src/NodalMerge.Studio.Storage/ExecutionSessionService.cs`** (new)
- Backed by `IStudioNodeStore` at `studio/execution-session/v1`.
- `CreateAsync` persists the record immediately; no in-memory-only state.
- `ListAsync` returns all sessions ordered by `StartedAt` descending.

### Durability guarantee

`ExecutionSession` is written to `IStudioNodeStore` before any scheduler, orchestrator, or workspace action is taken for that session. If the process crashes after session creation, the session record survives and the scheduler can resume from the last event in that session's stream (10b.2). On restart, `ListAsync` surfaces `Active` sessions; the UI or host can reconnect.

### Propagation

- `IWorkScheduler.EnqueueAsync` gains a `sessionId` parameter; the scheduler attaches it to every `ScheduledItem`.
- `OrchestratorAgentLoop` receives the `sessionId` at construction; every decision it makes includes it.
- REST API propagates `sessionId` in headers (or body) for all session-scoped requests.

### REST endpoints

- `POST /studio/sessions` — start a new session (`{ rootGoal, profileId, modelConfig? }`)
- `GET /studio/sessions` — list all sessions
- `GET /studio/sessions/{id}` — single session detail
- `POST /studio/sessions/{id}/pause` — sets `Status = Paused`
- `POST /studio/sessions/{id}/resume` — sets `Status = Active`, triggers scheduler to re-check pending items
- `POST /studio/sessions/{id}/branch` — create a child session branching from `parentEventId`

### Success criteria
- Create a session; crash the process; restart; `GET /studio/sessions` returns the session with `Status = Active`.
- Pause a session; no new work items are acquired by the scheduler for that session while paused; resume resumes acquisition.
- Branch a session from a specific `parentEventId`; child session has `ParentSessionId` and `ParentEventId` set; child runs independently.
- Two sessions exist simultaneously; their events, work units, and workspaces do not intermingle.

---

## Slice 10b.2 — Unified Execution Event Stream ✅

Currently the scheduler, orchestrator, artifact store, and workspace service each write to separate node stores with no shared identity. Events from different subsystems cannot be ordered, correlated, or replayed relative to each other. The unified execution event stream gives every significant state change a single causal home: a session-scoped, append-only stream where events are ordered, attributed, and immutable.

This is the Temporal replacement. It is NOT full event sourcing — it does not need academic purity. It needs enough causal fidelity to: debug divergence, reconstruct state at a point in time (10b.4), and support branching (10f).

### Domain record

**`src/NodalMerge.Studio.Contracts/Domain/ExecutionEvent.cs`** (new)

```csharp
public sealed record ExecutionEvent(
    string EventId,           // GUID, supplied by caller for idempotency (10b.3)
    string SessionId,
    string? WorkUnitId,       // null for session-level events
    ExecutionEventKind Kind,
    string PayloadJson,       // typed per Kind — see payload records below
    string? CausedByEventId,  // parent event in causal chain (null = root)
    DateTimeOffset OccurredAt);

public enum ExecutionEventKind
{
    // Session lifecycle
    SessionStarted,
    SessionPaused,
    SessionResumed,
    SessionBranchCreated,

    // Work unit lifecycle
    WorkUnitCreated,
    WorkUnitScheduled,        // accepted into scheduler queue
    WorkUnitStarted,          // lease acquired; execution beginning
    WorkUnitCompleted,        // released with success:true
    WorkUnitFailed,           // released with success:false
    WorkUnitAbandoned,        // explicitly cancelled

    // Scheduler internals
    SchedulerLeaseAcquired,
    SchedulerLeaseReleased,
    SchedulerLeaseExpired,    // lease timed out; item returned to queue

    // Workspace
    WorkspaceCreated,
    WorkspaceBranchCreated,   // branched from an existing workspace state
    WorkspaceArchived,
    WorkspaceDestroyed,

    // Artifact lifecycle
    ArtifactRecorded,         // generic knowledge/research/decision artifact
    ArtifactProposed,         // merge proposal specifically — carries FilesTouched
    ArtifactStatusChanged,

    // Proposal lifecycle (separate from MergeApplied — approval and application are distinct steps)
    ProposalApproved,
    ProposalRejected,
    ProposalSuperseded,

    // Merge lifecycle
    MergeApproved,            // proposal passed human or agent review gate
    MergeApplied,             // changes written to target branch; commit hash known

    // Orchestration
    OrchestrationDecision,
    ConflictDetected,         // file/region overlap found at enqueue
}
```

Each `Kind` has exactly one payload record. `PayloadJson` serializes that record. Reconstruction logic (10b.4) switches on `Kind` and deserializes to the correct type — no guessing.

**`src/NodalMerge.Studio.Contracts/Domain/ExecutionEventPayloads.cs`** (new)

```csharp
// Session
public sealed record SessionStartedPayload(
    string SessionId, IReadOnlyList<string> ProfileIds, string ModelConfigSnapshotJson);

public sealed record SessionBranchCreatedPayload(
    string ChildSessionId, string ParentSessionId, string ParentEventId);

// Work unit
public sealed record WorkUnitScheduledPayload(
    string WorkUnitId, string ProfileId, int AttemptCount);

public sealed record WorkUnitStartedPayload(
    string WorkUnitId, string AgentId);

public sealed record WorkUnitCompletedPayload(
    string WorkUnitId, string AgentId, string? ProducedProposalId);

public sealed record WorkUnitFailedPayload(
    string WorkUnitId, string AgentId, string FailureReason);

// Scheduler
public sealed record SchedulerLeaseAcquiredPayload(
    string WorkUnitId, string AgentId, DateTimeOffset ExpiresAt);

public sealed record SchedulerLeaseReleasedPayload(
    string WorkUnitId, string AgentId, bool Success);

public sealed record SchedulerLeaseExpiredPayload(
    string WorkUnitId, string PriorAgentId);

// Workspace
public sealed record WorkspaceCreatedPayload(
    string WorkspaceId, string WorkUnitId, string BranchName, string BaseBranch);

public sealed record WorkspaceBranchCreatedPayload(
    string NewWorkspaceId, string SourceWorkspaceId, string SourceEventId);

public sealed record WorkspaceArchivedPayload(
    string WorkspaceId, string ExecutionBranch);

public sealed record WorkspaceDestroyedPayload(
    string WorkspaceId, string Reason);

// Artifacts
public sealed record ArtifactProposedPayload(
    string ArtifactId, string WorkUnitId, IReadOnlyList<string> FilesTouched);

public sealed record ArtifactStatusChangedPayload(
    string ArtifactId, ArtifactStatus PreviousStatus, ArtifactStatus NewStatus);

// Proposal lifecycle
public sealed record ProposalApprovedPayload(
    string ProposalId, string ApprovedBy);   // agentId or "user:{email}"

public sealed record ProposalRejectedPayload(
    string ProposalId, string RejectedBy, string? Reason);

// Merge
public sealed record MergeApprovedPayload(
    string ProposalId, string ApprovedBy, DateTimeOffset ApprovedAt);

public sealed record MergeAppliedPayload(
    string ProposalId, string TargetBranch, string ResultCommitHash);

// Orchestration
public sealed record OrchestrationDecisionPayload(
    string OrchestratorAgentId, OrchestrationAction Action,
    IReadOnlyList<string> SpawnedIds, string? Reason);

public sealed record ConflictDetectedPayload(
    string WorkUnitId, IReadOnlyList<string> OverlappingFiles,
    IReadOnlyList<string> ConflictingWorkUnitIds);
```

`CausedByEventId` forms the causal chain: every event knows which prior event caused it. This is enough for lightweight replay (10b.4) and for UI timeline scrubbing without a full event-sourcing engine.

### Event stream service

**`src/NodalMerge.Studio.Core/Services/IExecutionEventStream.cs`** (new)

```csharp
public interface IExecutionEventStream
{
    Task<ExecutionEvent> AppendAsync<T>(string sessionId, string? workUnitId,
        ExecutionEventKind kind, T payload, string? causedByEventId = null,
        string? eventId = null,   // supply for idempotent re-drive; omit for new events
        CancellationToken ct = default);
    Task<IReadOnlyList<ExecutionEvent>> GetSessionEventsAsync(string sessionId,
        DateTimeOffset? since = null, CancellationToken ct = default);
    Task<ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default);
}
```

The generic `T` constrains callers to pass the correct payload type per kind. The implementation serializes `payload` to `PayloadJson`. Callers never pass raw `object` or anonymous types.

**`src/NodalMerge.Studio.Storage/ExecutionEventStreamService.cs`** (new)
- Backed by `IStudioNodeStore` at `studio/execution-event/v1`.
- Append-only: no update or delete operations. New entries only.
- `AppendAsync` sets `OccurredAt = DateTimeOffset.UtcNow`; caller cannot override it (prevents clock skew from corrupting causal ordering).
- If `eventId` is supplied and already exists in the store, the existing record is returned without a second write (idempotency, 10b.3).
- `GetSessionEventsAsync` returns events ordered by `OccurredAt`.

### Write path integration

Each existing subsystem appends to the stream **in addition to** its own store — the stream does not replace local stores; it references them by ID:

| Caller | Kind | Payload type |
|--------|------|--------------|
| `IExecutionSessionService.CreateAsync` | `SessionStarted` | `SessionStartedPayload` |
| `IWorkScheduler.EnqueueAsync` | `WorkUnitScheduled` | `WorkUnitScheduledPayload` |
| `IWorkScheduler.TryAcquireAsync` | `SchedulerLeaseAcquired` | `SchedulerLeaseAcquiredPayload` |
| agent loop start | `WorkUnitStarted` | `WorkUnitStartedPayload` |
| `IWorkScheduler.ReleaseAsync(success:true)` | `WorkUnitCompleted` + `SchedulerLeaseReleased` | respective payloads |
| `IWorkScheduler.ReleaseAsync(success:false)` | `WorkUnitFailed` + `SchedulerLeaseReleased` | respective payloads |
| lease timeout reaper | `SchedulerLeaseExpired` | `SchedulerLeaseExpiredPayload` |
| `IWorkspaceService.CreateAsync` | `WorkspaceCreated` | `WorkspaceCreatedPayload` |
| `IWorkspaceService.ArchiveAsync` | `WorkspaceArchived` | `WorkspaceArchivedPayload` |
| `nm.v1.merge.propose` | `ArtifactProposed` | `ArtifactProposedPayload` |
| reviewer approval | `ProposalApproved` → `MergeApproved` | `ProposalApprovedPayload`, `MergeApprovedPayload` |
| merge write-back | `MergeApplied` | `MergeAppliedPayload` |
| `OrchestratorAgentLoop` decision | `OrchestrationDecision` | `OrchestrationDecisionPayload` |
| scheduler overlap check | `ConflictDetected` | `ConflictDetectedPayload` |

### REST

- `GET /studio/sessions/{id}/events` — full event stream for a session, ordered chronologically
- `GET /studio/events/{eventId}` — single event detail with payload

### Success criteria
- Complete a Quick Spawn run; `GET /studio/sessions/{id}/events` returns events in causal order covering the full domain sequence: `SessionStarted → WorkUnitCreated → WorkUnitScheduled → SchedulerLeaseAcquired → WorkUnitStarted → WorkspaceCreated → ArtifactProposed → WorkspaceArchived → SchedulerLeaseReleased → WorkUnitCompleted`.
- Each event deserializes to its typed payload record without error; no event has an `ArtifactRecorded` kind where `ArtifactProposed` is the correct kind.
- Each event has a non-null `CausedByEventId` except the session root.
- `AppendAsync` with a previously used `eventId` returns the existing record; `GetSessionEventsAsync` shows one entry for that ID.
- Stream survives process restart; events are readable immediately after restart from `IStudioNodeStore`.

---

## Slice 10b.3 — Control-Plane Idempotency ✅

The scheduler's polling loop and retry logic will inevitably duplicate control-plane signals. Without idempotency, a retry that re-enqueues a work unit creates a second scheduler entry; a duplicate `merge.propose` creates two proposals; a re-spawned orchestrator fires two `LeaseAcquired` events. None of these are LLM correctness problems — they are distributed systems correctness problems.

Idempotency is scoped to **control-plane actions only**. It does not apply to LLM outputs (which are intentionally non-deterministic), to artifact content (which is hash-addressed), or to workspace writes (which are branch-isolated).

### Control actions requiring idempotency

| Action | Idempotency key |
|--------|-----------------|
| `IWorkScheduler.EnqueueAsync` | `SessionId + WorkUnitId` |
| `IWorkScheduler.TryAcquireAsync` | `WorkUnitId + AgentId` (re-acquire returns existing lease) |
| `IWorkspaceService.CreateAsync` | `WorkUnitId` (second call returns existing workspace) |
| `IArtifactLineageService.RecordAsync` | `ArtifactId` (second call is no-op if id matches) |
| `IExecutionEventStream.AppendAsync` | `EventId` (supplied by caller for re-drive scenarios) |
| `POST /studio/proposals` (MCP tool) | `CommandId` header (GUID per proposal intent) |

### Implementation pattern

Each idempotent service method:
1. Derives or accepts an idempotency key.
2. Checks `IStudioNodeStore` for an existing record at that key before writing.
3. If found: returns the existing record unchanged (no error, no side effects).
4. If not found: writes the record, then returns it.

No global idempotency table is needed — each service owns its own key space. The key is always derivable from domain identity (work unit ID, artifact ID, etc.), not from a separate opaque token, so callers do not need to manage nonce storage.

### CommandId for MCP tools

MCP tool calls that trigger control-plane actions (`nm.v1.merge.propose`, future `nm.v1.work.spawn`) receive a `CommandId` (GUID) from the caller. The server stores `CommandId → result` in `IStudioNodeStore` at `studio/command-result/v1`. If the same `CommandId` arrives again, the stored result is returned immediately without re-executing. TTL: 24 hours (sufficient for any retry window).

### Success criteria
- `EnqueueAsync` called twice with the same `SessionId + WorkUnitId`: second call returns the existing `ScheduledItem`; `ListPendingAsync` shows one entry, not two.
- Process crash during workspace creation; restart re-calls `CreateAsync(workUnitId)`; existing workspace is returned; no second worktree is created on disk.
- `nm.v1.merge.propose` called twice with the same `CommandId`: second response is identical to the first; only one `MergeProposal` record exists.
- `IExecutionEventStream.AppendAsync` called with a previously used `EventId`: second call is a no-op; stream has one entry for that ID.

---

## Slice 10b.4 — State Reconstruction API ✅

Not deterministic replay of agent cognition. Just:

> "Given a session and an event, what was the system state immediately after that event?"

This is used for: debugging multi-agent divergence, UI timeline scrubbing (the "back in time" slider), creating a workspace at an exact past state (foundation for 10f branching), and incident analysis after a failed run.

The implementation is lightweight — it does not re-execute anything. It queries the event stream and resolves the referenced records at their stored state.

### Service

**`src/NodalMerge.Studio.Core/Services/IStateReconstructionService.cs`** (new)

```csharp
public interface IStateReconstructionService
{
    Task<SessionStateSnapshot> GetStateAtAsync(string sessionId, string upToEventId,
        CancellationToken ct = default);
    Task<SessionStateSnapshot> GetStateAtTimeAsync(string sessionId, DateTimeOffset asOf,
        CancellationToken ct = default);
}

public sealed record SessionStateSnapshot(
    string SessionId,
    string BoundaryEventId,        // the last event included
    DateTimeOffset BoundaryTime,
    IReadOnlyList<string> ActiveWorkUnitIds,
    IReadOnlyList<string> ActiveWorkspaceIds,
    IReadOnlyList<string> ArtifactIds,
    IReadOnlyList<string> CompletedEventIds);
```

**`src/NodalMerge.Studio.Storage/StateReconstructionService.cs`** (new)
- `GetStateAtAsync`: loads all events for the session up to and including `upToEventId` via `IExecutionEventStream.GetSessionEventsAsync`.
- Folds over the event sequence: `WorkUnitCreated` adds to `ActiveWorkUnitIds`; `LeaseReleased(success:false)` removes it; `WorkspaceCreated` adds to `ActiveWorkspaceIds`; etc.
- Returns the accumulated snapshot. No re-execution of agents.

### REST

- `GET /studio/sessions/{id}/state?upToEvent={eventId}` — snapshot at event boundary
- `GET /studio/sessions/{id}/state?asOf={iso8601}` — snapshot at timestamp

### Usage in 10f (branching)

`POST /studio/proposals/{id}/branch` (10f) calls `GetStateAtAsync` using the `ProposalCreated` event ID to pinpoint the exact workspace state to branch from. This replaces the approximate "archive and snapshot" approach with a causal-exact one.

### Success criteria
- Complete a run with 3 work units; `GET /studio/sessions/{id}/state?upToEvent={midRunEventId}` returns a snapshot showing only the work units and workspaces that existed at that point.
- `asOf` query with a timestamp between enqueue and lease returns a snapshot with the work unit in `ActiveWorkUnitIds` but no workspace yet.
- Snapshot at the final event matches the actual post-run state (all IDs present, no extras).
- `GetStateAtAsync` does not call any agent, LLM, or external process.

---

## Slice 10c — Workspace Isolation ✅

> **As-built note:** v1 deliberately does **not** back workspaces with a real git worktree. There was no `IProcessService`/git shell-out in the codebase, and each work unit already gets an isolated branch directory via the existing `IBranchService` + `IFileWorkspaceService` (plain directory copy, created at work-unit-creation time). `AgentWorkspace` (`WorkspaceBackingModel.LogicalBranchWorkspace`) is a lifecycle/enforcement wrapper over that existing branch rather than a second, forked branch — `CreateAsync` resolves the work unit's own `BranchId`, it doesn't fork a new one from `baseBranch`. `DestroyAsync` marks the workspace finalized but does **not** delete the branch directory (it's shared with diff/inspection tooling, and a failed work unit can still be retried, reusing the same workspace). The service interface was named `IAgentWorkspaceService`, not `IWorkspaceService` as below, because `IWorkspaceService` already exists for an unrelated dashboard summary (`GetSummaryAsync`). Real git worktrees remain a future, pluggable backing model (`IWorkspaceBackingStore` with `DirectoryCopyWorkspace`/`GitWorktreeWorkspace` implementations) — not this slice. **This affects 10d** (no real git diff output to parse — `WorkspaceChanges` is still the existing plain-text diff format, not `+++ b/`-style unified diff) **and 10f** (branch-from-proposal has no real worktree to branch from yet).
>
> Implementation: [AgentWorkspace.cs](../src/NodalMerge.Studio.Contracts/Domain/AgentWorkspace.cs), [AgentWorkspaceService.cs](../src/NodalMerge.Studio.Storage/AgentWorkspaceService.cs), wired into `WorkSchedulerService` and `McpToolDispatcher`. Tests: `WorkspaceIsolationTests.cs`.

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

## Slice 10d — Artifact Lineage Store ✅

> **As-built note:** `IArtifactLineageService.RecordAsync`/`UpdateStatusAsync` return the persisted `ArtifactRef` (not `Task`) — matching the return-the-record convention already established by `IExecutionEventStream.AppendAsync` and `IAgentWorkspaceService.CreateAsync` in 10b.2/10c, rather than the plan's original `Task`-returning signature. This interface **replaces** the Phase 2.5/9d `IArtifactRefService`/`InMemoryArtifactRefService` (renamed, not added alongside) since the new service is the persistent superset.
>
> The real single-worker pipeline has no discrete planner stage or per-changeset event yet — fan-out and `ChangeIntent`-level granularity are Phase 4/10f.5. So only the artifact types with a real, distinct producer are wired: `Goal` (work unit creation, `ArtifactId = WorkUnitId`), `Task` (`nm.v1.task.create`, parent = the owning Goal), `MergeProposal` (`nm.v1.merge.propose`, parent = the owning Goal), and `MergeResult` (merge apply, parent = the `MergeProposal`). `Plan` and `BranchChangeset` remain defined in the `ArtifactType` enum for forward compatibility but have no write path yet — there is no separate planner output to record as `Plan`, and worker file writes are tracked via the workspace branch (10c), not as a discrete `BranchChangeset` artifact. The realistic chain today is `Goal → Task → MergeProposal → MergeResult`, not the five-stage chain in the original success criteria below.
>
> `FilesTouched` is parsed from the plain-text diff format `FileSystemWorkspaceService.DiffAsync` actually produces (`+++ ADDED: `, `~~~ MODIFIED: `, `--- DELETED: ` line prefixes) — not `+++ b/`-style unified diff, consistent with the 10c as-built note that there is no real git worktree/diff yet.
>
> Conflict pre-detection at enqueue resolves the *parent's other children* (siblings), not "the parent work unit" itself: `WorkSchedulerService.EnqueueAsync` looks up the enqueued unit's `ParentWorkUnitId`, calls `IWorkUnitService.GetChildrenAsync` (10a) to find siblings, and for each sibling walks `IArtifactLineageService.GetChainAsync` → `MergeProposal` refs → `IMergeService.GetAsync` to resolve `FilesTouched` (an `ArtifactRef` is a lightweight pointer; the actual `FilesTouched` lives on the `MergeProposal` record it points to). `IArtifactLineageService`/`IMergeService` are optional constructor parameters on `WorkSchedulerService` (default `null`). `IWorkUnitService` is **not** a direct constructor parameter — it's resolved lazily through an optional `IServiceProvider`, the same trick `AgentWorkspaceService` already uses, because `InMemoryWorkUnitService → IAgentControlService → InMemoryAgentRuntimeService → IWorkScheduler` closes a cycle back to this service. This was caught by a real test-host crash (circular dependency) before being fixed, not by inspection.
>
> Implementation: [ArtifactLineageService.cs](../src/NodalMerge.Studio.Storage/ArtifactLineageService.cs), wired into `InMemoryWorkUnitService` (Goal), `McpToolDispatcher` (Task, MergeProposal), `InMemoryMergeService` (status transitions, MergeResult), and `WorkSchedulerService` (conflict pre-detection). Tests: `ArtifactLineageTests.cs`.

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

## Slice 10e — Orchestration Decision Log ✅

> **As-built note:** `OrchestrationAction` was already defined in `ExecutionEventPayloads.cs` (10b.2, alongside the never-wired `OrchestrationDecisionPayload`/`ExecutionEventKind.OrchestrationDecision`) — `OrchestrationEvent.cs` reuses it rather than redefining it. This slice **also wires up that dormant 10b.2 hook**: `OrchestrationDecisionLogService.RecordAsync` writes to its own store (`studio/orchestration-event/v1`, queryable by `workUnitId`) and, when a `sessionId` is supplied, mirrors the same decision into the unified causal stream via `OrchestrationDecisionPayload` — matching 10b.2's stated pattern that "the stream does not replace local stores; it references them."
>
> The orchestrator has no separate LLM-routing stage (9e) to extract `Reason` from — all routing is the single orchestrator LLM's tool choice. `Reason` is the tool name for tool-driven decisions (e.g. `"nm_v1_scheduler_enqueue"`) and the LLM's own closing text for the final `end_turn`. Only tool calls that change execution routing become events: `nm_v1_scheduler_enqueue` → `Enqueue`, `nm_v1_agent_spawn` → `SpawnWorker`, `nm_v1_merge_apply` → `ApplyMerge`; investigative calls (`workunit.get`, `projection.get`) and `task.create` are not logged as routing decisions. A clean `end_turn` with no tool call always logs `NoOp` (not `AwaitReview` — distinguishing "stopped because waiting on a human" from "stopped because there was nothing left to do" needs semantic understanding of the projection that isn't reliably inferable from tool output, so it isn't guessed at; see the Deferred Work Tracker). `SpawnPlanner`, `AwaitReview`, and `Escalate` remain defined but unreachable today — same status as `Plan`/`BranchChangeset` from 10d, tracked below rather than silently dropped.
>
> `InputStage` is inferred from the 10d artifact chain (no `MergeProposal` → `Plan` if no `Task` yet, else `Execute`; `MergeProposal` present → `Review`, or `Merge` once `Approved`/`Applied`) — this is the first slice to actually *read* the lineage store 10d built, not just write to it.
>
> The "DAG replay panel" UI success criterion is **not implemented** — see the Deferred Work Tracker.
>
> Implementation: [OrchestrationEvent.cs](../src/NodalMerge.Studio.Contracts/Domain/OrchestrationEvent.cs), [OrchestrationDecisionLogService.cs](../src/NodalMerge.Studio.Storage/OrchestrationDecisionLogService.cs), wired into `OrchestratorAgentLoop.cs`. Tests: `OrchestrationDecisionLogTests.cs`, extended `FullAgentCycleTests.cs`.

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

## Slice 10f — Proposal DAG & Artifact Branching ✅

> **As-built note:** No new `IWorkspaceService.CreateFromProposalBaseAsync` or `IAgentWorkspaceService` method was added — given 10c's wrapper model (a workspace *is* its work unit's branch, not a fork), the only thing "branching" actually needs is for the *new* work unit's branch to be seeded with the right content, which the existing `IFileWorkspaceService.InitBranchAsync(branchId, seedFromBranchId)` already does. So:
> - `InMemoryMergeService.ProposeAsync` now snapshots the target branch's current content into a stable `base/{proposalId}` branch the moment a proposal is raised — *not* at apply time — so S0 stays correct regardless of whether the proposal later gets approved, rejected, or applied (none of those mutate this copy).
> - `IOrchestratorService.CreateWorkUnitAsync` gained two optional parameters: `seedFromBranchId` (threaded into the same `IBranchService.CreateBranchAsync` call every work unit already makes) and `branchedFromProposalId` (stored in the already-existing but previously-unused `WorkUnit.Metadata` dictionary as `branchedFromProposalId`). No new domain fields, no new storage.
> - The Goal artifact's `ParentArtifactId` — previously hardcoded `null` for every work unit — is now `workUnit.ParentWorkUnitId`. This was a latent gap from 10d (a child work unit's Goal had no parent in the artifact graph even when `ParentWorkUnitId` was set) that 10f's lineage needs anyway: a branched work unit's Goal is parented to the origin work unit's Goal, making `GetChildrenAsync`/`GetChainAsync` (10a/10d) sufficient to answer "what was branched from here" with zero new query surface.
> - REST routes use the existing `/studio/merges/...` prefix (`POST /studio/merges/{proposalId}/branch`, `GET /studio/merges/compare`), not the plan's literal `/studio/proposals/...` — `MergeProposal` resources already live under `/studio/merges` (validate/review/apply); a second prefix for the same resource would be confusing, not just inconsistent.
> - `proposal-dag`'s response shape is `{ workUnitId, proposals: [{ proposalId, status, baseState, producedState, filesTouched }], branches: [{ workUnitId, goal, status, branchedFromProposalId }] }` — simpler than the plan's illustrative JSON (no `reconciledFrom`/merger-proposal section, since N-proposal reconciliation is Phase 4's Merger/Reducer, not this slice).
> - The "Merge Review panel" UI buttons are **not implemented** — folded into the same UI-tracker row as 10e's DAG replay panel; see the Deferred Work Tracker.
>
> Implementation: [InMemoryMergeService.cs](../src/NodalMerge.Studio.Merge/InMemoryMergeService.cs) (base snapshot), [InMemoryWorkUnitService.cs](../src/NodalMerge.Studio.Orchestrator/InMemoryWorkUnitService.cs) (`seedFromBranchId`/`branchedFromProposalId`), [StudioRestEndpoints.cs](../src/NodalMerge.Studio.Host/StudioRestEndpoints.cs) (`branch`, `compare`, `proposal-dag`). Tests: `ProposalBranchingTests.cs`.

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

## Slice 10f.5 — Intent Graph & Conflict Resolver ✅ (scoped)

> **As-built note:** Only **Option A — strict partitioning** is implemented: `IIntentGraphService` records intents and answers overlap queries; `WorkSchedulerService.EnqueueAsync` folds overlapping intents into the same advisory, non-blocking `ConflictWarning` that 10d's reactive (FilesTouched-based) detection already produces — it does not block, lock, or serialize anything. Region locking (Option B), optimistic execution with revalidation (Option C), and the planner/agent revalidation loop all assume real concurrent worker execution exists to coordinate, which it doesn't until Phase 4 fan-out — building them now would be locking/queuing logic with nothing to lock or queue against. See the Deferred Work Tracker.
>
> `QueryOverlappingAsync` is **global by `TargetPath`/`RegionDescriptor`**, not "per-work-unit" as the plan's wording suggests — intents from any two *different* work units touching the same path/region overlap, regardless of whether they're siblings under the same parent. This is deliberately broader than 10d's sibling-scoped `FilesTouched` check: declared intents are supposed to catch conflicts *before* any work starts, so there's no reason to narrow the search to a parent/child relationship that may not even exist yet (today, with no fan-out, every work unit is effectively a root). A whole-file region (`""`, `"*"`, or `"whole-file"`) overlaps with any region on the same path; otherwise two intents conflict only if they name the exact same region string — no AST awareness, matching the project's existing no-real-diff-parsing precedent from 10c/10d.
>
> There's no planner stage to "produce" intents from (same gap as 10d's `Plan` artifact) — `nm_v1_intent_record` is exposed to the **orchestrator** (today's de facto planner) as an explicitly optional tool, callable before `nm_v1_scheduler_enqueue`. The `ScriptedLlmHandler` used by `FullAgentCycleTests` doesn't call it (the orchestrator script predates this tool and isn't required to use it), so this slice's coverage comes from `IntentGraphTests.cs` exercising the service and scheduler integration directly, not a full agent-loop run — consistent with how 10d/10f were primarily tested.
>
> `IIntentGraphService` is constructor-injected directly into `WorkSchedulerService` (no `IServiceProvider` indirection needed) since, unlike `IWorkUnitService`, nothing in its dependency chain (`IStudioNodeStore`, `IArtifactLineageService`) loops back to `IWorkScheduler`.
>
> Implementation: [ChangeIntent.cs](../src/NodalMerge.Studio.Contracts/Domain/ChangeIntent.cs), [IntentGraphService.cs](../src/NodalMerge.Studio.Storage/IntentGraphService.cs), wired into `WorkSchedulerService.cs` (conflict detection) and `McpToolDispatcher.cs`/`OrchestratorAgentLoop.cs` (`nm_v1_intent_record`). Tests: `IntentGraphTests.cs`.

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

## Slice 10g — Knowledge Artifacts ✅

> **As-built note:** `ArtifactRef` gained optional `Title`/`Body` fields — the plan's claim that knowledge artifacts need "no new storage schema" because "they are ArtifactRef records" only holds if `ArtifactRef` can actually carry note content, which it couldn't before this slice (it was a pure lineage pointer with no content fields at all). This was the minimal fix consistent with that intent, rather than inventing a parallel `KnowledgeArtifact` domain object with its own store.
>
> `InheritedConstraints` did **not** already exist on `AgentWorkspaceProjectionPayload` from 9d as the plan claimed — it didn't exist anywhere in the codebase. Both it and the ancestor-walk-into-`Artifacts` behavior (`AgentWorkspaceProjection includes the parent's Research artifact in the chain`) are new in this slice, added to `ProjectionManager.BuildAgentWorkspaceAsync`. `InheritedConstraints` is scoped to *ancestors only* — a work unit's own `Constraint` artifacts appear in the regular `Artifacts` chain but not in `InheritedConstraints`, since they weren't inherited from anywhere.
>
> Tool names are `nm_v1_artifact_record`/`query`/`list`, not the plan's literal dotted `nm.v1.artifact.*` — dots aren't valid under this project's own frozen tool-name constraint (`^[a-zA-Z0-9_-]{1,128}$`), so every other tool already uses underscores; the dotted spelling was shorthand, not a literal name.
>
> As with 10d/10e/10f.5, the same feature is wired twice: into `McpToolDispatcher` (used by the internal orchestrator/worker loops — this is what `FullAgentCycleTests` now exercises end-to-end) and into a new `ArtifactTools.cs` (used by external MCP clients via `WithToolsFromAssembly`), matching the existing `TaskTools`/`MergeTools` duplication pattern rather than introducing a new one.
>
> The ancestor-walk logic (`ParentWorkUnitId` chain, root-first, de-duped by `ArtifactId`) is duplicated three times in three different projects (`ProjectionManager`, `McpToolDispatcher`, `ArtifactTools`) rather than shared, because the services that need it don't share a common base across project boundaries and the loop is ~10 lines — same trade-off as `AgentWorkspaceService.MatchesGlob`/`WorkSchedulerService`'s glob duplication before it was consolidated. Worth revisiting only if a fourth caller shows up.
>
> Default `worker`/`planner` profile `AllowedTools` (in `AgentProfileService.SeedDefaults`) now include the three new tools, per spec — but note these seeded profiles aren't actually used by `FullAgentCycleTests` or any current call path that doesn't explicitly pass a `profileId`; see them as the intended-but-not-yet-default profile set.
>
> Implementation: [ArtifactRef.cs](../src/NodalMerge.Studio.Contracts/Domain/ArtifactRef.cs) (`Title`/`Body`), [ProjectionContracts.cs](../src/NodalMerge.Studio.Contracts/Projections/ProjectionContracts.cs) (`InheritedConstraints`), [ProjectionManager.cs](../src/NodalMerge.Studio.Projections/ProjectionManager.cs), [McpToolDispatcher.cs](../src/NodalMerge.Studio.AgentRuntime/McpToolDispatcher.cs), [ArtifactTools.cs](../src/NodalMerge.Studio.McpServer/Tools/ArtifactTools.cs). Tests: `StubProjectionManagerTests.cs` (ancestor inheritance, 2-level grandchild walk), extended `FullAgentCycleTests.cs` (real MCP-dispatch coverage for `artifact.record`).

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

10a → 10b → 10b.1 → 10b.2 → 10b.3 → 10b.4 → 10c → 10d → 10e → 10f → 10g

- **10a first**: the DAG is what scheduler, isolation, and lineage all reference.
- **10b before 10b.1**: the scheduler exists before sessions can attach to it. `EnqueueAsync` gains a `sessionId` parameter once 10b.1 is in place.
- **10b.1 before 10b.2**: the unified event stream requires a `SessionId` on every event; sessions must exist first.
- **10b.2 before 10b.3**: idempotency for `AppendAsync` is implemented inside the event stream service; the stream must exist first.
- **10b.3 before 10b.4**: state reconstruction reads from the event stream; the idempotency guarantees in 10b.3 ensure the stream is free of duplicates before reconstruction reads it.
- **10b.4 before 10c**: workspace creation (10c) becomes a causal event in the stream; `GetStateAtAsync` needs workspace events to produce accurate snapshots. More importantly, 10f's branch-from-proposal operation depends on `GetStateAtAsync` — that dependency must be resolved before branching is wired up.
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

After Phase 3, the system has a correct, isolated, attributable, replayable, branchable artifact platform with durable knowledge and session identity. The answer to "what can I do after a run?" is now: inspect, branch, replay, compare, resume, or start a parallel session — and reuse prior knowledge across all of them. Phase 4 makes it genuinely parallel:

- Fan-out: planner decomposes goal → N child work units → N scheduler entries → N isolated workers in parallel
- Artifact lifecycle state machine: formal states per artifact replacing the implicit status flags
- Merger/Reducer: N proposals → 1 reconciled candidate using `FilesTouched` conflict map from 10d
- Automated reviewer agent (Stage = Review) as optional pre-gate
- Dead-letter escalation using the lifecycle state machine

Knowledge artifacts (10g) pay off here: parallel workers each query `nm.v1.artifact.query` at turn-0 and inherit the same constraints — they don't rediscover independently.
