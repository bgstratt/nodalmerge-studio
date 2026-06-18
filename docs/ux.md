# NodalMerge Studio — UX Overview

## The Studio Shell

NodalMerge Studio opens as a single **VS Code webview panel** positioned in View Column Two (split to the right of your editor). It is accessed via the **`NodalMerge: Open Studio`** command in the Command Palette, or by pressing `F5` in the extension project.

All five views coexist inside a **tabbed shell** with a persistent tab bar at the top. The shell retains context when hidden and resumes automatically when revealed. Tabs:

| Tab | Purpose |
|---|---|
| **Home** | Session-based artifact explorer and run launcher |
| **Agent Config** | Define agent profiles, topology templates, and pipeline settings |
| **Workspace** | Real-time dashboard of work units, agents, merges, and failures |
| **Merge Review** | Inspect, validate, approve/reject, apply, and branch from merge proposals |
| **DAG Replay** | Visual DAG replay timeline with scrubber and known-good-state markers |

All views poll the Studio Host backend every 2 seconds when active and respond to WebSocket push events for live updates (stage changes, sync events).

---

## Home Tab (Artifact Explorer)

The **Home** tab is the primary launch and inspection surface. It is organized into a top bar and a three-column layout.

### Top Bar

- **Session selector** — Dropdown of all execution sessions. Selecting a session loads its work unit tree. The `(no session)` option clears the view.
- **Template selector** — Dropdown populated from your configured topology templates (set in Agent Config). Each template defines which orchestrator and workers will be used.
- **Goal textarea** — Free-form goal description. Paste a goal, pick a template, and press **Run**.
- **Run button** — Creates a work unit, an execution session, and spawns the orchestrator agent. The button disables during spawning and re-enables on completion (success or failure). On success, the goal field clears and the session auto-selects.
- **Settings gear (⚙)** — Toggles an inline settings panel with three toggles:
  - **Use LLM profile selection** — When enabled, the orchestrator asks the LLM which profile best fits each child work unit instead of relying solely on static file-scope patterns. When disabled, profiles are assigned strictly by file-scope matching and delegate routing.
  - **Max concurrent workers** — Caps the number of worker agents the scheduler will run simultaneously.
  - **Scheduler poll interval (ms)** — How frequently the scheduler checks for queued work units.

### Left Column — Work Unit Tree

Displays work units as a parent-child tree based on their `parentWorkUnitId` relationships. Each work unit shows:
- **Goal** (truncated with tooltip)
- **Status badge** (Active, Completed, Failed, Reviewing, etc.)
- **Blocked badge** — Appears when a fan-out slice was rejected by a `BeforeEnqueue` policy rule (e.g., non-overlapping file-scope constraint). Only shown while the work unit is still in `Created` status; disappears once it enqueues.
- **Stage badge** (Plan, Execute, Review, Merge) — Updates live via WebSocket as the orchestrator advances the work unit through the pipeline.
- **Proposal count**

Clicking a work unit selects it and loads its artifact timeline in the center column and the inspector in the right column.

**Right-click context menu** on any work unit node:
- **Split** — Prompts for two child goals with optional file scopes, then creates two child work units under the selected parent.
- **Re-run** — Prompts for an agent profile and re-enqueues the work unit into the scheduler.
- **Branch from latest proposal** — Finds the most recent `MergeProposal` artifact on the work unit and branches from it (prompts for a goal and profile).

### Center Column — Artifact Timeline

When a work unit is selected, this column shows a merged chronological feed of:
- **Artifacts** (Goal, Plan, Task, BranchChangeset, MergeProposal, MergeResult) — each shows its type, title, status badge, and timestamp. `MergeProposal` entries are clickable.
- **Orchestration events** — each shows the input stage, action taken, and timestamp. Clickable to reveal the full projection snapshot the orchestrator used at that decision point.

Clicking a `MergeProposal` artifact opens a **proposal inspector** in the right column.

