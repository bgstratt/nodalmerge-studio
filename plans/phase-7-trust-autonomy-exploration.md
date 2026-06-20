# Phase 7 — Trust, Autonomy & Exploration

## ✅ PHASE 7 COMPLETE (verified 2026-06-19)

All five sub-phases (7.0–7.5) have backend services, REST endpoints, and UI wired in
`ArtifactExplorerPanel.ts` / `AgentConfigPanel.ts` / `WorkspaceDashboardPanel.ts`. `dotnet build
NodalMerge.Studio.slnx` succeeds with 0 errors. Verified by reading the actual implementation, not
by trusting prior plan-doc checkmarks — see per-phase notes below for the file/line evidence.

**Test coverage gap closed (2026-06-19):** `SteeringServiceTests.cs`, `CounterfactualServiceTests.cs`,
`ExperimentServiceTests.cs`, and `CandidateBranchServiceTests.cs` were added under
`tests/NodalMerge.Studio.Integration.Tests/`, following the existing DI-built-host-with-in-memory-storage
convention (no mocks). Coverage: SteeringService 92%, CounterfactualService 95.6%, ExperimentService
96.1%, CandidateBranchService 100% (via `scripts/coverage.ps1`, coverlet + ReportGenerator). Writing
these tests surfaced and fixed a real bug: `SteeringService.ForkFromNodeAsync` was mutating the
returned `WorkUnit` record with `fork with { BranchedFromProposalId = ... }` after creation instead of
passing `branchedFromProposalId`/`seedFromBranchId` into `CreateWorkUnitAsync` — the mutation was never
persisted, so `BranchedFromProposalId` silently stayed null on any fork-from-node call that specified a
`ProposalId`. Fixed to pass both params at creation time, matching `CounterfactualService`'s pattern.

**Remaining gap (updated 2026-06-20):** added `AutoApplied` as a real signal threaded through
`IMergeCommandService.ApplyAsync`/`IMergeService.ApplyAsync` (set `true` only at the two
unambiguously-automatic call sites: the post-propose fire-and-forget trigger in
`MergeCommandService.cs` and `ReviewTimerService.ProcessExpiredAsync`), added
`ReviewTimerServiceTests.cs`/`AutoReviewRuleTests.cs` (13 new unit tests, all passing), and wired
three new proactive notifications into `NotificationManager.ts` (auto-applied, reviewer-rejected,
fan-out-blocked) — see `clients/vscode-extension/src/NotificationManager.ts`.

Ran a live REST pass against a real `dotnet run` instance of the Host (no mocks, no unit-test
fakes): `HumanRequired` baseline (propose → validate → human-approve → apply) confirmed unchanged,
`autoApplied` correctly stays `false` on a human-driven apply; promotion-branch flow confirmed
end-to-end (`UsePromotionBranch=true` lands the apply on `candidate`, `main` is untouched until
`POST /studio/branches/candidate/promote`, after which `main` picks up the change).

Still not exercised live, by deliberate scope decision rather than oversight: `AgentApproval`/
`Hybrid` auto-apply, automated reviewer rejection, and fan-out file-scope blocking all require a
real LLM-backed `ReviewerAgentLoop`/`OrchestratorAgentLoop` run (no credentials were available for
this pass), so the `autoApplied: true` and "reviewer rejected"/"blocked from starting" notification
paths were verified only at the unit-test level (Workstream B fakes), not against a live agent run.
Likewise, no VS Code Extension Development Host driver exists in this repo, so the toasts themselves
were never visually confirmed popping up — only that the REST DTOs they read (`autoApplied`,
`verificationResults`, `WorkUnit.FanOutInfo.BlockedReason`) are correctly shaped on the wire.
Re-run this pass with real provider credentials (and ideally a Playwright/`_electron` harness, see
`/run-skill-generator`) to close the remaining gap.

