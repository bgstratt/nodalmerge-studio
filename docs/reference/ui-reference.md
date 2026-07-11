# VS Code Extension UI Reference

Every user-facing action in the NodalMerge Studio VS Code extension, organized by panel. This is
the practical "what can I click" companion to the [README](../../README.md)'s conceptual overview
and to [api-reference.md](api-reference.md)'s backend catalog.

## Command Palette

| Command | What it opens |
|---|---|
| `NodalMerge: Open Studio` | The Studio shell — tabs for Goal Workspace, Activity Center, Model & Agent Studio, Decision Convergence, Pathways |
| `NodalMerge: Open Review` | Decision Convergence, scoped to a specific merge proposal |
| `NodalMerge: Open Decision Conflict` | Decision Convergence in conflict-resolution mode |
| `NodalMerge: Restart Studio Host` | Restarts the embedded .NET host process |
| `NodalMerge: Show Output` | Extension output channel (host logs) |

---

## Goal Workspace (`ArtifactExplorerPanel`)

The primary surface: create goals, watch the Decision Tree, inspect any node in the Decision Lens.

### Topbar
- **Active Exploration** — pick which session's decision tree to view
- **Exploration Strategy** — `Multi-Agent Fanout` (or another topology template) for a normal run, or one of four experiment strategies: `Multi-Model Comparison`, `Architecture Fork`, `Library Comparison`, `Product Strategy Fork`
- **Goal** — the goal text
- **▶ Run** — launches the goal under the selected strategy, review policy, and target
- **⚙ Settings** — toggles the Exploration Settings panel below

### Review / Target row (always visible under the topbar)
- **Review: Human Required / Agent Approval / Hybrid (5 min)** — sets this goal's review policy
- **Target: Candidate Branch / Direct** — only shown when promotion branches are enabled session-wide; overrides the session default per goal

### Exploration Settings panel (⚙)
- Auto-select agent profiles by capability (checkbox)
- Max concurrent workers (number)
- Scheduler poll interval, ms (number)
- Require build before proposal / Require tests before proposal (checkboxes — server-side policy gate, not a manual trigger)

### Fork config panel (appears for any of the 4 experiment strategies)
Per fork: a **Profile** dropdown and an optional **Constraint** text field (e.g. "use gRPC instead
of REST" for an Architecture Fork). **+ Add Fork** adds another entry. `Multi-Model Comparison`
picks 2 orchestrator profiles automatically and ignores the constraint field; the other three
require every fork to have a constraint (the backend rejects the request with a 400 otherwise).

### Decision Tree (left column)
- Click a node to select it (loads its timeline + Decision Lens)
- Right-click → **Fork Hypothesis**, **Re-explore**, **Fork from latest candidate**, **Fork from Known Good**
- A parent with 2+ forks shows an **N forks** badge + **Compare Results** link
- A counterfactual node shows a **Counterfactual** badge + **Compare with Original** link

### Reasoning & Execution Timeline (middle column)
Click any artifact or event to load it into the Decision Lens.

### Decision Lens (right column) — up to four tabs

**Metadata tab (always present, active by default):**
- **Fork Hypothesis** / **Re-explore** / **Fork from latest candidate** / **Fork from Known Good** — same actions as the tree's context menu. Fork from Known Good lists the node's branch's marked checkpoints (prompting for one if there's more than one), then a goal and profile, then forks a new work unit seeded from that checkpoint's content — not the branch's current, possibly-uncertain tip.
- **↺ Run with different model** (completed/merged nodes) — creates a counterfactual: re-runs this node's latest proposal under a different profile
- **⏸ Pause & Redirect** (running nodes) — pauses the agent, prompts for a constraint, forks a sibling that resumes with it
- **↳ Fork from here** (running nodes) — forks a sibling from this node's current state with a new goal + optional constraint

**Context tab (always present):**
- Loads the goal, plan, assumptions, constraints, evidence, execution results, allowed tools, and model for this node — the structured decision audit, never raw prompt text
- **📋 Copy as Markdown** — copies the above to clipboard
- Constraints proposed by domain observers appear in the Artifacts chain here, identifiable by
  their title prefix (e.g., `[SecurityAgent] Missing rate-limit on /api/auth`). See
  [docs/guides/domain-observers.md](../guides/domain-observers.md) for how observers work and
  how to enable them.

**Conversation tab (always present):**
- Full agent conversation log for this node — one entry per cycle, newest first, with tool
  calls/results as collapsible blocks and a token-usage summary. Polls live every 2s while the
  node is running.