Clicking an orchestration event opens an **event inspector** showing:
- Stage, action, orchestrator agent ID
- Spawned work unit/task IDs
- Reason for the action
- Full input projection snapshot (formatted JSON)

### Right Column — Inspector

**Work unit inspector** (shown when a work unit node is clicked):
- Status, stage (live), owner, assigned agent, branch ID, file scope, dependencies
- Goal text and success criteria
- Action buttons: Split, Re-run, Branch from latest proposal

**Proposal inspector** (shown when a timeline proposal is clicked):
- Status, source branch, confidence percentage, files touched count
- Goal text
- Actions:
  - **Open in Merge Review →** — Switches to the Merge Review tab and loads the proposal
  - **Branch from here** — Prompts for a goal and profile, then branches from this proposal
  - **Restore workspace** — Restores the workspace to the proposal's base state and opens each file as a read-only document
  - **Compare with…** — Select another proposal on the same work unit to view a two-column textual diff

**Event inspector** (shown when an orchestration event is clicked):
- Decision metadata and spawn details
- Raw input projection snapshot the orchestrator consumed

### Live Stage Updates

The Home tab opens a WebSocket connection to the Studio Host's runtime room. When the orchestrator advances a work unit's stage, the tree and inspector update in real time without polling.

---

## Agent Config Tab

The **Agent Config** tab manages all agent profiles, topology templates, pipeline profiles, and quick spawn capabilities. It has its own internal sub-tabs.

### Profiles Sub-Tab

Defines named agent profiles that map an agent type identifier to an LLM provider and model.

**Profile fields:**
| Field | Description |
|---|---|
| ID | Agent type key used when spawning (e.g., `orchestrator`, `code-worker`, `docs-agent`) |
| Display Label | Human-readable name shown in UI |
| Domain | Task domain (`code`, `docs`, `general`, `orchestration`, etc.) |
| LLM Provider | `VS Code LM`, `OpenAI compatible`, or `Anthropic` |
| Model | Model identifier dropdown (fetched from API) or manual entry. For `vscode-lm`, leave blank to use the active VS Code model |
| Base URL | API base URL (hidden for `vscode-lm`) |
| API Key | Password field with **Store Key** button (hidden for `vscode-lm`). Stored in VS Code SecretStorage |
| System Prompt Hint | Optional context passed to the agent at spawn |

The provider selector updates field visibility dynamically. Changing from VS Code LM to an API provider reveals base URL and key fields. The model dropdown refreshes from the provider's `/models` endpoint.

Profiles are displayed in a table with Edit and Delete actions. Press **Save Profiles** to persist changes to VS Code settings.

### Topology Templates Sub-Tab

Defines reusable orchestrator + worker team compositions.

**Template fields:**
| Field | Description |
|---|---|
| Name | Unique template name (e.g., `code-review-pair`) |
| Orchestrator Profile ID | References a profile from the Profiles tab |
| Worker Profile IDs | Comma-separated list of profile IDs |

Templates are displayed in a table. The **default** template is indicated with a green badge. Actions per row: **Set Default**, Edit, Delete. Press **Save Templates** to persist.

### Quick Spawn Sub-Tab

A fast-launch form for testing configurations:
- **Topology Template** — Dropdown of saved templates
- **Goal** — Free-text goal
- **Run automated review before human gate** — Checkbox that attaches a reviewer profile to the spawn

Press **► Quick Spawn** to create a work unit and spawn an orchestrator in one step. The result banner shows success or the specific error. On success, the goal field clears. If the selected profile has no credentials, a clear error message explains what's missing.

### Pipeline Profiles Sub-Tab

Defines pipeline-stage behavior for profiles on the server side:
- **Profile ID** — Agent type key (editable only on creation)
- **Name** — Display name
- **Pipeline Stage** — Orchestrate, Plan, Execute, Review, or Merge
- **Allowed Tools** — Comma-separated MCP tool names (empty = all tools)
- **File Scope Patterns** — Comma-separated glob patterns for file specialization rules
- **Max Iterations** — Agent loop iteration cap

