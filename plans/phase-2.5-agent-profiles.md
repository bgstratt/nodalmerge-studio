# Phase 2.5 — Pipeline Stages & Artifact-Centered Execution

## Core conceptual shift

The original plan had persona-based worker roles (Implementer, Reviewer, etc.). We're replacing that with a **pipeline of stages over a shared workspace**, where each stage transforms an artifact.

> Instead of "who the agent is" → "what stage the work is in."

```
Goal → [Orchestrate] → [Plan] → [Execute+Propose] → [Review] → [Apply]
         task list      plan       branch changes +    approve/     disk
                                   merge proposal      reject
```

Key consequences:
- `Reviewer` is a **function applied to proposals**, not a spawned worker identity. In Phase 2.5 it is the human gate. Phase 3 can add an automated reviewer agent.
- `Worker` is the **generic executor** — any LLM operating at the Execute stage. Not "an implementer" — just a stage with specific allowed tools and a stage-scoped prompt.
- The orchestrator routes by **artifact state** (what exists), not by persona (what type of agent to call).
- In Phase 2.5, Execute and Propose are collapsed: the worker produces changes *and* wraps them into a merge proposal. Phase 3 splits them if needed.

---

## Pre-2.5 status

✅ Pre-2.5 validation complete — end-to-end run produced `success.md` in the target repo via the merge apply write-back path.

## Progress

- [x] 9a — Diff in Merge Review panel
- [x] 9b — AgentProfile with pipeline stage model
- [x] 9c — Profile-driven loop configuration
- [x] 9d — AgentWorkspaceProjection with artifact chain
- [x] 9e — Artifact-state-driven orchestrator routing

---

## What 2.5 adds

| Today | After 2.5 |
|-------|-----------|
| `agentType = "orchestrator" / "worker"` — routing key only | `AgentProfile` with `PipelineStage`, stage-scoped tools, stage-appropriate prompt |
| Persona roles: Implementer, Reviewer | Pipeline stages: Orchestrate, Plan, Execute, Review (Reviewer is the human gate in 2.5) |
| No diff in Merge Review panel | `workspaceChanges` diff rendered inline with +/- syntax coloring |
| Context assembled ad hoc | `AgentWorkspaceProjection` includes artifact chain (plan → changes → proposals) |
| Orchestrator spawns a generic "worker" | Orchestrator reads artifact state, routes to the appropriate next stage |

---

## Slice 9a — Diff in Merge Review panel

`MergeProposal.workspaceChanges` already carries a unified diff. The review panel just doesn't show it.

**Files touched:**
- `clients/vscode-extension/src/panels/MergeReviewPanel.ts`

**Changes:**
1. Add `workspaceChanges?: string | null` to the `MergeProposal` interface.
2. Add a `<section id="section-diff">` in `REVIEW_HTML` (after the Change Description section).
3. Render as `<pre>` with per-line coloring: lines starting with `+` green, `-` red, `@@` muted blue. Collapse if empty.

**Success criteria:**
- Merge Review panel shows the diff for proposals that have `workspaceChanges`.
- Panel still renders correctly for proposals without it.

---

## Slice 9b — AgentProfile with pipeline stage model

Introduces `AgentProfile` as a server-side DAG entity. The key difference from the old plan: roles are pipeline stages, not personas.

### Domain record

**`src/NodalMerge.Studio.Contracts/Domain/AgentProfile.cs`** (new)

```csharp
namespace NodalMerge.Studio.Contracts.Domain;

public enum PipelineStage
{
    Orchestrate,  // goal → task list; coordinates all other stages
    Plan,         // task → structured plan (steps, file touchpoints, assumptions)
    Execute,      // plan → file changes on an isolated branch + merge proposal
    Review,       // proposal → approved / rejected / revision-requested
    Merge,        // multiple approved proposals → reconciled final state (Phase 3)
}

public sealed record AgentProfile(
    string AgentProfileId,
    string Name,
    PipelineStage Stage,
    string SystemPrompt,
    IReadOnlyList<string> AllowedTools,   // empty = all nm.v1.* tools
    int MaxIterations);
```