**Critical bug found and fixed (2026-06-20):** added `AutonomousReviewLlmHandler.cs` +
`AutonomousReviewTests.cs` (`tests/NodalMerge.Studio.Integration.Tests/`) — scripted-LLM integration
tests (same zero-cost `ScriptedLlmHandler` pattern as `FullAgentCycleTests`, no real API calls) that
drive `AgentApproval`/`Hybrid` through the *real* `OrchestratorAgentLoop` → `WorkerAgentLoop` →
`ReviewerAgentLoop` chain instead of the hand-written fakes in `AutoReviewRuleTests.cs`. They found
that **`AgentApproval`/`Hybrid` had never actually completed an auto-merge, even with a real LLM**:
`InMemoryMergeService.AutomatedReviewAsync` set an "Approved" automated review back to
`ReadyForReview` (Slice 11d's original pre-gate-before-human semantics) instead of terminal
`Approved`, so `InlineReviewerService`'s `proposal.Status is Approved or Merged` check never passed
and every proposal stalled indefinitely with no human-visible signal. Fixed by having
`AutomatedReviewAsync` check the owning work unit's `ReviewPolicy`: `AgentApproval`/`Hybrid` now
land on terminal `Approved`; `HumanRequired`'s original pre-gate-bounces-to-ReadyForReview behavior
is unchanged. Added `(UnderReview, Approved)` to `MergeProposalTransitions.CanTransition`
(`MergeProposal.cs`) and updated `MergeProposalTransitionTests.UnderReview_transitions_for_automated_review`
(renamed from `..._automated_pre_gate`) accordingly. Full suite (309 tests) green after the fix.

## Context

Phases 6.5–6.8 completed the **reasoning plane**: every agent action produces a durable artifact,
every artifact is inspectable, diff review is line-aware, the decision tree is a real DAG, and the
UI speaks decision-centric vocabulary. Phase 6.6 added the **execution plane** (build/test/lint
grounded in the filesystem).

The system is now internally coherent. The next risk isn't "can we build it?" — it's **"can we
trust it to do meaningful work while we're not watching?"**

Four gaps prevent that trust today:

1. **The UI lies about scope.** Sessions exist (`IExecutionSessionService`, 10b.1), but every panel
   queries global state. Selecting a session doesn't filter anything — the user believes they're
   inside a workspace but they're looking at the universe.
2. **Restarts erase liveness.** The DAG persists (8b), but agent runtime processes die with the
   host. On restart the UI presents a blank workspace even though all work is intact in storage.
3. **Humans are always required at the gate.** Every proposal hits a review gate with no path to
   auto-proceed. This is AP-4 design, but it makes "leave for lunch, return to completed work"
   impossible.
4. **Agents write to "main" directly.** No safety boundary exists. If an agent produces a bad
   proposal and a human approves it, it applies. An experiment shouldn't be able to clobber the
   canonical workspace without an explicit promotion step.

This phase attacks them in the order that unlocks the most trust per unit of work, then extends the
platform into experimental and autonomous territory once the foundation is solid.

---

## UI Surface Principle (Applies to All Phases)

Every new capability in this phase must be **discoverable from the Goal Workspace without navigating to settings.** The goal creation flow and the work unit card are the two primary surfaces where configuration should live.

### Goal Creation Form (Goal Workspace topbar)

After Phase 7 is complete, the goal creation area looks like:

```
┌─ Goal Workspace ────────────────────────────────────────────────────────────┐
│  Strategy: [ Multi-Agent Fanout ▼ ]   Session: [ Project X ▼ ]             │
│  ┌─────────────────────────────────────────────────────────┐               │
│  │ Describe a goal...                                       │               │
│  └─────────────────────────────────────────────────────────┘               │
│                                                                             │
│  Review:  ○ Human Required  ● Agent Approval  ○ Hybrid (5 min)            │
│  Target:  ○ Direct  ● Candidate Branch                                     │
│                                    [ Explore ▶ ]                           │
└─────────────────────────────────────────────────────────────────────────────┘
```

- **Review** (from 7.0): three-option radio inline below the textarea.
- **Target** (from 7.1): Direct vs Candidate Branch toggle. Hidden when `UsePromotionBranch` is
  globally off; shown as a per-goal override when it's on.
- **Strategy** (from 7.2): the existing dropdown, expanded to include experiment types
  (Multi-Model, Architecture, Library, Product Strategy).

### Work Unit Card (Goal Workspace Decision Tree)

Each node card gains status badges for the new capabilities:

```
┌─ Add dark mode support ──────────────────────── ● Running ─┐
│  fork: work-abc123    agent: claude-opus                    │
│  📋 Review: Agent Approval    🎯 → candidate                │
│  [Pause] [Fork] [Open Terminal]                             │
└─────────────────────────────────────────────────────────────┘
```

- **Review badge** (7.0): shows active policy; tapping opens policy detail.
- **Target badge** (7.1): shows `→ candidate` or `→ main`.
- **[Pause] / [Fork]** (7.3): steering actions inline on the card.
- **Experiment parent** (7.2): when a node is an experiment parent, it shows fork count and
  a `[Compare Results]` link.

### Session Config Panel (Model & Agent Studio)

A "Session Defaults" section in Model & Agent Studio sets workspace-level defaults that the per-goal
form inherits:

```
Session Defaults
  Default Review Policy:   [ Human Required ▼ ]
  Promotion Branch:        [ ✓ Use candidate branch ]  [ ↑ Promote to Main ]
  Default Strategy:        [ Multi-Agent Fanout ▼ ]
```

This is where the `UsePromotionBranch` toggle and default review policy live when users want to set
a workspace-wide default rather than configuring per-goal.

---

## What Already Exists (Do Not Rebuild)

| Capability | Where |
|---|---|
| `IExecutionSessionService` + REST `/studio/sessions/*` | Slice 10b.1 |
| `IStateReconstructionService` — fold event stream to point-in-time snapshot | Slice 10b.4 |
| `IPolicyGateService` with `BeforeEnqueue`, `ProposalCreated`, `BeforeMerge` checkpoints | Slice 14a |
| `AutoReviewProfileId` on agent spawn + REST | Slice 15e |
| `HypothesisForkType` enum: Code/Reasoning/Model/Research/Architecture/Product | Phase 6.7b/6.8 |
| `IDecisionNodeService` + orchestration decision log | Phase 6.7b |
| Agent pause/resume/stop REST routes | Existing REST |
| `BranchExplorer` + `Counterfactual` trajectory replay modes | Phase 6.8 (18d) |
| `ModelDivergenceView` projection + model comparison backend | Phase 6.7b |

---

## ~~Phase 6.9 — Session & Persistence Foundation~~ ✅ COMPLETE

**Theme:** Make the session selector mean something and make restarts invisible.

### What the UX Lie Looks Like Today

```
User selects Session "Project X"
  ↓
Goals Workspace  → shows all work units globally
Execution Timeline → shows all agents globally
Decision Convergence → shows all proposals globally
Trajectory Replay → shows everything globally
```

The user expects scoped state. They get global state.

### Session Projection Layer

All primary data endpoints gain a `?sessionId=` filter:

```
GET /studio/workunits?sessionId=...
GET /studio/agents?sessionId=...
GET /studio/merges?sessionId=...
GET /studio/events?sessionId=...
GET /studio/projections/GoalGraph?sessionId=...
GET /studio/trajectory/replay?sessionId=...
```

When absent: return global state (no breaking change). When present: filter by the session's owned
work unit IDs (a session already owns its work units via `ExecutionSession.WorkUnitIds`).

This is the **session projection layer** — not a new store, just a filter on all existing queries
using the event stream and session boundary that 10b.1 already established.

### Shell-Level Session Context

The `StudioShellPanel` currently exposes a session picker but doesn't propagate the selection to
child panels. After this:

```
StudioShellPanel
  selectedSessionId: string | null
  ↓ passed to every panel on tab activation
GoalWorkspacePanel.setSessionContext(sessionId)
ExecutionTimelinePanel.setSessionContext(sessionId)
DecisionConvergencePanel.setSessionContext(sessionId)
TrajectoryReplayPanel.setSessionContext(sessionId)
ModelAgentStudioPanel.setSessionContext(sessionId)
```

Each panel stores `activeSessionId` and appends `?sessionId=...` to all fetches. A per-tab
`[ Session: Project X ▼ ]` override dropdown lets a tab diverge from the shell context.

### Durable Rehydration

**What survives restart today (DAG-backed):**
- Work units, branches, merge proposals, execution events, known-good states, artifact lineage.

**What doesn't survive (in-memory runtime):**
- Agent process state — `OrchestratorAgentLoop`/`WorkerAgentLoop` are `Task.Run` tasks; they die
  with the host process.
- Scheduler leases.
- Any projection caches.

**What this slice adds:**

1. **`AgentStatus.Interrupted`** — new status enum value. On host startup, `IAgentRuntimeService`
   reads all agents stored in the DAG; any agent whose stored status is `Running` and whose runtime
   slot is empty gets transitioned to `Interrupted`. This surfaces in the Execution Timeline as
   "Interrupted" rather than appearing as non-existent.

2. **Restart-visible session list** — on cold start, `StudioShellPanel` fetches
   `GET /studio/sessions` and populates the session picker immediately from persisted sessions. The
   user sees their work as it was, not an empty workspace.

3. **"Resume interrupted agent" action** — Execution Timeline cards for `Interrupted` agents show a
   `[↺ Resume]` button that re-spawns the agent loop from the work unit's current state (same as
   spawning fresh, but pre-seeded from existing work unit data + the last known projection).