These are persisted to the Studio Host (not VS Code settings). The table shows current server-side profiles and supports inline editing.

---

## Workspace Tab (Dashboard)

The **Workspace** tab provides an at-a-glance operational view. It polls the Studio Host every 2 seconds and drives desktop notifications for new merge proposals.

### Work Units Section

Each work unit card shows:
- Goal text, work unit ID, branch, owner
- Status badge (Active, Completed, Failed, Reviewing, etc.)
- **View Conflict →** button — Appears when status is `Reviewing` to open the conflict report in the Merge Review tab
- **Spawn** button — Pre-fills the work unit ID and prompts for an agent profile to spawn

**+ New Work Unit** button at the bottom opens a VS Code input box for the goal and owner.

### Agents Section

Each agent card shows:
- Agent ID, status badge
- **Pause** / **Resume** toggle (contextual)
- **Stop** button (danger style)
- Goal text from the associated work unit

**+ Spawn Agent** button at the bottom prompts for agent type (via profile picker) and work unit ID.

### Pending Merges Section

Each merge proposal card shows:
- Goal text, status badge, source → target branch
- **Review →** button — Opens the Merge Review tab pre-loaded with that proposal

Visible for proposals in Draft, ReadyForReview, or Approved status.

### Failures Section (Dead Letters)

Each failure card shows:
- Work unit goal, failed badge
- Stage where failure occurred, agent profile, attempt count (X/3)
- Failure reason
- **Retry** button — Available when max attempts (3) have not been reached. Removed and replaced with "Max attempts reached" otherwise.

### Desktop Notifications

When the Workspace tab detects new merge proposals, VS Code information notifications fire. Clicking a notification opens the Shell and switches to the Merge Review tab with that proposal loaded.

---

## Merge Review Tab

The **Merge Review** tab inspects a single merge proposal or conflict report. It is typically entered by:
- Clicking a **Review →** button in the Workspace or Home tab
- Clicking a desktop notification
- The shell auto-loading the latest pending proposal when you switch to this tab

### Proposal View

When loaded with a proposal (`ReadyForReview`, `Approved`, `Draft`, or `Merged`), the view shows:

**Metadata grid:**
- Status badge (color-coded: Draft = gray, ReadyForReview = blue, Approved = green, Rejected = red, Merged = purple)
- Source branch, target branch, confidence percentage

**Sections (shown conditionally):**
- **Reconciled banner** — Appears when the proposal was created by merging multiple child proposals. Shows constituent proposal list with their status badges.
- **Goal** — The goal that produced this proposal
- **Summary** — Agent-generated summary
- **Change description** — Human-readable description of changes
- **File changes** — Diff viewer with inline/split mode toggle. Each file is a collapsible `<details>` block. Click **Open Diff in Editor** to open a VS Code diff editor for that file.
- **Automated review** — Verification results (green box for approved, red box for rejected)
- **Rollback plan** — Agent-specified rollback procedure

**Action buttons (contextual by status):**

| Status | Available actions |
|---|---|
| Draft | Validate |
| ReadyForReview | Approve, Reject |
| Approved | Apply |
| Rejected, Merged | (none — read-only) |

**Universal actions** (always shown):
- **Branch from here** — Prompts for a goal and profile, then creates a new work unit branched from this proposal's base state. Useful for trying an alternative approach or a different model.
- **Restore workspace** — Restores files to the state before this proposal was made. Opens each affected file as a read-only document in the editor.

### Conflict View

When a merger detects overlapping changes among child proposals without producing a merged proposal, the parent work unit enters `Reviewing` status. The Merge Review tab shows:
- A conflict notice with the work unit ID
- The conflict report content (preformatted text from the merger)
- Instructions to resolve the conflict manually by editing files on the affected branches, then re-running the merger

---

## DAG Replay Tab