Note: `Reviewer` is not in this enum as a spawnable agent type — `Review` is the stage, and in Phase 2.5 it is the human gate. An automated reviewer agent (Phase 3) would have `Stage = Review`.

### Storage

**`src/NodalMerge.Studio.Storage/StudioNodeKind.cs`**
- Add `public const string AgentProfileV1 = "studio/agent-profile/v1";`

**`src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`**
- Add `IAgentProfileService` with `CreateAsync`, `GetAsync`, `UpdateAsync`, `ListAsync`.

**`src/NodalMerge.Studio.Storage/AgentProfileService.cs`** (new)
- Backed by `IStudioNodeStore`. Seeds defaults on first start.

### Default profiles seeded on first start

| ProfileId | Stage | AllowedTools | MaxIterations |
|-----------|-------|-------------|---------------|
| `orchestrator` | Orchestrate | *(all)* | 25 |
| `planner` | Plan | `task.create`, `task.list`, `workunit.get`, `workspace.summary` | 15 |
| `worker` | Execute | `task.update`, `task.list`, `task.assign`, `file.read`, `file.write`, `file.delete`, `merge.propose`, `merge.validate`, `branch.create`, `branch.status`, `snapshot.get` | 20 |

`worker` handles both Execute and Propose in Phase 2.5 (it writes changes *and* calls `merge.propose`). A dedicated `proposer` profile is Phase 3 scope.

### REST endpoints

**`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`**
- `GET /studio/agent-profiles` → list
- `GET /studio/agent-profiles/{id}` → get
- `POST /studio/agent-profiles` → create
- `PUT /studio/agent-profiles/{id}` → update

### Extension panel

**`clients/vscode-extension/src/panels/AgentConfigPanel.ts`**
- Add "Pipeline Profiles" tab.
- Table: ProfileId | Name | Stage | Tools count | MaxIterations | Edit.
- Form: Name, Stage (dropdown of `PipelineStage` values), SystemPrompt (textarea), AllowedTools (comma-separated), MaxIterations.
- Quick Spawn profile selector shows pipeline profiles alongside LLM config profiles.

### Success criteria
- Three default profiles visible in Pipeline Profiles tab on first launch.
- Create/edit a profile; restart; profile persists.
- `GET /studio/agent-profiles` returns the list.
- All existing tests pass.

---

## Slice 9c — Profile-driven loop configuration

Loops load system prompt, maxIterations, and allowedTools from a resolved `AgentProfile` at spawn time. Hardcoded defaults remain as fallback when no profile is provided.

### Spawn signature

**`src/NodalMerge.Studio.Core/Services/ServiceContracts.cs`**
- `IAgentControlService.SpawnAsync` gains `string? profileId = null`.

**`src/NodalMerge.Studio.Host/StudioRestEndpoints.cs`**
- `SpawnAgentBody` gains `string? ProfileId = null`, passed through.

**`src/NodalMerge.Studio.McpServer/Tools/AgentTools.cs`** (or `AgentSpawnAsync` in dispatcher)
- `nm.v1.agent.spawn` gains optional `profileId` parameter.

### Loop constructors

**`OrchestratorAgentLoop.cs` and `WorkerAgentLoop.cs`**
- Accept `AgentProfile? profile`.
- System prompt: `profile?.SystemPrompt ?? <hardcoded default>`
- MaxIterations: `profile?.MaxIterations ?? <hardcoded default>`
- Tool list passed to LLM: filtered to `profile.AllowedTools` if non-empty; full list otherwise.

### Dispatcher enforcement

**`McpToolDispatcher.cs`**
- Gains `IReadOnlyList<string>? allowedTools = null`.
- Rejects calls to unlisted tools with a permission error. Safety net behind the LLM-level filtering.