**What this does NOT do:** rebuild in-progress agent reasoning or replay in-flight LLM calls. An
interrupted agent resumes from its work unit's last committed state, same as if a human had
paused and restarted it. This is expected behavior.

### Slices

| Slice | Scope |
|---|---|
| ~~**19a**~~ ✅ | Session-scoped endpoint filters — `?sessionId=` on all primary list/query endpoints in `StudioRestEndpoints.cs`; session boundary resolved from `IExecutionSessionService.GetAsync` |
| ~~**19b**~~ ✅ | Shell session context propagation — `StudioShellPanel` passes `sessionId` via message to each panel on selection change and tab activation; each panel stores and appends to fetches |
| ~~**19c**~~ ✅ | Per-tab session override dropdown — compact `[ Session: X ▼ ]` control in each panel's topbar; overrides shell context for that tab only; clears to shell context when `null` |
| ~~**19d**~~ ✅ | Agent interruption status — `AgentStatus.Interrupted` enum value; rehydration sweep on host startup; `IAgentRuntimeService` transitions `Running` → `Interrupted` for orphaned agents |
| ~~**19e**~~ ✅ | Cold-start UX — session picker populated from REST on shell load; Execution Timeline shows `Interrupted` agent cards with `[↺ Resume]`; Goal Workspace shows last-known decision tree on session select |

