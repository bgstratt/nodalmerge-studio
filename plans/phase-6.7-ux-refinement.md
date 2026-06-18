# Phase 6.7a — UX Terminology Refinement (Decision Workspace)

**Status:** Scope-Adjusted — Split from original Phase 6.7 plan after backend capability audit.

# Phase 6.7b — Decision-Centric Backend (Projections, MCP Tools, Node Schemas)

**Status:** Deferred — backend services for GoalGraph, TrajectoryTimeline, EvidenceLedger, ModelDivergenceView projections; `nm.v1.goal.*`, `trajectory.*`, `model.compare/replay`, `hypothesis.*`, `decision.*`, `evidence.*`, `reasoning.record` MCP tools; `studio/goal/v1`, `decision/v1`, `evidence/v1`, `trajectory/v1`, `hypothesis/v1`, `reasoning-commit/v1` node schemas. See end of this document for deferred items.

---

## Problem Statement

The current VS Code extension UI exposes implementation-level terminology ("Work Units", "DAG Replay", "Merge Review", "Artifact Timeline"). The architecture spec describes a decision-centric mental model (Goals, Decisions, Trajectories, Evidence, Hypothesis Forks), but the UI has not been updated to reflect this.

This phase (6.7a) performs a terminology-only renaming pass — all user-facing labels, headers, variable names, and CSS classes are updated to the decision-centric vocabulary. No new backend capabilities are introduced.

## What Already Exists (Actual Code Audit)

| Actual Capability | How It Enables This Phase |
|---|---|
| `ArtifactType` enum: `Goal`, `Plan`, `Decision`, `Research`, `Constraint`, `MergeProposal`, etc. | Timeline items can be classified by artifact type for typed labels |
| `ProjectionType.AgentWorkspace` with `Execution` field | Evidence data already flows into projections |
| `BranchExecutionResult` in `MergeProposal.VerificationResults` | Build/test results already rendered in merge review, can be shown elsewhere |
| Phase 6.6 REST endpoints: `nm_v1_workspace_exec_status`, `nm_v1_workspace_path` | Execution results queryable per branch |
| Agent profile templates (`AgentConfigService.getTemplates()`) | Strategy selector can map to existing templates |
| `StudioShellPanel` + 5 panes with scoped CSS/JS | Single-webview shell — rename panes, don't restructure |
| Existing REST endpoints for sessions, workunits, merges, agents, artifacts | All data the UI already fetches continues to work |

## Actual Gaps Found Between Spec Docs and Code

The architecture spec and MCP contract docs were forward-written with the decision-centric model, but the following were **never implemented**:

| What the Specs Claim | What Actually Exists |
|---|---|
| `GoalGraph`, `TrajectoryTimeline`, `EvidenceLedger`, `ModelDivergenceView` projections | Only 6 projection types: `WorkUnit`, `AuthoritativeState`, `Task`, `MergeProposal`, `ExecutionSnapshot`, `AgentWorkspace` |
| `nm.v1.goal.create/list` | Not in `McpToolNames.cs` |
| `nm.v1.trajectory.create/replay/fork` | Not implemented — DAG Replay uses existing `ReplayRange/ReplayRollback/ReplayInspect` |
| `nm.v1.model.compare/replay` | Not implemented |
| `nm.v1.hypothesis.fork/list` | Not implemented — no `branchType` on work units |
| `nm.v1.reasoning.record` | Not implemented |
| `nm.v1.decision.record/list` | Not implemented — but `ArtifactType.Decision` exists |
| `nm.v1.evidence.attach/list` | Not implemented — but `BranchExecutionResult` data exists |
| `studio/goal/v1`, `decision/v1`, etc. node schemas | Not in `StudioNodeKind` |

## What Phase 6.7a DOES

