# Phase 6.8 — Unified UX (Decision Workspace Integration)

## Status of Phase 6.7a (Terminology Rename)

**Fully complete.** Verified against actual code:

| Item | State |
|---|---|
| Shell title: "NodalMerge Studio — Decision Workspace" | ✅ `extension.ts:76` |
| Tabs: Goal Workspace, Model & Agent Studio, Execution Timeline, Decision Convergence, Trajectory Replay | ✅ `StudioShellPanel.ts:116-122` |
| Container IDs: `shell-pane-goal-workspace`, etc. | ✅ all 5 panels |
| TypeScript property names: `goalWorkspace`, `executionTimeline`, `decisionConvergence`, `modelAgentStudio`, `trajectoryReplay` | ✅ `StudioShellPanel.ts:25-29` |
| Goal Workspace HTML: "Decision Tree", "Reasoning & Execution Timeline", "Decision Lens", "Active Exploration", "Exploration Strategy" | ✅ `ArtifactExplorerPanel.ts:534-578` |
| `classifyArtifact()` with typed labels (🧠 Reasoning Step, 📐 Decision Candidate, etc.) | ✅ `ArtifactExplorerPanel.ts:612-625` |
| Execution Timeline HTML: "Active Goals", "Running Agents", "Pending Decisions", "Blocked Explorations", "+ New Goal", "+ Start Agent" | ✅ `WorkspaceDashboardPanel.ts:354-373` |
| Decision Convergence CSS: `.badge.exploring`, `.badge.proposed`, `.badge.accepted`, `.badge.converged`, `.converged-banner` | ✅ `MergeReviewPanel.ts:336-340` |
| Strategy selector → "Exploration Strategy", message type `'strategies'` | ✅ `ArtifactExplorerPanel.ts:119-143` |
| Action verbs: "Fork Hypothesis", "Re-explore", "Accept Decision", "Reject Decision", "Apply Decision", "Validate Evidence" | ✅ across message handlers |
| Merge Review → Decision Convergence panel class name | ✅ `DecisionConvergencePanel` |

No Bucket A work remains. Phase 6.7a shipped in full.

## What Gap Remains

The backend for the decision-centric model is complete (domains, services, MCP tools, REST routes, projections). The gap is in the UI — the data exists but no panel consumes it. These fall into two tiers:

### Tier 1 — Data-connected UI (backend exists, REST routes exist, zero frontend)

| Feature | Backend | REST Route | Current UI State |
|---|---|---|---|
| **Multi-model comparison** | `ModelDivergenceView` projection, `ModelTools.cs` | `GET /studio/models/compare?proposalIdA=&proposalIdB=`, `GET /studio/models/replay/{workUnitId}` | `_multi_model_` strategy disabled with "Coming soon" tooltip |
| **GoalGraph DAG visualization** | `GoalGraphProjectionPayload` | `GET /studio/projections/GoalGraph` | Decision Tree is a flat list only |
| **Trajectory Replay modes** | BranchExplorer, Counterfactual in `TrajectoryTools` | `GET /studio/trajectory/replay?mode=BranchExplorer\|Counterfactual` | Only linear timeline replay |
| **Evidence in Goal Workspace** | `EvidenceLedger` projection | `GET /studio/evidence?workUnitId=`, `POST /studio/evidence/attach` | Only inline in Decision Convergence execution section |
| **Generalized hypothesis fork types** | `HypothesisForkType` enum, `WorkUnit.ForkType`, `HypothesisTools` | `POST /studio/hypotheses/fork` | Fork UI only asks for goal text + file scope, no type selector |

### Tier 2 — New capabilities (no backend yet)

| Feature | What's Needed |
|---|---|
| **Reasoning Commit Graph** | New projection type that queries `IOrchestrationDecisionLogService` + `IDecisionNodeService` across a work unit tree to produce a real graph of reasoning → model → execution → convergence edges |
| **Full convergence panel with competing hypotheses** | Current Decision Convergence only shows one proposal's diff + evidence. A converged view should show all constituent hypotheses side-by-side with model, confidence, rationale, and evidence per candidate. |