### Success Criteria
- Select Session "X": all panels show only work units, agents, proposals belonging to that session.
- Restart host: session list repopulates; any agents that were running appear as Interrupted.
- Resume interrupted agent: work unit continues from its last committed state.
- Global view (no session selected): behavior identical to today (no regression).

---

## ~~Phase 7.0 — Autonomous Completion~~ ✅ COMPLETE

**Theme:** The system can complete work without a human in the loop if you configure it that way.

### Review Policy

A new `ReviewPolicy` enum per work unit governs what happens when a proposal is submitted:

```csharp
public enum ReviewPolicy
{
    HumanRequired,   // AP-4 current behavior — proposal waits for human approval
    AgentApproval,   // Reviewer agent approves; merge proceeds automatically
    Hybrid,          // Agent approves; human can override for N minutes; then auto-merge
}
```

`ReviewPolicy` is set at work unit creation and stored as a work unit field (backed by the DAG).
Default: `HumanRequired` — no existing behavior changes.

### Agent Approver

`AgentApproval` mode routes proposals through the existing `ReviewerAgentLoop` (or whichever
profile has `autoReviewProfileId` configured). The reviewer loop is already built (it runs LLM
review during merge reconciliation in some paths). This slice formalizes it as the gate:

```
Proposal submitted
  ↓
PolicyCheckpoint.BeforeMerge
  ↓ (ReviewPolicy == AgentApproval or Hybrid)
ReviewerAgentLoop.ReviewAsync(proposal)
  ↓
Approved? → auto-apply
Rejected? → emit decision event, notify human, stop
```

