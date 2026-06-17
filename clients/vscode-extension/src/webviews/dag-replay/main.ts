import { createInitialReplayState, replayReducer } from './branchReplay';
import type { ReplayState, ReplayAction } from './branchReplay';
import { WsClient } from './wsClient';
import type { WsStatus } from './wsClient';
import { renderDag } from './dagRenderer';

// ── State ──────────────────────────────────────────────────────────────────

let replayState: ReplayState = createInitialReplayState('studio-main');
let ws: WsClient | null = null;
let goals: Record<string, string> = {};

// ── DOM refs ───────────────────────────────────────────────────────────────

const svg         = document.getElementById('dag-svg')     as unknown as SVGSVGElement;
const statusDot   = document.getElementById('status-dot')  as HTMLElement;
const statusText  = document.getElementById('status-text') as HTMLElement;
const nodeCount   = document.getElementById('node-count')  as HTMLElement;
const scrubber    = document.getElementById('scrubber')    as HTMLInputElement;
const scrubBranch = document.getElementById('scrub-branch') as HTMLElement;
const scrubPos    = document.getElementById('scrub-pos')   as HTMLElement;
const playbackBar = document.getElementById('playback-bar') as HTMLElement;
const btnLive     = document.getElementById('btn-live')    as HTMLButtonElement;
const btnBranch   = document.getElementById('btn-branch')  as HTMLButtonElement;
const btnKgs      = document.getElementById('btn-kgs')     as HTMLButtonElement;

// ── vscode API (only available inside WebView) ─────────────────────────────

// Slice 0 — the Studio Shell calls acquireVsCodeApi() exactly once (in its own bootstrap
// script, which runs before this bundle's <script> tag in document order) and exposes the
// result here, since calling acquireVsCodeApi() a second time in the same webview throws.
const vscode = (window as unknown as { __nmVscode: { postMessage(msg: unknown): void } }).__nmVscode;

// ── Dispatch + render ──────────────────────────────────────────────────────

function dispatch(action: ReplayAction): void {
  replayState = replayReducer(replayState, action);
  render();
}

function render(): void {
  renderDag(svg, replayState, goals);

  const total = Object.keys(replayState.nodesById).length;
  if (nodeCount) { nodeCount.textContent = String(total) + ' node' + (total === 1 ? '' : 's'); }

  const activeBranch = replayState.activeBranchId;
  const branchIds    = replayState.branchNodeIds[activeBranch] ?? [];
  const idx          = replayState.cursor.nodeIndex;
  const count        = branchIds.length;
  const isPlayback   = replayState.cursor.mode === 'playback';

  // Scrubber
  if (scrubber) {
    scrubber.max   = String(Math.max(0, count - 1));
    scrubber.value = String(idx);
  }
  if (scrubBranch) {
    scrubBranch.textContent = goals[activeBranch] ?? replayState.branches[activeBranch]?.label ?? activeBranch;
  }
  if (scrubPos) {
    scrubPos.textContent = count > 0 ? (idx + 1) + ' / ' + count : '—';
  }

  // Playback action bar
  if (playbackBar) {
    playbackBar.classList.toggle('hidden', !isPlayback);
  }
}

function setStatus(s: WsStatus | 'idle'): void {
  const labels: Record<string, string> = {
    idle: 'idle', connecting: 'connecting…', connected: 'live',
    disconnected: 'disconnected', error: 'error',
  };
  const colors: Record<string, string> = {
    idle: '#555', connecting: '#cca700', connected: '#4dac26',
    disconnected: '#888', error: '#f14c4c',
  };
  if (statusDot)  { statusDot.setAttribute('data-status', String(s)); }
  if (statusText) { statusText.textContent = labels[s] ?? s; }
}

// ── Node click delegation ──────────────────────────────────────────────────

svg.addEventListener('click', (e: MouseEvent) => {
  const target = e.target as SVGElement;
  const branchId  = target.getAttribute('data-branch-id');
  const nodeIndex = target.getAttribute('data-node-index');
  if (!branchId || nodeIndex === null) { return; }

  const idx = parseInt(nodeIndex, 10);

  // Request historical pack if we might have gaps (send current known frontier)
  const frontier = Object.keys(replayState.nodesById);
  ws?.requestPack(frontier);

  dispatch({ type: 'set-cursor-index', branchId, nodeIndex: idx });

  // Also set active branch if different
  if (branchId !== replayState.activeBranchId) {
    dispatch({ type: 'set-active-branch', branchId });
  }
});

// ── Scrubber input ─────────────────────────────────────────────────────────

if (scrubber) {
  scrubber.addEventListener('input', () => {
    const idx      = parseInt(scrubber.value, 10);
    const branchId = replayState.activeBranchId;
    ws?.requestPack(Object.keys(replayState.nodesById));
    dispatch({ type: 'set-cursor-index', branchId, nodeIndex: idx });
  });
}

// ── Playback bar buttons ───────────────────────────────────────────────────

if (btnLive) {
  btnLive.addEventListener('click', () => {
    dispatch({ type: 'set-mode', mode: 'live' });
  });
}

if (btnBranch) {
  btnBranch.addEventListener('click', () => {
    vscode.postMessage({
      type:           'branchFromCursor',
      sourceBranchId: replayState.cursor.branchId,
      sourceNodeId:   replayState.cursor.nodeId,
    });
  });
}

if (btnKgs) {
  btnKgs.addEventListener('click', () => {
    vscode.postMessage({
      type:     'markKnownGood',
      branchId: replayState.cursor.branchId,
      nodeId:   replayState.cursor.nodeId,
    });
  });
}

// ── Extension host messages ────────────────────────────────────────────────

interface WorkUnitRef {
  workUnitId: string;
  branchId:   string;
  goal:       string;
}

window.addEventListener('message', (event: MessageEvent) => {
  const msg = event.data as Record<string, unknown>;

  if (msg.type === 'init') {
    const port   = (msg.port   as number)  ?? 5080;
    const roomId = (msg.roomId as string)  ?? 'studio-main';
    const wus    = (msg.workUnits as WorkUnitRef[]) ?? [];

    goals = {};
    for (const wu of wus) { goals[wu.branchId] = wu.goal; }

    replayState = createInitialReplayState(roomId, roomId, goals[roomId] ?? roomId);

    ws?.close();
    ws = new WsClient(port, roomId,
      (action) => { replayState = replayReducer(replayState, action); render(); },
      (status) => { setStatus(status); },
    );
    ws.connect();
    render();
    return;
  }

  if (msg.type === 'workUnits') {
    goals = {};
    for (const wu of (msg.workUnits as WorkUnitRef[])) { goals[wu.branchId] = wu.goal; }
    render();
    return;
  }

  if (msg.type === 'branchCreated') {
    const newBranchId = msg.newBranchId as string;
    const goal        = msg.goal        as string;
    goals[newBranchId] = goal;
    // Register branch in replay state and move cursor to it
    dispatch({
      type:             'create-branch-from-cursor',
      newBranchId,
      newRoomId:        newBranchId,
      newLabel:         goal,
      reason:           'manual',
    });
  }
});

// ── Initial render ─────────────────────────────────────────────────────────

setStatus('idle');
render();