---

## Architecture Principle: Same Transport Pattern as Phase 6.5

Every new UI feature follows the same three-layer pattern already established:

```
Panel (TS fetch) → REST endpoint (StudioRestEndpoints.cs) → Shared service (existing)
```

No direct MCP calls from the extension. The REST routes added in Phase 6.5 gap closure already exist for every feature in Tier 1.

---

## Slice 18a — Enable Multi-Model Comparison Strategy

The smallest change with the highest conceptual impact — unblocks the `_multi_model_` strategy and wires it to existing backend.

### Changes

1. **`AgentConfigService.ts`**: Add multi-model comparison as a real template type, not a disabled pseudo-template. A multi-model run sends the same goal to two orchestrator profiles (different models) simultaneously via the existing `POST /studio/workunits` + `POST /studio/agents/spawn` pattern, creating siblings under a shared parent.

2. **`ArtifactExplorerPanel.ts`**: Remove the `_multi_model_` hardcoded disabled entry from `sendStrategies()`. Instead, rely on `AgentConfigService.getTemplates()` returning a real multi-model template when the user has configured at least two orchestrator profiles with different models.

3. **`ArtifactExplorerPanel.ts`**: Add a "Model Comparison" inspector mode — when two sibling work units exist under the same parent, fetch `GET /studio/models/compare?proposalIdA=&proposalIdB=` and render diverged files with diff-A / diff-B side-by-side.

4. **`ArtifactExplorerPanel.ts`**: "Multi-Model Comparison" becomes a first-class exploration strategy label in the strategy dropdown, on par with "Single Agent" and "Multi-Agent Fanout".

**Verification:** Configure two orchestrator profiles (e.g., GPT-5-mini + Claude Opus). Create a multi-model run. Two sibling work units are created. After both propose, the timeline shows model divergences via `model.compare`. The strategy dropdown shows "Multi-Model Comparison" as a live option, not a disabled placeholder.

---

## Slice 18b — GoalGraph DAG Visualization in Goal Workspace

Replaces the flat decision tree list in the Goal Workspace left column with a real DAG.

### Backend
- `GoalGraphProjectionPayload` already contains `IReadOnlyList<GoalGraphNode>` with `GoalId`, `Goal`, `WorkUnitId`, `BranchId`, `Status`, `ParentGoalId`, `ChildGoalIds`, `Owner`, `AssignedAgent`, `ProposalCount`, `CreatedAt`.
- REST: `GET /studio/projections/GoalGraph?workUnitId=&branchId=`

### Frontend Changes

1. **`ArtifactExplorerPanel.ts`**: Add a `refreshGoalGraph()` method that fetches `GET /studio/projections/GoalGraph` for the selected session's work units.

2. **`GW_JS`**: Replace the current `renderDecisionTree()` (which produces a flat `<div class="dn-node">` list) with a DAG renderer:
   - Each node is a card showing: goal fragment (truncated), status badge, model badge, child count.
   - Edges are drawn as CSS borders / connector lines between parent and children (no canvas — pure CSS grid with hierarchical indent + connector, same approach the existing DAG Replay SVG already uses but simplified).
   - Nodes are sorted by creation time, with parent→child indentation.
   - Clicking a node loads its timeline + inspector.
   - A node with `forkType` shows a fork-type badge.

**Verification:** Open a session with fanned-out children. The left column shows a hierarchical DAG instead of a flat list. Nodes have parent→child indentation. Clicking a node loads its timeline. Fork-type badges appear on hypothesis forks.

---

## Slice 18c — Evidence in Goal Workspace Inspector

Surfaces build/test evidence in the Decision Lens (right column) when a node is selected.

### Backend
- `GET /studio/evidence?workUnitId=` returns build/test evidence entries.
- `GET /studio/workspace/{branchId}/exec/latest` returns `BranchExecutionResult`.

### Frontend Changes

