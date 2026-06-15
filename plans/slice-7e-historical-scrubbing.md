# Slice 7e — Historical Scrubbing + Branch from Cursor

Status: **Complete**

## Problem

The DAG panel (Slice 7d) shows live events as they arrive but cannot scrub backwards in time. Clicking a past node does nothing. The "branch from cursor" action (create a new WorkUnit at a specific historical point) and "rollback to known good" are not wired.

## What already exists

`replayReducer` already handles all the state for historical playback:
- `set-cursor-index` — moves cursor to a specific node index on a branch
- `set-mode` — switches between `"live"` and `"playback"`
- `create-branch-from-cursor` — creates a new branch in local replay state from the cursor position

The NodalMerge room WebSocket supports:
- `request-server-pack` — asks the server to send a pack of historical nodes
- `mst-request` / `mst-done` — Merkle sync tree handshake for catching up from a frontier

These are in the DEAD_SIMPLE_API under section 3 (replay) and are handled by `RuntimeDagPersistenceService` already running in the Studio Host.

## Architecture

```
WebView (DAG replay panel — extended from 7d)
  ├─ scrubber UI (timeline slider or click-on-node)
  ├─ on scrub → dispatch set-cursor-index
  ├─ on cursor < head → request-server-pack to fill historical gaps
  ├─ [Branch from cursor] → postMessage to extension host
  └─ [Mark Known Good] → postMessage to extension host

Extension host (TS)
  ├─ on 'branchFromCursor' → POST /studio/branches + POST /studio/workunits
  └─ on 'markKnownGood'   → POST /studio/state/markKnownGood
```

## Files touched

### Updated: `extension/src/webviews/dag-replay/wsClient.ts`

Add `requestPack(frontier: string[])`:
```ts
ws.send(JSON.stringify({ type: 'request-server-pack', frontier }));
```
Handle incoming `pack` messages: extract nodes and dispatch `append-branch-node` for each.

### Updated: `extension/src/webviews/dag-replay/main.ts`

- On `set-cursor-index` with mode `playback`: if local node history is incomplete for the target branch, send `request-server-pack` with current frontier (known node IDs) to fill gaps
- Listen for `pack` events, apply nodes to reducer, then move cursor

### Updated: `extension/src/webviews/dag-replay/dagRenderer.ts`

Add scrubber UI:
- Click on any node → `dispatch({ type: 'set-cursor-index', branchId, nodeIndex })`
- Timeline slider at bottom of panel (horizontal, clamped to active branch node count)
- Cursor node highlighted differently from live head

Add action buttons (appear when cursor is in playback mode):
```
[▶ Jump to live]   [⎇ Branch from here]   [📌 Mark Known Good]
```

### Updated: `extension/src/panels/DagReplayPanel.ts`

Handle inbound messages from WebView:

```ts
case 'branchFromCursor': {
  // 1. Create branch in Studio
  const branchId = await fetch(POST /studio/branches, { name: msg.newBranchId, fromBranchId: msg.sourceBranchId });
  // 2. Create WorkUnit on that branch
  const wu = await fetch(POST /studio/workunits, { goal: msg.goal, owner: 'user', branchId });
  // 3. Tell WebView to register the branch locally
  panel.webview.postMessage({ type: 'branchCreated', branchId, workUnitId: wu.workUnitId, goal: wu.goal });
}

case 'markKnownGood': {
  await fetch(POST /studio/state/markKnownGood, { branchId: msg.branchId, nodeId: msg.nodeId, label: msg.label });
  vscode.window.showInformationMessage(`Known good state saved for ${msg.branchId}`);
}
```

### New REST endpoints on Studio Host

```
POST /studio/branches                — IBranchService.CreateBranchAsync()
POST /studio/state/markKnownGood     — IKnownGoodStateService.MarkKnownGoodAsync()
GET  /studio/state/knownGood/{branchId}  — IKnownGoodStateService.FindKnownGoodAsync()
POST /studio/state/checkoutKnownGood     — IKnownGoodStateService.CheckoutKnownGoodAsync()
```

## Playback mode interaction rules

| Action | Available in | Notes |
|--------|-------------|-------|
| Click node | always | enters playback, cursor moves to that node |
| Timeline slider | always | same as click node |
| ▶ Jump to live | playback only | `set-mode live`, cursor snaps to head |
| ⎇ Branch from here | playback only | opens goal prompt, creates WU |
| 📌 Mark Known Good | playback only | opens label prompt, saves checkpoint |
| New nodes arriving | live mode auto-appends; playback mode queues silently |

## Historical pack flow

```
WebView sends:   { type: "request-server-pack", frontier: ["node-abc", "node-def"] }
Host responds:   { type: "pack", nodes: [ { nodeId, branchId, lamport, ... }, ... ] }
WebView:         dispatches append-branch-node for each → cursor can now navigate
```

`RuntimeDagPersistenceService` already handles `request-server-pack` for the room. No new server work needed.

## Out of scope

- Full MST (Merkle sync) handshake (request-server-pack covers the Studio use case without needing mst-request/mst-done in v1)
- Animated playback (auto-advance cursor at 1fps — deferred)
- Cross-branch cursor comparison (Slice 7f / future)
- Replay of a rolled-back state (checkout known good is a server-side op, not a cursor move)

## Success criteria

- [ ] Clicking a node in the DAG panel sets the cursor to that position
- [ ] `request-server-pack` is sent when historical nodes are needed
- [ ] Historical nodes are applied to the reducer and cursor can reach them
- [ ] "Jump to live" restores live mode and snaps cursor to head
- [ ] "Branch from here" prompts for goal, creates Branch + WorkUnit via REST
- [ ] New branch appears in DAG panel within 1 live tick
- [ ] "Mark Known Good" prompts for label, calls REST endpoint, shows confirmation

## Next slice

**Slice 7f — Agent Config:** Settings UI for defining agent profiles (model hint, domain scope, display label), topology templates (orchestrator + worker roles), and workspace defaults.