The **DAG Replay** tab provides a visual replay of the entire branch/timeline DAG. It uses an external JavaScript bundle (`dag-replay.js`) rendered in an SVG canvas.

### Toolbar

- **Status dot** — Connection indicator (idle = gray, connecting = yellow, connected = green, disconnected = gray, error = red)
- **Node count** — Total DAG nodes currently rendered

### SVG DAG

Nodes are rendered as clickable shapes. Clicking a node highlights it and reveals the **playback bar** at the bottom. Node types include branches, proposals, and known-good-state markers.

### Scrubber

A horizontal slider with current position label and branch indicator. Dragging the scrubber moves the focus position through the timeline.

### Playback Bar

Appears when a node is selected:
- **▶ Live** — Returns to live/head position
- **⎇ Branch from here** — Prompts for a goal, creates a new work unit branched from the selected node's position, and registers the new branch in the DAG
- **📌 Mark Known Good** — Prompts for a label and creates a known-good-state checkpoint at the selected node

Context menu actions (right-click on any node):
- **Branch from cursor** — Same as "Branch from here" but also captures the current cursor position as a starting point

Work units and timeline data are polled every 2 seconds, so newly spawned branches and completed proposals appear without reopening the tab.

---

## UX Data Flow

```
┌─────────────────────────────────────────────────────────┐
│ VS Code Extension (TypeScript)                          │
│                                                         │
│  StudioShellPanel  ←── one webview, 5 view instances     │
│       │                                                 │
│       ├── ArtifactExplorerPanel (Home)                   │
│       ├── AgentConfigPanel      (Agent Config)           │
│       ├── WorkspaceDashboardPanel (Workspace)            │
│       ├── MergeReviewPanel      (Merge Review)           │
│       └── DagReplayPanel        (DAG Replay)             │
│                                                         │
│  Polling: GET /studio/*  (2s interval)                  │
│  Mutations: POST /studio/*                              │
│  Live push: WebSocket ws://127.0.0.1:5080/ws/runtime    │
│  Secrets: VS Code SecretStorage API                     │
│  Settings: VS Code Configuration API                    │
│  LM API: LmApiProxy (localhost proxy for vscode-lm)     │
│                                                         │
└───────────────┬─────────────────────────────────────────┘
                │ HTTP + WebSocket
┌───────────────▼─────────────────────────────────────────┐
│ Studio Host (.NET)                                       │
│                                                         │
│  REST endpoints:                                         │
│    /studio/workunits          CRUD                       │
│    /studio/agents             spawn/pause/resume/stop    │
│    /studio/merges             propose/validate/review/   │
│                               apply/branch/restore/      │
│                               compare/file-changes/      │
│                               constituents/conflict-report│
│    /studio/sessions           list + work unit tree      │
│    /studio/workspace-summary  aggregate status           │
│    /studio/replay/timeline    branch timeline data       │
│    /studio/dead-letter        dead letter management    │
│    /studio/options            studio settings           │
│    /studio/agent-profiles     pipeline profiles         │
│    /studio/state/markKnownGood known-good-state          │
│                                                         │
└─────────────────────────────────────────────────────────┘
```

---

## Interaction Patterns

### Modal Prompts

VS Code `showInputBox` and `showQuickPick` are used for user input (goals, profile selection, branch names, labels). These appear as top-of-window modals, not inline forms.

### Inline Forms

Agent Config uses inline form panels (expand/collapse) within the webview for editing profiles, templates, and pipeline profiles.

### Status Feedback

- **Toasts** — `showInformationMessage`, `showWarningMessage`, `showErrorMessage` for operation results
- **Spinner text** — Buttons change text during async operations (e.g., `Running…`, `Spawning…`, `Loading…`)
- **Result banners** — Quick Spawn shows a temporary green/red banner

### Keyboard and Accessibility

- All buttons are keyboard-focusable
- Tab navigation within the shell cycles through the tab bar and content
- Right-click context menus on work unit nodes and DAG nodes
- Diff views support inline and split modes with persisted preference via VS Code state