**Decision tab (only shown when a decision candidate — a pending `MergeProposal` — exists for
this node):**
- Decision Status, Source, Confidence, Files touched, plus **Open in Review →**, **Fork Hypothesis
  from here**, **Restore workspace**, **Compare with…**
- The first time a node with a pending candidate is selected, this tab auto-activates so the
  fastest path (review the candidate) doesn't require an extra click — but it's a tab like any
  other, not a takeover: Metadata/Context/Conversation stay one click away, and re-selecting the
  same node won't re-jump you back to it. Clicking any proposal row in the Reasoning & Execution
  Timeline (not just the auto-picked candidate) also opens it here.

### Compare Results view (experiments)
- Click a fork card to select it
- **Pick Winner** — approves the selected fork's latest proposal and rejects every other fork's latest proposal (uses the same Accept/Reject path as Decision Convergence)
- **Reset** — clears the selection
- **📋 View proposals** — opens the fork's proposals

### Compare with Original view (counterfactuals)
Side-by-side original vs. counterfactual: model/provider, status, confidence, files touched, diff
summary for each, plus a "which was better" line when set.

---

## Activity Center (`WorkspaceDashboardPanel`)

Secondary surface for direct work-unit/agent lifecycle management without going through the Goal
Workspace's Decision Tree.

- **Session override** — filter this panel to one session
- **+ New Goal** — create a work unit via sequential prompts: goal → owner → review policy →
  (if promotion branches are on) target (Candidate / Direct)
- Active Goals: **Spawn** (start an agent) · **View Conflict →** (when Reviewing) · **↺ Requeue**
  (when `Cancelled` — the un-cancel, mirroring Decision Convergence's Unreject-and-Revise for a
  Rejected proposal; a leaf work unit is re-queued for a worker, a fan-out parent re-attempts
  reconciliation. Resolves fresh LLM credentials from the configured Orchestrator profile before
  requeuing, since a cancel/requeue cycle commonly spans a Host restart that wipes the in-memory
  credential cache the automated reviewer and re-enqueued workers both depend on — no live
  orchestrator loop is (re-)started as a side effect)
- Running Agents: **+ Start Agent** · **Pause** · **Resume** · **↺ Resume** (for `Interrupted`
  agents after a host restart) · **Stop**
  Agents spawned by a connected headless peer appear in this list alongside interactively spawned
  agents and can be paused, resumed, or stopped from here. The peer process itself is controlled
  externally. See [docs/guides/headless-peer.md](../guides/headless-peer.md).
- Pending Decisions: **Review Decision →**
- Blocked Explorations (dead-letter queue): **Retry** (when attempts remain) · **Continue**
  (`MaxIterationsExceeded` only, also gated on attempts remaining — resumes the same work unit
  with its own prior conversation reconstructed and a fresh iteration budget, instead of starting
  over) · **Re-plan the slice** / **Re-plan from scratch** (always available regardless of attempt
  count — decomposes the failed goal into fresh, independently-budgeted sub-slices and marks the
  original `Cancelled`, rather than resuming it)

---

## Model & Agent Studio (`AgentConfigPanel`)

Configuration surface — profiles, templates, and session-wide defaults.

### Profiles tab
Table of agent profiles with **Edit** / **Delete** per row, **+ Add Profile**. The form: ID, label,
domain, LLM provider (VS Code LM / OpenAI-compatible / Anthropic), model (with **↑ Refresh** to
fetch live model list), base URL + API key (hidden for VS Code LM), system prompt hint.
**Save Profiles** persists everything.

### Exploration Strategies tab
Topology templates (orchestrator + worker assignments): **Set Default**, **Edit**, **Delete**,
**+ Add Exploration Strategy**. **Save Strategies** persists.

### Quick Explore tab
One-off run: strategy, goal, "run automated review before human gate" checkbox, **▶ Quick Explore**.

### Pipeline Profiles tab
Stage-specific agent behavior (Orchestrate/Plan/Execute/Review/Merge): allowed tools, file-scope
patterns, max iterations, system prompt. **Edit** / **+ Add Pipeline Profile**.

### Session Defaults tab
- **Default Review Policy** dropdown
- **Use candidate branch** checkbox (session-wide promotion branch toggle)
- **↑ Promote to Main** — applies `candidate` → `main`; enabled only when the toggle above is on
- **Save Session Defaults**

