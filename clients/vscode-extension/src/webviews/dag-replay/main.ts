import { createInitialReplayState, replayReducer } from './branchReplay';
import type { ReplayState, ReplayAction } from './branchReplay';
import { WsClient } from './wsClient';
import type { WsStatus } from './wsClient';
import { renderDag } from './dagRenderer';

// ── State ──────────────────────────────────────────────────────────────────

let replayState: ReplayState = createInitialReplayState('studio-main');
let ws: WsClient | null = null;
let goals: Record<string, string> = {};

// Slice 13h — live stage badges, parity with ArtifactExplorerPanel's tree (12c). Nodes here are
// organized per branchId, not per workUnitId (see ReplayNode/BranchStream in branchReplay.ts), so
// stage is tracked per branchId too — workUnitIdToBranchId converts incoming
// work-unit-stage-changed frames (which only know workUnitId) into that key. Seeded from the
// WorkUnit.currentStage already present in the one-time 'init' payload (so a panel opened mid-run
// shows the current stage immediately, not just stages that change after it opens), then kept
// live by WsClient's onStageChange callback below.
let stages: Record<string, string | null> = {};
let workUnitIdToBranchId: Record<string, string> = {};

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
const btnRestoreKgs = document.getElementById('btn-restore-kgs') as HTMLButtonElement;
const nodeDetail      = document.getElementById('node-detail')       as HTMLElement;
const nodeDetailTitle = document.getElementById('node-detail-title') as HTMLElement;
const nodeDetailBody  = document.getElementById('node-detail-body')  as HTMLElement;
const nodeDetailClose = document.getElementById('node-detail-close') as HTMLButtonElement;

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
  renderDag(svg, replayState, goals, stages);

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
  if (statusDot)  { statusDot.setAttribute('data-status', String(s)); }
  if (statusText) { statusText.textContent = labels[s] ?? s; }
}

// ── Node click delegation ──────────────────────────────────────────────────

svg.addEventListener('click', (e: MouseEvent) => {
  const target = e.target as SVGElement;
  const branchId  = target.getAttribute('data-branch-id');
  const nodeIndex = target.getAttribute('data-node-index');
  const nodeId    = target.getAttribute('data-node-id');
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

  // Slice — node detail drawer: ReplayNode.nodeId is seeded from the backend's
  // artifact/orchestration-event id (see registerTimeline's replayOpId), so most clicks can
  // resolve real content via /studio/replay/inspect. Locally-synthesized nodes (e.g. merge
  // markers) won't resolve — the inspect response's `error` field is handled in renderNodeDetail.
  if (nodeId) {
    const node = replayState.nodesById[nodeId];
    if (nodeDetail && nodeDetailTitle && nodeDetailBody) {
      nodeDetail.classList.remove('hidden');
      nodeDetailTitle.textContent = node?.opSummary ?? nodeId;
      nodeDetailBody.innerHTML = '<div class="node-detail-row" style="opacity:0.5;font-style:italic">Loading…</div>';
    }
    vscode.postMessage({ type: 'inspectNode', branchId, nodeId });
  }
});

if (nodeDetailClose) {
  nodeDetailClose.addEventListener('click', () => {
    if (nodeDetail) { nodeDetail.classList.add('hidden'); }
  });
}

// ── Scrubber input ─────────────────────────────────────────────────────────

if (scrubber) {
  scrubber.addEventListener('input', () => {
    const idx      = parseInt(scrubber.value, 10);
    const branchId = replayState.activeBranchId;
    ws?.requestPack(Object.keys(replayState.nodesById));
    dispatch({ type: 'set-cursor-index', branchId, nodeIndex: idx });
  });
}

// ── Session override picker ────────────────────────────────────────────────

const dagSessionOverride = document.getElementById('dag-session-override') as HTMLSelectElement | null;
if (dagSessionOverride) {
  dagSessionOverride.addEventListener('change', () => {
    vscode.postMessage({
      type: 'sessionOverrideChanged',
      panelId: 'shell-pane-trajectory-replay',
      sessionId: dagSessionOverride.value || undefined,
    });
  });
}

// ── Replay mode selector (Slice 18d) ───────────────────────────────────────

const replayModeSelect = document.getElementById('replay-mode') as HTMLSelectElement | null;

