# Slice 7b — Workspace Dashboard Panel

Status: **Complete**

## Problem

The Studio Host is running (Slice 7a), but there is no UI showing the current state of the workspace — active work units, spawned agents, pending merge proposals, or failures.

## Architecture

The dashboard is a VS Code WebView panel. The extension host (TypeScript) owns the polling loop and passes data to the WebView via `postMessage`. The WebView is pure display — it receives typed messages and renders HTML.

```
Extension host (TS)
  WorkspaceDashboardPanel
    ├─ poll /studio/workspace-summary every 2s (HTTP GET)
    ├─ on data → panel.webview.postMessage({ type: 'summary', data })
    └─ on command button click ← panel.webview.onDidReceiveMessage

WebView (HTML + vanilla JS or lightweight React)
    ├─ renders WorkUnit list (goal, status, branch, assigned agent)
    ├─ renders Agent list (agentId, workUnitId, status)
    ├─ renders Pending Merges list (proposalId, branch, status)
    ├─ renders Failures + Known Good States
    └─ action buttons → postMessage back to extension host
```

## New REST endpoint on Studio Host

The WebView calls HTTP — not MCP tools directly — to keep the WebView simple and avoid MCP parsing in the browser context. Add a lightweight REST endpoint to `StudioWebApplication.cs`:

```
GET /studio/workspace-summary?branchId={optional}
```

Returns the same data as `nm.v1.workspace.summary` but as plain JSON:

```json
{
  "activeWorkUnits": ["wu-abc", "wu-def"],
  "activeAgents": ["worker-xyz"],
  "pendingMerges": ["mp-1"],
  "failures": [],
  "knownGoodStates": []
}
```

Also add:

```
GET  /studio/workunits           — IWorkUnitService.ListAsync()
GET  /studio/workunits/{id}      — IWorkUnitService.GetAsync()
POST /studio/workunits           — IOrchestratorService.CreateWorkUnitAsync()
GET  /studio/agents              — IAgentControlService.ListActiveAsync()
POST /studio/agents/spawn        — IAgentControlService.SpawnAsync()
POST /studio/agents/{id}/pause   — IAgentControlService.PauseAsync()
POST /studio/agents/{id}/resume  — IAgentControlService.ResumeAsync()
POST /studio/agents/{id}/stop    — IAgentControlService.StopAsync()
```

These are thin wrappers over the existing service layer — no new service logic.

## Files touched

### Updated: `src/NodalMerge.Studio.Host/StudioWebApplication.cs`

Add the REST endpoints listed above to the `Build()` method.

### Updated: `src/NodalMerge.Studio.Host/StudioServiceCollectionExtensions.cs`

No change needed — services already registered.

### New: `extension/src/panels/WorkspaceDashboardPanel.ts`

- Opens a `WebviewPanel` in `vscode.ViewColumn.Two`
- Starts 2s polling loop on show, stops on dispose
- `postMessage({ type: 'summary', ... })` on each poll result
- Handles inbound messages: `{ type: 'spawn', agentType, workUnitId }`, `{ type: 'pause', agentId }`, etc.
- Forwards to Studio Host via `fetch()`

### New: `extension/src/webviews/dashboard.html`

Static HTML bundled into the extension. Sections:

```
[ Work Units ]  [ Agents ]  [ Pending Merges ]  [ Failures ]

Work Units
  wu-abc  "Build NodalMerge docs site"  Active  branch: work-abc  agent: worker-xyz
  [+ New Work Unit]

Agents
  worker-xyz  wu-abc  active  [Pause]  [Stop]
  [+ Spawn Agent]

Pending Merges
  mp-1  feat/docs → main  ReadyForReview  [Review →]

Failures
  wu-old  "..."  Failed
```

### New: `extension/src/extension.ts` additions

Register `nodalmerge.openDashboard` command → `WorkspaceDashboardPanel.createOrShow()`.

Add to `package.json` contributes:
- `"viewsContainers"` with an activity bar icon
- `"views"` with a tree view for quick status (separate from the WebView panel — the tree view is the sidebar, the WebView panel is the detail view)

## Out of scope

- Inline task list per work unit (Slice 7b renders WU-level only; tasks are visible via MCP or future 7g)
- Real-time push (2s polling is sufficient; WebSocket push for agent events is deferred)
- Merge review actions in the dashboard panel (clicking "Review →" opens Slice 7c panel)

## Success criteria

- [ ] Dashboard panel opens from activity bar or `nodalmerge.openDashboard` command
- [ ] Polls `/studio/workspace-summary` and renders current state
- [ ] "New Work Unit" dialog prompts for goal + owner, calls REST endpoint
- [ ] "Spawn Agent" prompts for agentType + workUnitId, calls REST endpoint
- [ ] "Pause" / "Stop" buttons call REST endpoints and refresh panel
- [ ] Panel disposes cleanly without dangling poll loops

## Next slice

**Slice 7c — Merge Review Panel:** Dedicated panel for the AP-4 human gate — shows proposal details, diff summary, and approve/reject/apply actions.