- Rename all 5 tab panels to decision-centric labels
- Update all HTML headers, labels, placeholders, empty states
- Update JS variable names and function names
- Update CSS class names to match new terminology
- Update TypeScript class names, property names, and method names in extension host
- Update VS Code command titles and panel title
- Leverage existing `ArtifactType` enum for timeline item classification
- Add evidence display using existing `BranchExecutionResult` data path

## What Phase 6.7a Does NOT Do

- **No new .NET services** — zero backend changes
- **No new MCP tools** — uses existing tool catalog only
- **No new node schemas** — uses existing storage
- **No new REST endpoints** — uses existing endpoints
- **No multi-model comparison UI** — requires `nm.v1.model.compare` (deferred to 6.7b)
- **No trajectory replay modes** — requires `nm.v1.trajectory.replay` (deferred to 6.7b)
- **No hypothesis fork type backend** — requires `branchType` on work units (deferred to 6.7b)

---

## Design Principles

### DP-1: Terminology Consistency

Once changed, every occurrence of old terminology is updated — panel class names, HTML labels, JS variable names, comments, and user-facing text. No partial migration.

### DP-2: No Behavioral Changes

All REST API calls, message types, and data flow remain identical. Names change; behavior does not.

### DP-3: Scoped CSS/JS Pattern Preserved

All views continue using `scopeViewCss`/`wrapViewScript` from `sharedWebviewChrome.ts`.

---

## Slice Breakdown

### Slice 17a — Rename Panel Classes and Tab Labels (Foundation)

**Files changed:**
- `src/panels/StudioShellPanel.ts` — tab definitions, import names, property names
- `src/panels/ArtifactExplorerPanel.ts` → keep file, rename exported class to `GoalWorkspacePanel`
- `src/panels/WorkspaceDashboardPanel.ts` → rename class to `ExecutionTimelinePanel`
- `src/panels/MergeReviewPanel.ts` → rename class to `DecisionConvergencePanel`
- `src/panels/DagReplayPanel.ts` → rename class to `TrajectoryReplayPanel`
- `src/panels/AgentConfigPanel.ts` → rename class to `ModelAgentStudioPanel`
- `src/extension.ts` — update panel references

**Changes:**

| Old Class | New Class | New Tab Label |
|---|---|---|
| `ArtifactExplorerPanel` | `GoalWorkspacePanel` | "Goal Workspace" |
| `AgentConfigPanel` | `ModelAgentStudioPanel` | "Model & Agent Studio" |
| `WorkspaceDashboardPanel` | `ExecutionTimelinePanel` | "Execution Timeline" |
| `MergeReviewPanel` | `DecisionConvergencePanel` | "Decision Convergence" |
| `DagReplayPanel` | `TrajectoryReplayPanel` | "Trajectory Replay" |

**Container IDs:**
| Old | New |
|---|---|
| `shell-pane-home` | `shell-pane-goal-workspace` |
| `shell-pane-workspace` | `shell-pane-execution-timeline` |
| `shell-pane-merge-review` | `shell-pane-decision-convergence` |
| (from panel) | `shell-pane-trajectory-replay` |
| (from panel) | `shell-pane-model-agent-studio` |

**Property renames in `StudioShellPanel`:**
```typescript
// Old → New
home: ArtifactExplorerPanel        → goalWorkspace: GoalWorkspacePanel
workspace: WorkspaceDashboardPanel → executionTimeline: ExecutionTimelinePanel
mergeReview: MergeReviewPanel     → decisionConvergence: DecisionConvergencePanel
agentConfig: AgentConfigPanel     → modelAgentStudio: ModelAgentStudioPanel
dagReplay: DagReplayPanel         → trajectoryReplay: TrajectoryReplayPanel
```

**Message type updates:** All `studio.tabActivated` tab IDs updated to new container IDs. `studio.showTab` message handling updated.

---

### Slice 17b — Goal Workspace (Formerly "Home") Terminology Reframe

**Files changed:** `src/panels/GoalWorkspacePanel.ts` (formerly `ArtifactExplorerPanel.ts`)