if (replayModeSelect) {
  replayModeSelect.addEventListener('change', () => {
    const newMode = replayModeSelect.value;
    vscode.postMessage({ type: 'replayModeChanged', mode: newMode });

    // Show/hide main DAG vs alternate view
    const svg = document.getElementById('dag-svg');
    const altView = document.getElementById('alternate-view');
    const scrubberRow = document.getElementById('scrubber-row');
    const playbackBarEl = document.getElementById('playback-bar');
    if (newMode === 'linear') {
      if (svg) svg.style.display = 'block';
      if (altView) altView.style.display = 'none';
      if (scrubberRow) scrubberRow.style.display = 'flex';
      if (playbackBarEl) playbackBarEl.style.display = 'none';
    } else {
      if (svg) svg.style.display = 'none';
      if (altView) altView.style.display = 'block';
      if (scrubberRow) scrubberRow.style.display = 'none';
      if (playbackBarEl) playbackBarEl.style.display = 'none';
      if (altView) altView.innerHTML = '<p style="opacity:0.45;font-style:italic">Loading…</p>';
    }
  });
}

// ── Alternate view renderers ──────────────────────────────────────────────

function escHtml(s: string): string {
  return String(s || '').replace(/&/g,'&').replace(/</g,'<').replace(/>/g,'>');
}

function badgeHtml(status: string): string {
  return '<span style="display:inline-block;border-radius:9px;padding:1px 8px;font-size:0.78em;background:var(--vscode-badge-background);color:var(--vscode-badge-foreground)">' + escHtml(status) + '</span>';
}

function fmtDate(iso?: string | null): string {
  if (!iso) { return '—'; }
  try { return new Date(iso).toLocaleString(); } catch { return iso; }
}

function renderNodeDetail(payload: any): string {
  if (!payload || payload.error) {
    return '<div class="node-detail-row" style="opacity:0.6;font-style:italic">'
      + escHtml(payload?.error ?? 'No additional detail available for this node.') + '</div>';
  }
  if (payload.kind === 'artifact') {
    const a = payload.artifact ?? {};
    let html = '<div class="node-detail-row"><span class="node-detail-label">Type</span>' + escHtml(a.type) + '</div>';
    html += '<div class="node-detail-row"><span class="node-detail-label">Status</span>' + escHtml(a.status) + '</div>';
    if (a.title) { html += '<div class="node-detail-row"><span class="node-detail-label">Title</span>' + escHtml(a.title) + '</div>'; }
    html += '<div class="node-detail-row"><span class="node-detail-label">Owner</span>' + escHtml(a.ownedByAgentId || a.ownedByWorkUnitId || '—') + '</div>';
    html += '<div class="node-detail-row"><span class="node-detail-label">Created</span>' + fmtDate(a.createdAt) + '</div>';
    if (a.body) { html += '<div class="node-detail-body-text">' + escHtml(a.body) + '</div>'; }
    return html;
  }
  if (payload.kind === 'orchestration-event') {
    const ev = payload.orchestrationEvent ?? {};
    let html = '<div class="node-detail-row"><span class="node-detail-label">Action</span>' + escHtml(ev.action) + '</div>';
    html += '<div class="node-detail-row"><span class="node-detail-label">Stage</span>' + escHtml(ev.inputStage) + '</div>';
    if (ev.reason) { html += '<div class="node-detail-row"><span class="node-detail-label">Reason</span>' + escHtml(ev.reason) + '</div>'; }
    if (ev.spawnedIds && ev.spawnedIds.length) {
      html += '<div class="node-detail-row"><span class="node-detail-label">Spawned</span>' + escHtml(ev.spawnedIds.join(', ')) + '</div>';
    }
    html += '<div class="node-detail-row"><span class="node-detail-label">Occurred</span>' + fmtDate(ev.occurredAt) + '</div>';
    return html;
  }
  if (payload.kind === 'known-good-state') {
    const k = payload.knownGoodState ?? {};
    let html = '<div class="node-detail-row"><span class="node-detail-label">Checkpoint</span>' + escHtml(k.description) + '</div>';
    html += '<div class="node-detail-row"><span class="node-detail-label">Created by</span>' + escHtml(k.createdBy) + '</div>';
    html += '<div class="node-detail-row"><span class="node-detail-label">Created</span>' + fmtDate(k.createdAt) + '</div>';
    return html;
  }
  return '<div class="node-detail-row" style="opacity:0.6;font-style:italic">No additional detail available for this node.</div>';
}