1. **`ArtifactExplorerPanel.ts`**: When a work unit is selected and the inspector renders, also fetch `GET /studio/evidence?workUnitId=`.

2. **`GW_JS`** `renderDecisionInspector()`: Add an "Evidence" section below the metadata grid when evidence exists:
   ```
   Evidence
     dotnet build: ✅ passed   dotnet test: ✅ 47/47
   ```
   Each evidence entry is a compact row with a status icon + summary. No expandable output — that stays in Decision Convergence.

**Verification:** Select a work unit that has build/test results. The Decision Lens shows an "Evidence" section with build/test status rows.

---

## Slice 18d — Trajectory Replay Mode Selector

Adds Branch Explorer and Counterfactual modes to the Trajectory Replay tab.

### Backend
- `GET /studio/trajectory/replay?mode=BranchExplorer` returns branches grouped by branch ID with goal lists.
- `GET /studio/trajectory/replay?mode=Counterfactual&workUnitId=` returns sibling/forked alternatives.
- `GET /studio/trajectory/replay?mode=Linear` (default) returns the existing linear timeline.

### Frontend Changes

1. **`DagReplayPanel.ts`**: Add a mode selector dropdown (Linear / Branch Explorer / Counterfactual) above the replay view.

2. **`dag-replay/main.ts`** (or inline in the panel): Handle mode-switch messages to fetch the appropriate trajectory endpoint and re-render.

3. **Branch Explorer view**: Each branch becomes a collapsible group showing its work units in order.

4. **Counterfactual view**: Shows the target work unit and its sibling/alternative timelines side-by-side as cards with goal, status, fork type, and creation time.

**Verification:** The Trajectory Replay tab shows a mode dropdown. Switching to Branch Explorer groups work units by branch. Switching to Counterfactual shows sibling alternatives for a given work unit.

---

## Slice 18e — Generalized Hypothesis Fork Type Selector

When the user forks a hypothesis, let them choose the fork type.

### Backend
- `HypothesisForkType` enum: `Code`, `Reasoning`, `Model`, `Research`, `Architecture`, `Product`.
- `WorkUnitCreateCommand.ForkType` accepts it.
- `POST /studio/hypotheses/fork` accepts `forkType`.

### Frontend Changes

1. **`ArtifactExplorerPanel.ts`** `handleWorkUnitAction('forkHypothesis')`: After collecting goals, show a quick-pick for `HypothesisForkType` (Code / Reasoning / Model / Research / Architecture / Product). Pass `forkType` to the work unit creation call.

2. **`GW_JS`**: The "Fork Hypothesis" action button now triggers the type picker flow instead of immediately asking for goals. Or: add a dropdown next to the fork action. Prefer quick-pick for minimal HTML change.

**Verification:** Right-click a node → "Fork Hypothesis". A quick-pick appears with fork types. Selecting one passes `forkType` to the creation, and the new node shows a fork-type badge in the tree.

---

## Slice 18f — Reasoning Commit Graph (Tier 2 — new backend)

A new projection that builds a real reasoning → model → execution → convergence graph from orchestration decision log events, decision nodes, and execution results.

### Backend (new)

1. **New `ProjectionType.ReasoningCommitGraph`** in `ProjectionContracts.cs`.

2. **New payload**: `ReasoningCommitGraphProjectionPayload` with `IReadOnlyList<ReasoningCommitNode>` and `IReadOnlyList<ReasoningCommitEdge>` where:
   ```csharp
   public sealed record ReasoningCommitNode(
       string CommitId, string WorkUnitId, string AgentId,
       string Stage, string Action, string? Reasoning,
       string? AgentModel, string? AgentProvider,
       DateTimeOffset OccurredAt);

   public sealed record ReasoningCommitEdge(
       string FromCommitId, string ToCommitId, string EdgeType);
       // EdgeType: Refine, Fork, Replace, Merge, Invalidate, EvidenceAttached
   ```

3. **`ProjectionManager.BuildReasoningCommitGraphAsync`**: Queries `IOrchestrationDecisionLogService` for all events across a work unit tree, queries `IDecisionNodeService` for decisions, queries `IWorkspaceExecutionCommandService` for evidence, and builds nodes + edges.