**Top Bar Changes:**
1. Label: "Session" → "Active Exploration"
2. Label: "Template" → "Exploration Strategy"
3. Goal textarea placeholder: "Paste a goal..." → "Describe a goal — e.g. Add dark mode support across the settings UI"
4. Run success toast: `"NodalMerge: Started \"${goal}\"."` → `"Goal created: ${goal}"`

**Left Column (Decision Tree):**
1. Header: `"Work Units"` → `"Decision Tree"`
2. Empty state: `"Select a session to view its work unit DAG."` → `"Create a goal to start exploring decisions."`
3. Variables: `workUnits` → `decisionNodes`, `selectedWorkUnitId` → `selectedNodeId`
4. Functions: `renderWorkUnits()` → `renderDecisionTree()`, `renderWorkUnitInspector()` → `renderDecisionInspector()`
5. Node context menu action label: `"Split"` → `"Fork Hypothesis"`
6. Node action buttons: `"Re-run"` → `"Re-explore"`, `"Branch from latest proposal"` → `"Fork from latest candidate"`

**Center Column (Timeline):**
1. Header: `"Artifact Timeline"` → `"Reasoning & Execution Timeline"`
2. Empty state: `"Select a work unit to see its artifacts."` → `"Select a decision node to see its reasoning and execution timeline."`
3. Timeline item labels keyed on `ArtifactType`:
   - `MergeProposal` → `📐 Decision Candidate`
   - `Decision` / `DecisionLog` → `🧠 Reasoning Step`
   - `Plan` → `📐 Plan Proposal`
   - `Research` → `🔍 Research`
   - `Goal` → `🎯 Goal`
   - `Task` → `📋 Task`
   - `BranchChangeset` → `📁 Code Change`
   - `MergeResult` → `✅ Merged`
   - Orchestration event → `🤖 Agent Action`

**Right Column (Decision Lens):**
1. Header: `"Inspector"` → `"Decision Lens"`
2. Empty state: `"Nothing selected."` → `"Select a decision node or timeline item to inspect."`
3. Metadata labels:
   - `"Status"` → `"Decision Status"`
   - `"Owner"` → `"Initiator"`
   - `"Agent"` → `"Executor"`
   - `"Branch"` → `"Hypothesis Fork"`
   - `"Stage"` → `"Phase"`
   - `"File scope"` → `"File scope"` (unchanged — concrete)
   - `"Depends on"` → `"Depends on"` (unchanged)
4. Proposal inspector: `"Open in Merge Review →"` → `"Open in Decision Convergence →"`
5. Proposal action: `"Branch from here"` → `"Fork Hypothesis from here"`

**Settings Panel:**
1. Gear button tooltip: `"Settings"` → `"Exploration Settings"`
2. Checkbox: `"Use LLM profile selection (orchestrator asks the LLM which profile fits each child work unit)"` → `"Auto-select agent profiles by capability"`

---

### Slice 17c — Model & Agent Studio (Formerly "Agent Config") Terminology Reframe

**Files changed:** `src/panels/ModelAgentStudioPanel.ts` (formerly `AgentConfigPanel.ts`)

**Changes:**
1. Subheader: Add `"Configure models, agent profiles, and exploration strategies."`
2. Templates section header: `"Templates"` → `"Exploration Strategies"`
3. Template field labels: `"Orchestrator"` → `"Planner Profile"`, `"Workers"` → `"Executor Profiles"`
4. Button label: `"Quick Spawn"` → `"Quick Explore"`
5. Variable/function renames in the JS string: all internal vars using old naming updated

---

### Slice 17d — Execution Timeline (Formerly "Workspace") Terminology Reframe

**Files changed:** `src/panels/ExecutionTimelinePanel.ts` (formerly `WorkspaceDashboardPanel.ts`)

**Changes:**
1. Header: `"NodalMerge Studio"` → `"Execution Timeline"`
2. Section headers:
   - `"Work Units"` → `"Active Goals"`
   - `"Agents"` → `"Running Agents"`
   - `"Pending Merges"` → `"Pending Decisions"`
   - `"Failures"` → `"Blocked Explorations"`