> **Domain observer enable/disable is config-file only.** There is no toggle in this panel today.
> Set `Workspace:EnabledDomainAgents` in `appsettings.json` and restart the host. A per-goal
> override is available at agent spawn time via the `enabledDomainAgents` field on
> `POST /studio/agents/spawn`. See [docs/guides/domain-observers.md](../guides/domain-observers.md).

---

## Decision Convergence (`MergeReviewPanel`)

The merge-review gate. Two modes depending on the proposal/work-unit state.

### Proposal review mode
- Status badge, source/target branch, confidence, goal, summary, rationale
- Code Changes: **Inline** / **Split** diff toggle, per-file expand, **Open Diff in Editor**
- Evidence: build/test results, **download full output** when truncated
- Converged Decision section (if synthesized from multiple candidates): constituent proposal cards
- **Validate Evidence** · **Accept Decision** · **Revise** (keep the agent's current file changes,
  attach a compacted summary of the almost-correct attempt, and steer it toward the gap) ·
  **Revert and Restart** (wipe the work unit's branch back to its pre-attempt snapshot and restart
  the goal fresh with just your steering note) · **Apply Decision** · **Fork Hypothesis** ·
  **Restore workspace** (read-only pre-change files)

### Conflict resolution mode
Shows the conflict report; resolution itself happens by editing the conflicting branches outside
the panel, then re-running the merger.

---

## Pathways / Workspace History (`DagReplayPanel`)

The workspace's branchable history — "git for agent reasoning." Renders the `WorkspacePathways`
projection: goals started, integrations, rejections/dead branches, and external file updates,
each attributed to an actor (agent / human / external). Per-cycle orchestration chatter
(NoOp/Enqueue/SpawnPlanner) deliberately does not appear here — that lives in the per-goal views.
See plans/pathways-workspace-history.md for the design.

- Lanes are chronological; selecting a session **dims** out-of-session lanes rather than hiding
  them (Pathways data is always workspace-wide)
- History is **event-sourced**: a proposal that merged then was superseded by reconciliation
  keeps both moments as separate nodes (Integration → Superseded, chained), with true
  transition timestamps from the execution event log
- Node kinds render with distinct shapes/colors (legend row above the canvas); the projection's
  DAG edges draw as cross-lane connectors — a fan-out child's proposal chains to its parent's
  proposal node, not straight to the root goal
- **Sync now**: resync external repository changes on demand (uses the host's configured
  repository path), instead of waiting for the next goal creation
- **Replay Mode**: Linear / Branch Explorer / Counterfactual
- DAG canvas: click a node for the detail drawer:
  - Integration/Rejection/Superseded nodes: proposal detail, the agent conversation that produced
    it, inline file diffs + **View Diff in Editor** (read-only side-by-side), **Branch from here
    (new steering)** (counterfactual re-run from the proposal's base state with a different
    profile/goal/constraint), **Materialize to scratch workspace**
  - GoalStarted/DeadBranch nodes: goal/actor/status detail + **Materialize to scratch workspace**
  - ExternalUpdate nodes: changed-file list + **View file changes** (before/after file-level diff)
  - Materialize writes to `{extension storage}/pathways-scratch/{branch}/{timestamp}` (never
    the live repo) and offers to open it in a new window. Integration nodes carrying a
    repository snapshot get **"Materialize this point in time to scratch"** — the repo exactly
    as that integration left it (snapshot + CAS); other nodes fall back to
    **"Materialize current branch state to scratch"**, and the label says which you're getting
  - Reviewed proposals show **"Reviewed by"** (user vs reviewer-agent identity)
- Scrubber: slide through the lane's timeline (position shown as `N / Total`)
- Playback bar: **▶ Live** (jump to latest) · **⎇ Branch from here** (new work unit seeded from the
  scrubbed branch's current content) · **📌 Mark Known Good** (label + save checkpoint) ·
  **↩ Restore Known Good** (pick a marked checkpoint for this branch — confirms if there's only
  one, otherwise prompts — and restores the branch's files to that point in place)

---

## Summary

| Panel | Core job |
|---|---|
| Goal Workspace | Create goals, explore the Decision Tree, run experiments/steering/counterfactuals |
| Activity Center | Direct work-unit/agent lifecycle without the Decision Tree |
| Model & Agent Studio | Profiles, topology templates, session-wide defaults |
| Decision Convergence | The human approval gate — accept/reject/apply |
| Pathways | Workspace history — integrations/rejections/external updates, branch-from-node, materialize-to-scratch |