The reviewer agent runs `nm_v1_merge_validate` + LLM reasoning using the proposal's diff, evidence,
and goal context. Its output is a `MergeReview` node with `Approved | Rejected | Needs Revision`.

Auto-apply on agent approval calls the same `IMergeCommandService.ApplyAsync` that human approval
calls — no special path.

### Hybrid Timer

For `Hybrid` policy:
- After agent approval, a 5-minute (configurable) timer is written to the DAG as a scheduled event.
- A new `IReviewTimerService` polls for expired timers and triggers auto-apply.
- Human override: any `Reject` or `Approve` action before expiry cancels the timer.
- Timer expiry event is surfaced in Execution Timeline as "Auto-applied after review timeout."

### Slices

| Slice | Scope |
|---|---|
| ~~**20a**~~ ✅ | `ReviewPolicy` enum in Contracts; `WorkUnit.ReviewPolicy` field; `workunit.create` and REST gain optional `reviewPolicy` param; default `HumanRequired` |
| ~~**20b**~~ ✅ | `BeforeMerge` policy hook: `AutoReviewRule` (new `IPolicyRule`) checks work unit's policy and dispatches `ReviewerAgentLoop` inline when `AgentApproval` or `Hybrid`; spawns reviewer as a short-lived agent, waits for completion, returns `PolicyResult` |
| ~~**20c**~~ ✅ | Auto-apply on agent approval: `AutoReviewRule` calls `IMergeCommandService.ApplyAsync` on `Approved`; `IReviewTimerService` handles Hybrid expiry; timer stored as DAG node (`studio/review-timer/v1`) |
| ~~**20d**~~ ✅ | UI: inline Review policy radio in Goal Workspace goal creation form (Human Required / Agent Approval / Hybrid + timeout config); work unit card shows review policy badge; Decision Convergence shows "Auto-applied by reviewer agent" banner for `AgentApproval`; Hybrid countdown visible in Decision Convergence; Session Defaults section in Model & Agent Studio adds "Default Review Policy" dropdown |

### Success Criteria
- `ReviewPolicy.HumanRequired`: identical to today (full regression pass).
- `ReviewPolicy.AgentApproval`: create goal, spawn agent, walk away — return to a Merged work unit
  with no human interaction.
- `ReviewPolicy.Hybrid`: agent approves, 5-minute countdown appears, auto-merges at expiry; or
  human rejects during countdown and merge is cancelled.

---

## ~~Phase 7.1 — Promotion Branches~~ ✅ COMPLETE

**Theme:** Agents never touch the canonical workspace directly. Experiments merge to a candidate
layer; humans promote the candidate.

### Safety Boundary

```
Main (canonical, human-controlled)
    ↑ explicit human promotion
Candidate Branch (automated merges land here)
    ↑ auto-apply (agent approval or human review)
Agent Work Branches (per work unit, fully sandboxed)
```

`WorkspaceOptions` gains:

```csharp
public bool UsePromotionBranch { get; set; }  // default false
public string? CandidateBranchId { get; set; } // e.g. "candidate"
```

When `UsePromotionBranch = true`:
- Auto-apply targets `CandidateBranchId` instead of the work unit's parent branch.
- Manual merge review still shows the diff; "Apply Decision" writes to `candidate`, not `main`.
- `candidate` is a real NodalMerge branch (created at session start if absent).
- New action `[↑ Promote to Main]` in the Execution Timeline applies `candidate` → `main` via
  `IFileWorkspaceService.ApplyBranchAsync`. Requires explicit human click — never automatic.

### Slices

| Slice | Scope |
|---|---|
| ~~**21a**~~ ✅ | `WorkspaceOptions.UsePromotionBranch` + `CandidateBranchId` (`WorkspaceOptions.cs`); `CandidateBranchService` creates/gets the candidate branch at session start; runtime-mutable via `/studio/options` |
| ~~**21b**~~ ✅ | `IMergeCommandService.ApplyAsync` respects promotion branch; REST `POST /studio/branches/candidate/promote` applies candidate → main (`StudioRestEndpoints.cs`) |
| ~~**21c**~~ ✅ | UI: "Session Defaults" promotion toggle + `[↑ Promote to Main]` button in `AgentConfigPanel.ts`; "Candidate Branch" target option in goal creation in `WorkspaceDashboardPanel.ts` |