### Success criteria
- Spawn with `profileId=worker`; dispatcher rejects `nm.v1.task.create` (not in worker's tool list).
- Spawn with `profileId=planner`; dispatcher rejects `nm.v1.merge.propose`.
- No profile → existing behavior unchanged; all integration tests pass.

---

## Slice 9d — AgentWorkspaceProjection with artifact chain

The projection tells each agent where in the pipeline the work is and what artifacts already exist upstream. This is what lets the orchestrator make routing decisions without hardcoding logic.

**Critical design note**: `ArtifactChain` is a lineage graph, not a routing struct. The prior design (`string? Plan + IReadOnlyList<string> ProposalIds`) could answer "does a plan exist?" but could not answer "what produced this proposal?", "what is its parent?", or "where does replay start?" A flat ID list has no parent traversal and makes branching, replay, and DAG visualization significantly harder in every later phase. The model below fixes this before anything is built on top of it.

### Artifact identity model

**`src/NodalMerge.Studio.Contracts/Domain/ArtifactRef.cs`** (new)

```csharp
namespace NodalMerge.Studio.Contracts.Domain;

public enum ArtifactType
{
    Goal,               // the root work unit intent
    Plan,               // structured plan produced by a planner
    Task,               // a discrete unit of work within a plan
    Research,           // discovered facts: codebase analysis, API contracts, dependency inventory
    Decision,           // architectural choice with rationale and trade-offs
    Constraint,         // invariant or requirement that all descendant runs must respect
    BranchChangeset,    // file changes on an execution branch (pre-proposal)
    MergeProposal,      // a formal proposal wrapping a BranchChangeset
    MergeResult,        // the applied workspace state after a proposal is merged
}

public enum ArtifactStatus
{
    Active,       // in use; not yet terminal
    Approved,     // proposal approved by human (or automated reviewer)
    Rejected,     // explicitly rejected
    Superseded,   // replaced by a merger's reconciled output
    Applied,      // merge result written back to disk
}

public sealed record ArtifactRef(
    string ArtifactId,
    ArtifactType Type,
    string? ParentArtifactId,      // null only for Goal (root)
    ArtifactStatus Status,
    DateTimeOffset CreatedAt,
    string? OwnedByWorkUnitId,
    string? OwnedByAgentId);
```

This gives every artifact a parent. The chain is now:

```
Goal
 └─ Plan
     └─ Task A
         ├─ BranchChangeset
         │   └─ MergeProposal A
         └─ MergeProposal B (fork — different model run)
```

You can walk up to answer "what produced this?", walk down to answer "what did this produce?", and filter by type and status to route without special-case logic.

### Projection payload

**`src/NodalMerge.Studio.Contracts/Projections/ProjectionContracts.cs`**

```csharp
public sealed record ArtifactChain(
    IReadOnlyList<ArtifactRef> Artifacts);     // ordered by CreatedAt; full graph for this work unit

public sealed record AgentWorkspaceProjectionPayload(
    string AgentId,
    PipelineStage Stage,
    WorkUnitProjectionPayload WorkUnit,
    IReadOnlyList<StudioTask> AssignedTasks,
    ArtifactChain Artifacts,
    IReadOnlyList<string> AvailableTools);
```

Convenience accessors the projection manager populates (not stored separately — derived from `Artifacts`):
- `Plan`: first `ArtifactRef` with `Type == Plan && Status == Active`
- `PendingProposals`: all `Type == MergeProposal && Status == Active`
- `ApprovedProposals`: all `Type == MergeProposal && Status == Approved`
- `InheritedConstraints`: all `Type == Constraint` from this work unit and its ancestors — walked up the `ParentWorkUnitId` chain

These replace the old flat fields. Routing logic in 9e reads from `Artifacts` directly.

`InheritedConstraints` is the mechanism by which knowledge artifacts propagate: a constraint recorded on the root work unit automatically becomes part of every descendant's projection context. Agents never need to rediscover that "auth middleware must not store session tokens" — it is in the projection from turn 0.

### Population

**`src/NodalMerge.Studio.Core/ProjectionManager.cs`** (or `AgentWorkspaceProjectionBuilder`)
- On projection build: query `IStudioNodeStore` for all artifact records owned by the work unit.
- Assemble `ArtifactRef` list in creation order.
- Return `ArtifactChain` with full graph.

Each service that produces an artifact (planner loop, worker loop, merge propose tool) writes an `ArtifactRef` to the store when the artifact is created. The `ArtifactId` is the natural ID of the artifact (e.g., `MergeProposalId` for a proposal, plan branch path for a plan). This is the Phase 2.5 write path — the Phase 3 slice 10d adds the backing store query for the full graph.

### Loop changes

**`OrchestratorAgentLoop.cs` and `WorkerAgentLoop.cs`**
- Turn 0 seed calls `projectionManager.GetAsync(ProjectionType.AgentWorkspace, ...)` and serializes into the opening message.
- This replaces the ad hoc "Begin orchestrating work unit {id}..." strings.

### Success criteria
- Orchestrator's turn-0 LLM message includes `artifacts` array with typed, parented refs — not flat ID lists.
- `nm.v1.projection.get` with `type: "AgentWorkspace"` returns the full `ArtifactChain`.
- After a worker produces a proposal: `Artifacts` contains `[Goal, Plan (if ran), Task, BranchChangeset, MergeProposal]` with correct parent chain.
- Routing in 9e reads artifact type + status from the chain; no string parsing of plan text needed.

---

## Slice 9e — Artifact-state-driven routing in orchestrator

Orchestrator reads the artifact chain from the projection and routes to the appropriate next pipeline stage. This replaces hardcoded `agentType="worker"` spawning.

### Routing logic (in `OrchestratorAgentLoop` or injected as `IPipelineRouter`)

```
if no tasks exist          → spawn Planner (or self-plan if no Planner profile configured)
if plan exists, no branch changes → spawn Worker (Execute stage)
if branch has changes, no proposal → worker should self-propose; orchestrator can prompt it
if proposal pending review → surface to human (already handled by merge gate)
if proposal approved      → call nm.v1.merge.apply
```

The orchestrator uses `nm.v1.projection.get` with `type: "AgentWorkspace"` to read the current state before each routing decision. It does not hardcode stage transitions — it reads artifact presence.

### `InjectSpawnCredentials` update

**`OrchestratorAgentLoop.cs`**
- When spawning, inject `profileId` resolved from the target stage (e.g., `worker` for Execute stage).
- This replaces the current `agentType="worker"` passthrough.

### Success criteria
- Quick Spawn a goal with no plan: orchestrator spawns a worker that produces file changes and a merge proposal.
- Merge Review panel shows the diff (9a) and the proposal.
- Human approves → apply writes back to the filesystem.
- Test with two sequential goals: second run routes correctly even if first proposal already exists.

---

## Slice ordering

9a → 9b → 9c → 9d → 9e

- **9a first**: zero risk, immediate UX value, requires no server changes.
- **9b before 9c**: profiles must exist before loops can load them.
- **9c before 9d**: loop config determines which tools the projection reports as available.
- **9d before 9e**: orchestrator needs the artifact chain to route by state.

---

## Files not touched in 2.5

| File | Reason |
|------|--------|
| `LlmClient.cs` | Provider abstraction complete; no changes needed |
| Integration tests | Pass `null` for `profileId`; default behavior unchanged |
| `mcp-v1-contract.md` | `nm.v1.agent.spawn` gains optional `profileId` — additive, backward-compatible |
| Merger/Reducer logic | Phase 3 scope |
| Automated reviewer agent | Phase 3 scope — in 2.5 Review is the human gate |

---

## Phase 3 pointer

After 9e, the pipeline is complete for single-worker runs. Phase 3 extends it:
- Fan-out: orchestrator spawns N workers in parallel on isolated branches
- Merger/Reducer stage that reconciles overlapping proposals
- Automated reviewer agent (Stage = Review) as an optional quality gate before human review
- Streaming pipeline stage state to the DAG replay panel
- Dead-letter / failure escalation path