3. Buttons: `"+ New Work Unit"` → `"+ New Goal"`, `"+ Spawn Agent"` → `"+ Start Agent"`
4. Work unit cards: branch shown as `"fork: {branchId}"`
5. Agent cards: show profile name in addition to agent ID
6. Merge cards: `"Review →"` → `"Review Decision →"`
7. Dead letter cards: `"stage:"` → `"phase:"`, `"profile:"` → `"model:"`
8. Empty states:
   - `"No work units yet."` → `"No active goals."`
   - `"No agents."` → `"No running agents."`
   - `"No merge proposals."` → `"No pending decisions."`
   - `"No failures."` → `"No blocked explorations."`
9. JS functions: `renderWorkUnits()` → `renderActiveGoals()`, `renderMerges()` → `renderPendingDecisions()`, `renderFailures()` → `renderBlockedExplorations()`

---

### Slice 17e — Decision Convergence (Formerly "Merge Review") Terminology Reframe

**Files changed:** `src/panels/DecisionConvergencePanel.ts` (formerly `MergeReviewPanel.ts`)

**Changes:**
1. Loading text: `"Loading proposal…"` → `"Loading decision candidate…"`
2. Title: `"Merge Review: {branch}"` → `"Decision Convergence: {goal}"`
3. Metadata labels:
   - `"Source branch"` → `"Hypothesis Fork"`
   - `"Target branch"` → `"Target"` (unchanged)
   - `"Confidence"` → `"Confidence"` (unchanged)
   - `"Status"` → `"Decision Status"`
4. Section headers:
   - `"Change description"` → `"Rationale"`
   - `"File changes"` → `"Code Changes"`
   - `"Verification"` → `"Evidence"`
5. Reconciled banner: `"Reconciled proposal — combined from N child proposal(s)."` → `"Converged decision — synthesized from N candidate(s)."`
6. Action buttons:
   - `"Validate"` → `"Validate Evidence"`
   - `"Approve"` → `"Accept Decision"`
   - `"Reject"` → `"Reject Decision"`
   - `"Apply"` → `"Apply Decision"`
   - `"Branch from here"` → `"Fork Hypothesis"`
7. Conflict view: `"Merge conflict"` → `"Decision Conflict"`
   - Description: `"Resolve manually..."` → `"Resolve conflicting hypotheses manually..."`
8. CSS badge classes (additional selectors, old ones kept for backward compat):
   - `.badge.draft` also matched by `.badge.exploring`
   - `.badge.readyforreview` also matched by `.badge.proposed`
   - `.badge.approved` also matched by `.badge.accepted`
   - `.badge.merged` also matched by `.badge.converged`

---

### Slice 17f — Trajectory Replay (Formerly "DAG Replay") Terminology Reframe

**Files changed:** `src/panels/TrajectoryReplayPanel.ts` (formerly `DagReplayPanel.ts`)

**Changes:**
1. Panel description text: add subheader `"Replay the evolution of decisions through the goal → decomposition → execution → convergence lifecycle."`
2. The existing replay functionality (linear DAG timeline) is now labeled as "Linear Replay" mode
3. Node event labels in tooltips/display:
   - `"workunit.create"` → `"Goal Created"`
   - `"agent.spawn"` → `"Agent Started"`
   - `"merge.propose"` → `"Decision Proposed"`
   - `"merge.apply"` → `"Decision Applied"`
   - `"merge.reject"` → `"Decision Rejected"`
4. Playback bar: add model badge on each node when `agentId` is available (can be mapped to profile)
5. Note: replay mode selector (Branch Explorer, Model Comparison, Counterfactual) — deferred to 6.7b

---

### Slice 17g — Evidence Display in Decision Lens

**Purpose:** Surface execution evidence (build/test results from Phase 6.6) in the Goal Workspace inspector and Decision Convergence view.