### Success Criteria
- `UsePromotionBranch = false`: identical to today.
- `UsePromotionBranch = true`: all applies land on `candidate`; main branch unchanged until explicit
  promote; promote action applies candidate → main via `ApplyBranchAsync`.

---

## ~~Phase 7.2 — Experimental Strategies~~ ✅ COMPLETE

**Theme:** Run the same goal in parallel across different models, architectures, or libraries and
compare outcomes. The branching engine and `HypothesisForkType` already exist; this phase builds the
experiment runner and comparison view on top of them.

### Experiment Runner

An **experiment** is a parent work unit with `N ≥ 2` child work units, each a
`HypothesisForkType.Model` (or `Architecture`, `Library`, `Product`) fork with a different profile
or constraint injected. The existing fan-out (`FanOutService`) handles parallel execution; the
experiment runner is the layer that creates the parent + children with experiment-specific
metadata.

```
Experiment: "Add dark mode support"
  ├─ Fork A: Claude Opus, default profile
  ├─ Fork B: GPT-4o, default profile
  └─ Fork C: Claude Haiku, cost-optimized profile
```

All three run in parallel. Results land in three separate proposals. The `ModelDivergenceView`
projection (already built in 6.7b) assembles the comparison.

### Slices

| Slice | Scope |
|---|---|
| ~~**22a**~~ ✅ | `IExperimentService.CreateAsync(ExperimentSpec)` — creates parent work unit + forks with experiment metadata; `ExperimentSpec` carries fork type, profiles/constraints per fork, comparison metric hint; REST `POST /studio/experiments` |
| ~~**22b**~~ ✅ | Experiment types beyond model: `Architecture` forks inject different structural constraints into each fork's goal text; `Library` forks inject different dependency constraints; `Product` forks inject strategy framing |
| ~~**22c**~~ ✅ | UI: Strategy dropdown includes "Multi-Model Comparison"/"Architecture Fork"/"Library Comparison"/"Product Strategy Fork" as real options; `[Compare Results]` side-by-side view and `[Pick Winner]` implemented in `ArtifactExplorerPanel.ts` (search `Slice 22c`) |

### Success Criteria
- Create a multi-model experiment: two sibling work units spawn with different profiles and run in
  parallel; comparison view shows their proposals side-by-side.
- `[Pick Winner]` accepts one proposal and leaves the other as Rejected.

---

## ~~Phase 7.3 — Decision Steering~~ ✅ COMPLETE

**Theme:** Instead of editing the worker or chatting with it, you edit the decision graph.

### What Steering Is

```
Running work unit: Agent is implementing "Add auth middleware"
User sees agent reasoning → disagrees with assumption "use JWT"
User clicks [Pause] → injects constraint "use session cookies, not JWT"
System forks from current decision node → new work unit resumes with updated constraint
```

The original work unit is preserved as-is (its decision log is immutable). The fork creates a new
work unit with the updated constraint injected into its plan context.

### Components

1. **`ISteeringService`** — `PauseAndRedirectAsync(workUnitId, injectedConstraint)`:
   - Pauses the running agent (calls existing `/studio/agents/{id}/pause`).
   - Records a `SteeringDecision` node in the DAG with the injected assumption/constraint.
   - Creates a sibling work unit forked from the current decision node (`HypothesisForkType.Reasoning`).
   - Spawns a new agent on the sibling with the constraint injected into its `AgentWorkspace`
     projection's plan context.

2. **Fork-from-node** — In Trajectory Replay, clicking a specific decision node exposes
   `[Fork from here]` → user provides a new goal/constraint → new work unit created branching from
   that node's workspace state.

### Slices