function renderBranchExplorer(data: any): string {
  const branches: any[] = data?.branches ?? [];
  if (!branches.length) { return '<p style="opacity:0.45;font-style:italic">No branches found.</p>'; }
  let html = '<h2 style="font-size:0.82em;text-transform:uppercase;letter-spacing:0.07em;opacity:0.5;margin:0 0 8px">Branch Explorer</h2>';
  branches.forEach((br: any) => {
    html += '<details style="margin-bottom:8px;border:1px solid var(--nm-border);border-radius:4px;padding:8px" open>';
    html += '<summary style="cursor:pointer;font-weight:600;font-size:0.9em"><span class="mono" style="font-family:var(--vscode-editor-font-family,monospace);opacity:0.7">' + escHtml(br.branchId) + '</span> <span style="opacity:0.5">(' + br.workUnitCount + ' work units)</span></summary>';
    html += '<div style="margin-top:6px">';
    (br.goals ?? []).forEach((g: any) => {
      html += '<div style="padding:4px 8px;margin:2px 0;border-left:2px solid var(--vscode-textLink-foreground,#3794ff);font-size:0.85em">';
      html += '<span style="font-weight:600">' + escHtml(g.goal ?? '') + '</span> ';
      html += badgeHtml(g.status);
      if (g.phase) { html += ' <span style="font-size:0.85em;opacity:0.6">' + escHtml(g.phase) + '</span>'; }
      html += '</div>';
    });
    html += '</div></details>';
  });
  return html;
}