**Data source (already exists):**
- `BranchExecutionResult` in `MergeProposal.VerificationResults` (parsed JSON already rendered in merge review)
- `AgentWorkspaceProjectionPayload.Execution` field (from Phase 6.6)
- REST: `GET /studio/workspace/{branchId}/exec/latest`

**Changes:**
1. In Goal Workspace inspector (right column): when a decision node is selected, fetch execution status for its branch and show a small "Evidence" section below the metadata
2. In Decision Convergence: execution section already exists (from Phase 6.6 slice 16h) — just update section header from `"Verification"` → `"Evidence"`
3. Evidence section UI (compact, in inspector):
   ```
   Evidence
     dotnet build: ✅  dotnet test: ✅ (47/47)
   ```

---

### Slice 17h — Reasoning-Aware Timeline Classification

**Purpose:** Use existing `ArtifactType` enum values to render timeline items with typed labels and icons.

**Existing artifact types (from `ArtifactRef.cs`):**
- `Goal` → `🎯 Goal`
- `Plan` → `📐 Plan Proposal`
- `Decision` → `🧠 Reasoning Step`
- `Research` → `🔍 Research`
- `Constraint` → `🔒 Constraint`
- `Task` → `📋 Task`
- `BranchChangeset` → `📁 Code Change`
- `MergeProposal` → `📐 Decision Candidate`
- `MergeResult` → `✅ Merged`

**Implementation:** Replace the current timeline rendering (which shows raw `a.type` as label) with a `classifyArtifact()` function that maps `ArtifactType` → `{ label, icon }`. Events keep their orchestration event label.

---

### Slice 17i — Exploration Strategy Selector

**Purpose:** Rename the template selector to "Exploration Strategy" with descriptive labels.

**Existing templates** (from `AgentConfigService.getTemplates()`) map directly:
- Any single-profile template → "Single Agent"
- Multi-worker template → "Multi-Agent Fanout"
- Research-oriented template → "Research Only"
- Architecture-oriented template → "Architecture Review"

**Note:** "Multi-Model Comparison" strategy is not available in 6.7a (requires backend). It can be added to the dropdown as disabled with a tooltip "Coming soon — requires multi-model comparison backend."

**Changes:**
1. HTML: `id="ex-template"` → `id="ex-strategy"`, label `"Template"` → `"Strategy"`
2. JS: variable/event handler references updated
3. TypeScript: `sendTemplates()` renamed `sendStrategies()`, `handleRun(templateName, goal)` renamed `handleRun(strategy, goal)`
4. Message type `'templates'` → `'strategies'`

---

### Slice 17j — CSS Class Cleanup

**Purpose:** Rename CSS classes to match new terminology, keeping old selectors for backward compatibility during transition.

**File:** All panel TS files (CSS is embedded as template strings in each panel file)

**Renames:**
| Old CSS Class | New CSS Class |
|---|---|
| `.ex-col-tree` | `.gw-decision-tree` |
| `.ex-col-timeline` | `.gw-timeline` |
| `.ex-col-inspector` | `.gw-inspector` |
| `.ex-topbar` | `.gw-topbar` |
| `.ex-field` | `.gw-field` |
| `.ex-body` | `.gw-body` |
| `.ex-settings-panel` | `.gw-settings-panel` |
| `.ex-settings-row` | `.gw-settings-row` |
| `.wu-node` | `.dn-node` |
| `.wu-title` | `.dn-title` |
| `.wu-meta` | `.dn-meta` |

**Kept as-is:** `.tl-item`, `.tl-kind`, `.tl-title`, `.tl-time` (timeline classes are generic), `.badge.*`, `.empty`, `.card`, `.row`, `.title`, `.mono`

---

### Slice 17k — Shell Window Title, Command Names, and Outward-Facing Text

**Files changed:** `src/extension.ts`, `package.json`

