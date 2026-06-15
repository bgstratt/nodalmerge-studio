# Slice 7d — DAG Replay Panel (Live)

Status: **Complete**

## Problem

The NodalMerge CRDT engine records every operation as a node in a content-addressed DAG. Branches, forks, and merges are all visible in this DAG, but there is no visualization. The docs demo (`infinite-room-workspace`) has a fully working branch-DAG visualization built on `branchReplay.ts` + `replayLayout.ts`. This slice ports that into the Studio extension as a WebView panel showing live events.

## What already exists

In `docs/apps/demos/infinite-room-workspace/src/lib/`:

| File | Purpose |
|------|---------|
| `branchReplay.ts` | Pure reducer (`replayReducer`) + action types. No framework deps. |
| `replayLayout.ts` | `computeBranchOffsets()` + `computeMaxBranchColumn()`. Pure functions. |

Both files are **framework-agnostic** and can be used verbatim inside a VS Code WebView's bundled script.

The Studio Host exposes `/ws/runtime` — the same NodalMerge WebSocket room endpoint the demo connects to. No new server work is needed for live events.

## Architecture

```
WebView (DAG replay panel)
  ├─ Connects to ws://localhost:PORT/ws/runtime
  ├─ Sends hello { type:"hello", room:"studio-main", pubkey:"studio-ui", frontier:[] }
  ├─ Receives runtime events → dispatches to replayReducer
  ├─ Renders DAG as SVG using computeBranchOffsets + computeMaxBranchColumn
  └─ Branch labels enriched from extension host (WorkUnit goal → branch label)

Extension host (TS)
  ├─ Opens DagReplayPanel WebView
  ├─ Fetches WorkUnit list from /studio/workunits
  └─ postMessage({ type:'workUnits', data }) → used to label branches with goals
```

The WebView's WebSocket connects directly to the Studio Host — not via the extension host. This is the same pattern the demo uses (browser → `/ws/runtime`). WebViews support WebSocket natively.

## Files touched

### New: `extension/src/panels/DagReplayPanel.ts`

- Opens WebView panel in `vscode.ViewColumn.Two`
- Fetches `GET /studio/workunits` to get goal labels
- `postMessage({ type:'init', port, workUnits })` to WebView on open
- Handles inbound message `{ type:'branchFromCursor', newBranchId, goal }` → calls `POST /studio/workunits` to create a WorkUnit on the new branch

### New: `extension/src/webviews/dag-replay/`

```
dag-replay/
  index.html
  main.ts           — entry point, handles postMessage from extension host
  wsClient.ts       — thin wrapper around native WebSocket → /ws/runtime
  dagRenderer.ts    — SVG rendering using branchOffsets
  branchReplay.ts   — copied verbatim from docs demo
  replayLayout.ts   — copied verbatim from docs demo
```

(These are bundled by esbuild into the WebView script.)

### `wsClient.ts`

```ts
class WsClient {
  constructor(port: number, roomId: string, onAction: (a: ReplayAction) => void) {
    const ws = new WebSocket(`ws://localhost:${port}/ws/runtime`);
    ws.onopen = () => ws.send(JSON.stringify({
      type: 'hello', room: roomId, pubkey: 'studio-ui', frontier: []
    }));
    ws.onmessage = (e) => {
      const payload = JSON.parse(e.data);
      onAction({ type: 'append-runtime-event', roomId, payload, receivedAtIso: new Date().toISOString() });
    };
  }
}
```

### `dagRenderer.ts`

SVG layout:
- Each branch is a horizontal lane
- Nodes are circles arranged left-to-right per branch (x = node index, y = branch lane)
- Branch offsets from `computeBranchOffsets` determine lane y-positions
- Merge nodes have two incoming edges (two-parent DAG lines)
- Branch label on left: WorkUnit goal if known, branchId otherwise
- Current cursor highlighted in blue; head node marked

### Visual design

```
main       ●─────●─────●─────M─────●
                              ↑
feat/docs       ●─────●─────●
                       ↑
feat/demo-fix        ●─────●
```

- `●` = node (CRDT operation)
- `M` = merge node (two incoming edges)
- Branch lanes separated by 40px vertical gap
- Scrubbing handled in Slice 7e

## Room ID convention

In the Studio, each WorkUnit's `BranchId` maps to a NodalMerge room. The DAG panel connects to the "root" room (`studio-main` or a configured workspace room). Branch rooms follow from there.

The exact room ↔ branch mapping is driven by `InMemoryBranchService` on the server side. The panel shows whatever branches appear in the room's event stream.

## Extension host port injection

The WebView cannot access extension state directly. The extension host calls `panel.webview.postMessage({ type: 'init', port: 5080, roomId: 'studio-main' })` immediately after the panel opens. The WebView waits for this message before connecting.

## Out of scope

- Cursor scrubbing / historical replay (Slice 7e)
- Clicking on a node to view its payload
- Multi-room support (one root room per workspace in v1)
- Filtering branches by agent or work unit

## Success criteria

- [ ] Panel opens from activity bar or `nodalmerge.openDagReplay` command
- [ ] WebView connects to `/ws/runtime` and receives live events
- [ ] `replayReducer` dispatches `append-runtime-event` correctly
- [ ] SVG renders branch lanes with nodes in chronological order
- [ ] Merge edges show two-parent lines
- [ ] Branch labels show WorkUnit goal when available
- [ ] Panel updates in real time as new CRDT operations arrive
- [ ] Panel disposes cleanly and closes WebSocket

## Next slice

**Slice 7e — Historical Scrubbing:** Add a cursor scrubber to the DAG panel. Clicking a past node enters playback mode; "branch from cursor" creates a new WorkUnit from that point in history.