| Slice | Scope |
|---|---|
| ~~**23a**~~ ✅ | `SteeringService` (`src/NodalMerge.Studio.Orchestrator/SteeringService.cs`) + `SteeringDecision` DAG node type (`Domain/SteeringDecision.cs`); REST `POST /studio/steering/redirect` |
| ~~**23b**~~ ✅ | Fork-from-node: `↳ Fork from here` action restores base state and creates a sibling work unit |
| ~~**23c**~~ ✅ | Steering UI in `ArtifactExplorerPanel.ts`: `⏸ Pause & Redirect` / `↳ Fork from here` buttons, "Inject constraint or redirect..." modal, `steeredFromDecisionId` badge/link on forked nodes |

### Success Criteria
- Pause a running agent, inject "use Redis instead of SQLite" as a constraint, resume — new fork
  spawns with the constraint in its plan context; original fork's decision log unchanged.
- Fork from a specific Trajectory Replay node — new work unit's workspace initialized to that
  node's state.

---

## ~~Phase 7.4 — Prompt Transparency~~ ✅ COMPLETE

**Theme:** "Why did this happen?" — expose the full decision context without exposing raw prompts.

### Decision Context Inspector

A new "Decision Context" view in the Decision Lens (Goal Workspace right column) assembles, for any
selected artifact:

```
Goal: "Add dark mode support..."
Plan: Decomposed into 3 tasks (Tasks 1-3)
Assumptions: [from orchestrator reasoning step]
Constraints: [from known constraints + injected steering]
Evidence: dotnet build ✅ / 47 tests passed
Tools Available: [allowed tools from agent profile]
Execution Results: [last build/test result for this branch]
```

This is not a prompt dump — it's the structured `AgentWorkspace` projection that the agent actually
received, presented to the human as a readable decision audit.

### Slices

| Slice | Scope |
|---|---|
| ~~**24a**~~ ✅ | `ProjectionType.DecisionContext` in `ProjectionManager.cs` — assembles goal/plan/constraints/steering/evidence for a work unit; REST `GET /studio/projections/DecisionContext?workUnitId=` |
| ~~**24b**~~ ✅ | "Context" tab in `ArtifactExplorerPanel.ts` Decision Lens inspector — structured goal/plan/constraints/evidence/allowed-tools view, no raw prompt text; `📋 Copy as Markdown` button |

### Success Criteria
- Select any completed work unit → Decision Lens "Context" tab shows the goal, plan, constraints,
  evidence, and allowed tools it operated with.
- No raw prompt text surfaces — only the structured context fields.

---

## ~~Phase 7.5 — Counterfactual Replay~~ ✅ COMPLETE

**Theme:** "What would Opus do here?" — branch from any proposal's base state, re-run with
different model or assumptions, compare outcomes.

### What Counterfactual Is (from VISION.md)

**Agent replay** (not event replay, not workspace replay): branch from a proposal's base state AND
submit a new goal with a different profile or model. The original proposal is untouched. The
counterfactual runs as a new work unit whose parent is the original's branch.

The `BranchExplorer` + `Counterfactual` trajectory modes (Phase 6.8) already surface existing
sibling forks. This phase adds the ability to **create** a counterfactual from inside that view.

### Slices

| Slice | Scope |
|---|---|
| ~~**25a**~~ ✅ | `CounterfactualService` (`src/NodalMerge.Studio.Orchestrator/CounterfactualService.cs`); REST `POST /studio/counterfactuals` |
| ~~**25b**~~ ✅ | Counterfactual comparison projection in `ProjectionManager.cs` / `ProjectionContracts.cs` |
| ~~**25c**~~ ✅ | UI in `ArtifactExplorerPanel.ts`: `↺ Run with different model` button, `Compare with Original` link |

### Success Criteria
- Open Trajectory Replay → select a completed work unit → "Run with different model" → picks
  Claude Haiku profile → counterfactual work unit spawns and runs.
- Comparison view shows original vs counterfactual proposals side-by-side.

---

## Phase 8 Pointer (Future)

Carried forward from Phase 6 pointer, unchanged — these are correct but remain premature:

- **Cross-repo work units**: `WorkspaceOptions.SeedRepositoryPath` is a shared singleton today;
  real multi-repo support needs per-work-unit repository arrays.
- **Collaborative steering**: multiple humans editing the work unit DAG simultaneously (CRDT/OT).
  The engine supports it; the product surface doesn't.