### Frontend (new)

4. **New panel** or new tab in Trajectory Replay: "Reasoning Graph" mode. Renders nodes as a vertical git-log-like timeline where each commit shows: agent, model, stage, action, reasoning excerpt. Edges are drawn as CSS connector lines. Clicking a node opens its full reasoning in the inspector.

5. **Alternatively**: integrate into the existing Goal Workspace Decision Lens as a "Reasoning Chain" view when a node is selected.

**Note**: This slice is larger than 18a-18e combined. It is the "backbone upgrade" described in the unified UX vision. Defer if 18a-18e consume the available bandwidth; ship as a standalone follow-up phase.

---

## Slice 18g — Converged Decision View Enhancement

Enhances the Decision Convergence panel to show all constituent hypotheses side-by-side when viewing a reconciled/converged proposal.

### Backend (exists)
- `GET /studio/merges/{proposalId}/constituents` already returns constituent proposals with status/goal/summary.
- `GET /studio/models/compare?proposalIdA=&proposalIdB=` compares two proposals.

### Frontend Changes

1. **`MergeReviewPanel.ts`**: When rendering a converged proposal (one with `reconciledFrom`), expand the constituent list from the current bare row to cards showing per-constituent: model, confidence, rationale (from `decision.record`), build/test evidence (from `GET /studio/evidence`), and a mini diff summary (reuse existing hunk render).

2. **`DC_HTML`**: Add a "Constituent Hypotheses" section above the converged diff, showing the side-by-side candidate comparison.

**Verification:** Open a reconciled proposal. The panel shows constituent hypotheses as cards with model, confidence, evidence badges, and rationale excerpts.

---

## Discussion: What Truly Remains vs. What Already Exists

The unified UX vision describes a reasoning commit graph as "the backbone upgrade." The existing orchestration decision log (`IOrchestrationDecisionLogService`) already records every agent action with stage, action, reasoning, and agent identity — it *is* the reasoning commit data. What's missing is a projection that assembles it into a graph shape with typed edges, and a panel that renders it.

The "generalized branching" (Code/Reasoning/Model/Research/Architecture/Product fork types) is mostly done: `HypothesisForkType` exists, `WorkUnit.ForkType` is persisted, `HypothesisTools` can create typed forks. Only the UI type selector (Slice 18e) is missing.

"Models become participants in a decision graph" — the `ModelDivergenceView` projection and model comparison tools exist. The UI for comparing model outputs (Slice 18a) is the missing piece.

## Slice Ordering

```
18a (Multi-model) → 18b (GoalGraph DAG) → 18c (Evidence) → 18d (Trajectory modes) → 18e (Fork types) → 18g (Convergence enhancement)
```

18f (Reasoning Commit Graph) is a larger standalone effort — schedule after 18a-18e if bandwidth permits, or as a dedicated follow-up phase.

18a first because it unblocks the `_multi_model_` feature that's already visible in the UI as "Coming soon" — highest user-visible impact for smallest code change.

---

## Verification Checklist

- [ ] Multi-model comparison strategy is a selectable, working option (not disabled)
- [ ] `/studio/models/compare` is called when two sibling proposals exist and renders diverged files
- [ ] Goal Workspace left column shows a hierarchical DAG with parent-child indentation
- [ ] Goal Workspace Decision Lens shows evidence (build/test results) when available
- [ ] Trajectory Replay tab has a mode selector (Linear / Branch Explorer / Counterfactual)
- [ ] Branch Explorer groups work units by branch
- [ ] Counterfactual shows sibling/alternative timelines
- [ ] Hypothesis fork type selector appears when forking (quick-pick with types)
- [ ] New nodes show fork-type badge
- [ ] Converged decision view shows constituent hypothesis cards
- [ ] All existing panels continue to function (no regressions)
- [ ] All existing .NET tests pass
- [ ] TypeScript compiles without errors
- [ ] Webview renders without console errors