function renderCounterfactual(data: any): string {
  const alternatives: any[] = data?.alternatives ?? [];
  const targetWorkUnitId = data?.targetWorkUnitId ?? '';
  const targetGoal = data?.targetGoal ?? '';
  let html = '<h2 style="font-size:0.82em;text-transform:uppercase;letter-spacing:0.07em;opacity:0.5;margin:0 0 8px">Counterfactual</h2>';
  if (targetWorkUnitId) {
    html += '<p style="font-size:0.85em;opacity:0.6">Target: <span class="mono" style="font-family:var(--vscode-editor-font-family,monospace)">' + escHtml(targetWorkUnitId) + '</span>' + (targetGoal ? ' — ' + escHtml(targetGoal) : '') + '</p>';
  }
  if (!alternatives.length) {
    html += '<p style="opacity:0.45;font-style:italic">No alternative timelines found for this work unit.</p>';
  } else {
    html += '<p style="font-size:0.85em;opacity:0.6">' + alternatives.length + ' alternative timeline(s)</p>';
    alternatives.forEach((alt: any) => {
      html += '<div style="padding:8px;margin:4px 0;border:1px solid var(--nm-border);border-radius:4px">';
      html += '<div style="font-weight:600;font-size:0.9em">' + escHtml(alt.goal ?? '') + '</div>';
      html += '<div style="font-size:0.8em;margin-top:4px;display:flex;gap:6px;flex-wrap:wrap">';
      html += badgeHtml(alt.status);
      if (alt.forkType) { html += '<span style="font-size:0.78em;opacity:0.7;border:1px solid var(--nm-border);border-radius:9px;padding:1px 8px">' + escHtml(alt.forkType) + '</span>'; }
      if (alt.relationship) { html += '<span style="font-size:0.78em;opacity:0.5">' + escHtml(alt.relationship) + '</span>'; }
      html += '</div></div>';
    });
  }
  return html;
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

if (btnRestoreKgs) {
  btnRestoreKgs.addEventListener('click', () => {
    vscode.postMessage({
      type:     'restoreKnownGood',
      branchId: replayState.cursor.branchId,
    });
  });
}

// ── Extension host messages ────────────────────────────────────────────────

interface WorkUnitRef {
  workUnitId:   string;
  branchId:     string;
  goal:         string;
  currentStage?: string | null;
}

interface TimelineEntry {
  kind: string;
  nodeId: string;
  description: string;
  occurredAt: string;
}

interface TimelineData {
  branchId: string;
  entries: TimelineEntry[];
}

interface TimelineResponse {
  branches: string[];
  timelines: TimelineData[];
}

function applyStageChange(workUnitId: string, stage: string | null): void {
  const branchId = workUnitIdToBranchId[workUnitId];
  if (!branchId) { return; }
  stages[branchId] = stage;
  render();
}

// Slice 13h — registers branches and nodes from the /studio/replay/timeline REST
// response. Called from both init and workUnits message handlers so new artifacts and
// orchestration events appear without waiting for a full re-init.
function registerTimeline(timeline: TimelineResponse): void {
  for (const branchId of timeline.branches) {
    if (!replayState.branches[branchId]) {
      const label = goals[branchId] ?? branchId;
      dispatch({
        type: 'register-branch',
        branchId,
        roomId: branchId,
        label,
        basedOnBranchId: null,
        basedOnNodeId: null,
      });
    }
  }

  for (const td of timeline.timelines) {
    const branchId = td.branchId;
    for (const entry of td.entries) {
      dispatch({
        type: 'append-branch-node',
        branchId,
        roomId: branchId,
        opSummary: `${entry.kind}: ${entry.description}`,
        payloadJson: JSON.stringify(entry),
        replayOpId: entry.nodeId,
        atIso: entry.occurredAt,
      });
    }
  }
}

window.addEventListener('message', (event: MessageEvent) => {
  const msg = event.data as Record<string, unknown>;

  if (msg.type === 'init') {
    const wsUrl  = (msg.wsUrl  as string)  ?? 'ws://127.0.0.1:5080/ws/runtime';
    const roomId = (msg.roomId as string)  ?? 'studio-main';
    const wus    = (msg.workUnits as WorkUnitRef[]) ?? [];

    goals = {};
    stages = {};
    workUnitIdToBranchId = {};
    for (const wu of wus) {
      goals[wu.branchId] = wu.goal;
      stages[wu.branchId] = wu.currentStage ?? null;
      workUnitIdToBranchId[wu.workUnitId] = wu.branchId;
    }

    replayState = createInitialReplayState(roomId, roomId, goals[roomId] ?? roomId);

    // Slice 13h — register timeline branches/nodes from REST before connecting
    // the WebSocket (which now only handles live stage-change events).
    if (msg.timeline) {
      // Dispatch each timeline entry through the reducer so branches get auto-registered
      const tl = msg.timeline as TimelineResponse;
      for (const branchId of tl.branches) {
        if (!replayState.branches[branchId]) {
          const label = goals[branchId] ?? branchId;
          replayState = replayReducer(replayState, {
            type: 'register-branch',
            branchId,
            roomId: branchId,
            label,
            basedOnBranchId: null,
            basedOnNodeId: null,
          });
        }
      }
      for (const td of tl.timelines) {
        for (const entry of td.entries) {
          replayState = replayReducer(replayState, {
            type: 'append-branch-node',
            branchId: td.branchId,
            roomId: td.branchId,
            opSummary: `${entry.kind}: ${entry.description}`,
            payloadJson: JSON.stringify(entry),
            replayOpId: entry.nodeId,
            atIso: entry.occurredAt,
          });
        }
      }
    }

    ws?.close();
    ws = new WsClient(wsUrl, roomId,
      (action) => { replayState = replayReducer(replayState, action); render(); },
      (status) => { setStatus(status); },
      applyStageChange,
    );
    ws.connect();
    render();
    return;
  }

  if (msg.type === 'workUnits') {
    goals = {};
    for (const wu of (msg.workUnits as WorkUnitRef[])) {
      goals[wu.branchId] = wu.goal;
      workUnitIdToBranchId[wu.workUnitId] = wu.branchId;
      if (wu.currentStage !== undefined) { stages[wu.branchId] = wu.currentStage; }
    }
    // Slice 13h — merge updated timeline into existing replay state
    if (msg.timeline) {
      const tl = msg.timeline as TimelineResponse;
      registerTimeline(tl);
    }
    render();
    return;
  }

  if (msg.type === 'updateSessionPicker' && msg.panelId === 'shell-pane-trajectory-replay') {
    const sel = document.getElementById('dag-session-override') as HTMLSelectElement | null;
    if (sel) {
      const shellLabel = msg.shellSessionId ? ' (' + String(msg.shellSessionId).slice(0, 8) + '…)' : '';
      sel.innerHTML = '<option value="">Follow Workspace' + shellLabel + '</option>';
      for (const s of (msg.sessions as Array<{ sessionId: string; status: string }> ?? [])) {
        const opt = document.createElement('option');
        opt.value = s.sessionId;
        opt.textContent = String(s.sessionId).slice(0, 12) + '… (' + s.status + ')';
        sel.appendChild(opt);
      }
      sel.value = (msg.overrideSessionId as string) || '';
    }
    return;
  }

  // Slice 18d — render alternate view data from trajectory/replay endpoint
  if (msg.type === 'replayModeData') {
    const mode = msg.mode as string;
    const data = msg.data as any;
    const altView = document.getElementById('alternate-view');
    if (altView) {
      if (mode === 'branchexplorer') {
        altView.innerHTML = renderBranchExplorer(data);
      } else if (mode === 'counterfactual') {
        altView.innerHTML = renderCounterfactual(data);
      } else {
        // Linear — show the DAG
        const svg = document.getElementById('dag-svg');
        if (svg) svg.style.display = 'block';
        altView.style.display = 'none';
      }
    }
    return;
  }

  if (msg.type === 'nodeDetail') {
    if (nodeDetailBody) {
      nodeDetailBody.innerHTML = renderNodeDetail(msg.error ? { error: msg.error } : msg.detail);
    }
    return;
  }

  if (msg.type === 'branchCreated') {
    const newBranchId = msg.newBranchId as string;
    const goal        = msg.goal        as string;
    const workUnitId  = msg.workUnitId  as string;
    goals[newBranchId] = goal;
    workUnitIdToBranchId[workUnitId] = newBranchId;
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