1. Webview panel title: `"NodalMerge Studio"` → `"NodalMerge Studio — Decision Workspace"`
2. Command titles (in `package.json` `contributes.commands`):
   - `nodalmerge.openStudio`: `"Open NodalMerge Studio"` → `"Open Decision Workspace"`
   - `nodalmerge.openMergeReview`: `"Open Merge Review"` → `"Open Decision Convergence"`
3. Extension display name and output channel name: unchanged

---

## Implementation Order

```
17a → Rename panel classes and tab labels (foundation — must be first)
17b → Goal Workspace terminology reframe (largest single change)
17c → Model & Agent Studio reframe
17d → Execution Timeline reframe
17e → Decision Convergence reframe
17f → Trajectory Replay reframe
17g → Evidence display in Decision Lens (uses existing data)
17h → Reasoning-aware timeline classification (uses existing ArtifactType)
17i → Exploration Strategy selector
17j → CSS class cleanup (follows all other changes)
17k → Shell window title and command names (final polish)
```

---

## Verification Checklist

- [ ] All 5 tabs display correct new labels
- [ ] Goal Workspace: "Decision Tree" header, decision node rendering works
- [ ] Goal Workspace: Timeline shows typed items with icons from ArtifactType
- [ ] Goal Workspace: Inspector shows "Decision Lens" header and updated field labels
- [ ] Model & Agent Studio: shows "Model & Agent Studio" header, strategies section
- [ ] Execution Timeline: shows "Active Goals", "Running Agents", "Pending Decisions", "Blocked Explorations"
- [ ] Decision Convergence: shows "Decision Convergence" title, "Accept Decision"/"Reject Decision"/"Apply Decision" buttons
- [ ] Trajectory Replay: shows "Trajectory Replay" tab, linear replay works, event labels updated
- [ ] Evidence display: shows build/test status in inspector when execution results available
- [ ] Exploration Strategy selector: shows strategy labels, creates work units correctly
- [ ] Shell window title: "NodalMerge Studio — Decision Workspace"
- [ ] All existing functionality works: create work units, spawn agents, review merges, replay DAG
- [ ] No TypeScript compilation errors
- [ ] Webview renders without console errors (no broken selectors)
- [ ] CSS scoping works correctly for all views
- [ ] All REST API calls continue to work (no backend changes)
- [ ] Existing .NET tests pass (no backend changes)
- [ ] Dead letter actions still work
- [ ] Notification manager still shows merge proposal notifications
- [ ] Branch workspace operations (open folder, open terminal) still work

---

## Deferred to Phase 6.7b (Backend + New UX Features)

These items require new .NET services, MCP tools, projections, or node schemas that don't exist yet:

| Deferred Item | What's Needed |
|---|---|
| Multi-Model Comparison UI (slice 17g) | `ModelDivergenceView` projection, `nm.v1.model.compare`, `nm.v1.model.replay` MCP tools |
| Trajectory replay modes (Branch Explorer, Model Comparison, Counterfactual) | `nm.v1.trajectory.replay` with mode parameter |
| Hypothesis fork type backend (code/reasoning/model/research/architecture/product) | `branchType` field on work units, `nm.v1.hypothesis.fork` MCP tool |
| Goal as first-class entity | `studio/goal/v1` node schema, `nm.v1.goal.create/list` MCP tools |
| Decision nodes with model/confidence/disagreement fields | `studio/decision/v1` node schema, `nm.v1.decision.record/list` MCP tools |
| Evidence ledger | `studio/evidence/v1` node schema, `EvidenceLedger` projection, `nm.v1.evidence.attach/list` MCP tools |
| Reasoning commit graph | `studio/reasoning-commit/v1` node schema, `nm.v1.reasoning.record` MCP tool |
| Trajectory replay engine | `studio/trajectory/v1` node schema, `TrajectoryTimeline` projection |
| GoalGraph projection for decision tree | New `ProjectionType.GoalGraph` |
| Multi-Model strategy in exploration selector | Backend model comparison plumbing |