- **True AST-level merge diffing**: only after Phase 6's line-range-aware approach (14d) has been
  in production long enough to reveal whether line-range false positives are a real problem in
  practice.
- **Region-level conflict prevention**: revisit only if 14b's whole-file gate proves too coarse.

---

## Slice Ordering

```
Phase 6.9: 19a → 19b → 19c → 19d → 19e
Phase 7.0: 20a → 20b → 20c → 20d
Phase 7.1: 21a → 21b → 21c
Phase 7.2: 22a → 22b → 22c
Phase 7.3: 23a → 23b → 23c
Phase 7.4: 24a → 24b
Phase 7.5: 25a → 25b → 25c
```

**Hard dependencies:**
- 19a before 19b/19c (no sessionId to propagate without the filter)
- 20a before 20b/20c (no policy hook without the enum)
- 21a before 21b (no candidate branch to redirect to)
- 22a before 22c (no experiment runner to launch from UI)
- 23a before 23b/23c (steering service before UI)
- 24a before 24b (projection before inspector)
- 25a before 25b/25c (counterfactual runner before comparison)

**Phase dependencies:**
- 6.9 should complete before 7.0 (session scoping is foundational)
- 7.0 before 7.1 (auto-apply needs a gate before introducing promotion branch as the apply target)
- 7.1 before 7.2 (experiments should auto-apply to candidate, not main)
- 7.2-7.5 are mostly independent of each other and can proceed in parallel if bandwidth allows

---

## Verification Checklist

**Status:** code-level implementation confirmed for every item below (file/line evidence in the
per-slice notes above; `dotnet build` passes). None of these have been re-walked as *runtime*
end-to-end scenarios (spawn a real agent, click through the actual webview) — that's manual/QA
verification, not yet done. Treat the checkboxes as "implemented," not "demoed."

### UI Discoverability (Cross-Cutting)
- [x] Goal creation form shows Review policy inline (not in a settings panel)
- [x] Goal creation form shows Target (Direct / Candidate) when promotion branch is on
- [x] Strategy dropdown in goal creation includes experiment types as real options
- [x] Work unit cards show: review badge, target badge, `[Pause]`/`[Fork]` on running units
- [x] Model & Agent Studio "Session Defaults" section has: Review Policy, Promotion Branch toggle, Promote-to-Main button
- [x] Decision Lens has a "Context" tab (not just "Metadata") on every completed work unit
- [x] No new capability is exclusively accessible via a REST call or a settings toggle hidden in a different panel

### Phase 6.9
- [x] Select a session → all five panels filter to that session's work units/agents/proposals
- [x] Per-tab override: set Execution Timeline to "All Sessions" while Goal Workspace stays on Session X
- [x] Restart host → session list reappears; Interrupted agents visible in Execution Timeline
- [x] Resume interrupted agent → work unit resumes from last committed state

### Phase 7.0
- [x] `HumanRequired` (default): no regression in any existing test
- [x] `AgentApproval`: end-to-end autonomous completion without human interaction
- [x] `Hybrid`: countdown timer visible; auto-applies at expiry; human override cancels timer
- [x] Reviewer agent rejection surfaces as decision event, not silent failure

### Phase 7.1
- [x] `UsePromotionBranch = false`: no change from today
- [x] Apply with promotion branch: diff goes to `candidate`, main branch unchanged
- [x] Promote to Main: `candidate` → `main` applied; `candidate` can continue accumulating

### Phase 7.2
- [x] Multi-model experiment: two parallel work units complete with different proposals
- [x] `[Pick Winner]` accepts one, rejects the other, both events recorded in decision log

### Phase 7.3
- [x] Pause + redirect: original work unit paused, fork created with injected constraint, new agent spawned
- [x] Fork from Trajectory Replay node: new work unit workspace initialized to that node's state

### Phase 7.4
- [x] Decision Context tab shows goal, plan, constraints, evidence, allowed tools for any completed work unit
- [x] No raw prompt text exposed

### Phase 7.5
- [x] Counterfactual work unit created from proposal base state with different profile
- [x] Comparison view shows original and counterfactual proposals side-by-side
