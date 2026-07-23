import { createInitialReplayState, replayReducer } from './branchReplay';
import type { ReplayState, ReplayAction } from './branchReplay';
import { WsClient } from './wsClient';
import type { WsStatus } from './wsClient';
import { renderDag } from './dagRenderer';
import { renderPlanTree, renderPlanNodeDetail, fitView, MIN_ZOOM, MAX_ZOOM } from './planTree';
import type { PlanTreeData, PlanContractDef, PlanView, PlanBounds } from './planTree';

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

// Session highlight (plans/pathways-workspace-history.md) — Pathways data is deliberately
// workspace-wide, so a selected session dims out-of-session lanes rather than filtering them.
// True when the host's workUnits fetch was session-scoped (see DagReplayPanel's sessionActive
// flag); `goals` then holds exactly the session's branches, which is the highlight set.
let sessionScopeActive = false;

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
  const dimmed = sessionScopeActive
    ? new Set(Object.keys(replayState.branches).filter(id => !(id in goals)))
    : null;
  renderDag(svg, replayState, goals, stages, dimmed, pathwaysEdges);

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

  // Node detail drawer. Pathways nodes (see pathwaysInspectId above) either round-trip to
  // /studio/replay/inspect for kinds it can resolve, or render straight from the node's own
  // payloadJson for kinds it can't (GoalStarted/DeadBranch/ExternalUpdate). Locally-synthesized
  // nodes (e.g. merge markers) fall back to the same "no detail" message renderNodeDetail shows
  // for an inspect `error` response.
  if (nodeId) {
    const replayNode = replayState.nodesById[nodeId];
    if (nodeDetail && nodeDetailTitle && nodeDetailBody) {
      nodeDetail.classList.remove('hidden');
      nodeDetailTitle.textContent = replayNode?.opSummary ?? nodeId;

      let pathwaysNode: PathwaysNode | null = null;
      try { pathwaysNode = replayNode ? (JSON.parse(replayNode.payloadJson) as PathwaysNode) : null; } catch { /* not a pathways node */ }
      // Slice 3 — remembered so the nodeDetail handler (which fires later, once the async
      // inspect round-trip resolves) knows whether to offer "Branch from here (new steering)".
      currentInspectedNode = pathwaysNode;

      if (pathwaysNode && pathwaysNode.kind) {
        const inspectId = pathwaysInspectId(pathwaysNode);
        if (inspectId) {
          nodeDetailBody.innerHTML = '<div class="node-detail-row" style="opacity:0.5;font-style:italic">Loading…</div>';
          // workUnitId tells the host to also fetch file diffs + the agent conversation that
          // produced this proposal (slice 2); requestNodeId round-trips through the host so the
          // nodeDetail handler can drop responses that arrive after the user clicked a different
          // node (rapid clicks would otherwise render node A's detail with node B's actions).
          vscode.postMessage({
            type: 'inspectNode', branchId, nodeId: inspectId,
            workUnitId: pathwaysNode.workUnitId, requestNodeId: pathwaysNode.nodeId,
          });
        } else {
          nodeDetailBody.innerHTML = renderPathwaysNodeDetail(pathwaysNode) + renderMaterializeAction(pathwaysNode);
        }
      } else {
        nodeDetailBody.innerHTML = '<div class="node-detail-row" style="opacity:0.5;font-style:italic">Loading…</div>';
        vscode.postMessage({ type: 'inspectNode', branchId, nodeId, requestNodeId: nodeId });
      }
    }
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

// ── Sync now (Slice 4, pathways-workspace-history.md) ───────────────────────

const btnSyncNow = document.getElementById('btn-sync-now') as HTMLButtonElement | null;
if (btnSyncNow) {
  btnSyncNow.addEventListener('click', () => {
    vscode.postMessage({ type: 'syncNow' });
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

// ── Plan sub-tab (read-only plan decomposition) ────────────────────────────

let planPaneActive = false;
let planData: PlanTreeData | null = null;
let planContractsById = new Map<string, PlanContractDef>();

// Persisted pan/zoom (a transform on the #pw-viewport group). Survives the 2s poll re-render because
// renderPlan feeds it back into renderPlanTree; auto-fit runs once per session (planFittedSession).
let planView: PlanView = { k: 1, tx: 0, ty: 0 };
let planBounds: PlanBounds = { width: 0, height: 0 };
let planFittedSession: string | null = null;
// Set true when a pointer drag crosses the pan threshold, so the ensuing click doesn't also select
// a node. Shared between the pan handler and the node-click handler; reset at each pointerdown.
let planPanMoved = false;

const planSvg = document.getElementById('plan-svg') as unknown as SVGSVGElement | null;
const planEmpty = document.getElementById('plan-empty') as HTMLElement | null;
const planDetail = document.getElementById('plan-detail') as HTMLElement | null;
const planDetailTitle = document.getElementById('plan-detail-title') as HTMLElement | null;
const planDetailBody = document.getElementById('plan-detail-body') as HTMLElement | null;

function requestPlanTree(): void {
  vscode.postMessage({ type: 'pathways.loadPlanTree' });
}

// Apply the current pan/zoom by updating only the viewport group's transform — no re-render, so
// dragging/zooming stays smooth regardless of tree size.
function applyPlanTransform(): void {
  const vp = document.getElementById('pw-viewport');
  if (vp) { vp.setAttribute('transform', `translate(${planView.tx} ${planView.ty}) scale(${planView.k})`); }
}

function planViewportSize(): { w: number; h: number } {
  const r = planSvg?.getBoundingClientRect();
  return { w: r?.width ?? 0, h: r?.height ?? 0 };
}

function fitPlan(): void {
  const { w, h } = planViewportSize();
  planView = fitView(planBounds, w, h);
  applyPlanTransform();
}

// Sub-tab toggle: plain display swap of the two panes (the .active class is reserved for the outer
// shell tabs). Activating Plan requests its data immediately; the 2s poll keeps it fresh (see the
// workUnits handler) so status colors track the roll-up live.
for (const tab of Array.from(document.querySelectorAll('.pw-tab'))) {
  tab.addEventListener('click', () => {
    const pane = (tab as HTMLElement).dataset.pane;
    planPaneActive = pane === 'plan';
    for (const t of Array.from(document.querySelectorAll('.pw-tab'))) {
      t.classList.toggle('pw-tab-active', t === tab);
    }
    const traj = document.getElementById('pw-pane-trajectory');
    const plan = document.getElementById('pw-pane-plan');
    if (traj) { traj.style.display = planPaneActive ? 'none' : 'flex'; }
    if (plan) { plan.style.display = planPaneActive ? 'flex' : 'none'; }
    if (planPaneActive) { requestPlanTree(); }
  });
}

function renderPlan(sessionId?: string): void {
  if (!planSvg) { return; }
  const hasNodes = !!planData && planData.nodes.length > 0;
  if (planEmpty) {
    planEmpty.classList.toggle('hidden', hasNodes);
    if (!hasNodes) { planEmpty.textContent = planData ? 'No plan recorded for this session yet.' : 'Select a session to view its plan.'; }
  }
  planSvg.style.display = hasNodes ? 'block' : 'none';
  if (hasNodes && planData) {
    planBounds = renderPlanTree(planSvg, planData, planView);
    // Auto-fit once when a session's plan first appears; afterwards the user's pan/zoom is preserved
    // across the poll re-renders (planView is passed straight back into renderPlanTree above).
    if (sessionId && sessionId !== planFittedSession) {
      planFittedSession = sessionId;
      fitPlan();
    }
  }
}

// Scroll to zoom around the cursor; the world point under the cursor stays put.
if (planSvg) {
  planSvg.addEventListener('wheel', (ev) => {
    if (!planData) { return; }
    ev.preventDefault();
    const r = planSvg.getBoundingClientRect();
    const sx = ev.clientX - r.left, sy = ev.clientY - r.top;
    const factor = Math.exp(-ev.deltaY * 0.0015);
    const k2 = Math.max(MIN_ZOOM, Math.min(MAX_ZOOM, planView.k * factor));
    planView = {
      k: k2,
      tx: sx - ((sx - planView.tx) / planView.k) * k2,
      ty: sy - ((sy - planView.ty) / planView.k) * k2,
    };
    applyPlanTransform();
  }, { passive: false });

  // Drag anywhere to pan. No setPointerCapture — capturing the pointer retargets the ensuing click
  // to the SVG and breaks node selection. Instead we listen for move/up on `window` for the duration
  // of the drag, so panning still works if the pointer leaves the SVG and always ends on pointerup,
  // while a plain (non-dragged) click reaches the node normally. Past a 3px threshold sets
  // planPanMoved so the click that follows a real drag is treated as pan-end, not a node select.
  let panning = false, startX = 0, startY = 0, startTx = 0, startTy = 0;
  const onPanMove = (ev: PointerEvent): void => {
    if (!panning) { return; }
    const dx = ev.clientX - startX, dy = ev.clientY - startY;
    if (!planPanMoved && Math.hypot(dx, dy) > 3) { planPanMoved = true; planSvg.classList.add('pw-panning'); }
    if (planPanMoved) { planView = { ...planView, tx: startTx + dx, ty: startTy + dy }; applyPlanTransform(); }
  };
  const onPanUp = (): void => {
    if (!panning) { return; }
    panning = false; planSvg.classList.remove('pw-panning');
    window.removeEventListener('pointermove', onPanMove);
    window.removeEventListener('pointerup', onPanUp);
  };
  planSvg.addEventListener('pointerdown', (ev) => {
    if (ev.button !== 0) { return; }
    panning = true; planPanMoved = false;
    startX = ev.clientX; startY = ev.clientY; startTx = planView.tx; startTy = planView.ty;
    window.addEventListener('pointermove', onPanMove);
    window.addEventListener('pointerup', onPanUp);
  });
}

document.getElementById('plan-fit-btn')?.addEventListener('click', fitPlan);

// Click a node → detail drawer, rendered from already-loaded data (no round-trip). Delegated so it
// survives re-renders; reads data-wu off the node group (no inline handlers — CSP blocks them).
if (planSvg) {
  planSvg.addEventListener('click', (ev) => {
    if (planPanMoved) { planPanMoved = false; return; }  // this click ended a pan, not a select
    // Hit-test via elementFromPoint, not ev.target: setPointerCapture (used for drag-pan) retargets
    // the click to the SVG itself, so ev.target is never the node group. elementFromPoint ignores
    // capture and returns the actual element under the cursor.
    let node = document.elementFromPoint(ev.clientX, ev.clientY) as Element | null;
    while (node && node !== planSvg && !(node as HTMLElement).dataset?.wu) { node = node.parentElement; }
    const wu = node && (node as HTMLElement).dataset ? (node as HTMLElement).dataset.wu : undefined;
    if (!wu || !planData || !planDetail || !planDetailBody || !planDetailTitle) { return; }
    const n = planData.nodes.find((x) => x.workUnitId === wu);
    if (!n) { return; }
    planDetailTitle.textContent = n.goal.length > 80 ? n.goal.slice(0, 79) + '…' : n.goal;
    planDetailBody.innerHTML = renderPlanNodeDetail(n, planContractsById, escHtml);
    planDetail.classList.remove('hidden');
  });
}
document.getElementById('plan-detail-close')?.addEventListener('click', () => {
  planDetail?.classList.add('hidden');
});

// ── Alternate view renderers ──────────────────────────────────────────────

// Full-fidelity copy of src/webviews/views/lib/esc.js (the canonical escaping helper) — a literal
// import is blocked by tsconfig (allowJs off, include: *.ts only). The previous local version was
// a broken no-op (each character "replaced" with itself), so everything routed through it — LLM
// conversation text, diff content, file paths — reached innerHTML unescaped. Escapes the full set
// (&, <, >, ", ') so the result is safe in both text and attribute contexts.
function escHtml(s: unknown): string {
  return String(s == null ? '' : s).replace(/[&<>"']/g, (c) =>
    ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c] as string));
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

// Client-side detail render for pathways node kinds /studio/replay/inspect can never resolve
// (see INSPECTABLE_KINDS) — everything needed is already on the node itself.
function renderPathwaysNodeDetail(node: PathwaysNode): string {
  let html = '<div class="node-detail-row"><span class="node-detail-label">Kind</span>' + escHtml(node.kind) + '</div>';
  html += '<div class="node-detail-row"><span class="node-detail-label">Actor</span>' + escHtml(actorLabel(node)) + '</div>';
  if (node.reviewedBy) { html += '<div class="node-detail-row"><span class="node-detail-label">Reviewed by</span>' + escHtml(node.reviewedBy) + '</div>'; }
  if (node.workUnitId) { html += '<div class="node-detail-row"><span class="node-detail-label">Work unit</span>' + escHtml(node.workUnitId) + '</div>'; }
  if (node.branchId) { html += '<div class="node-detail-row"><span class="node-detail-label">Branch</span>' + escHtml(node.branchId) + '</div>'; }
  html += '<div class="node-detail-row"><span class="node-detail-label">Occurred</span>' + fmtDate(node.occurredAt) + '</div>';
  if (node.filesTouched && node.filesTouched.length) {
    html += '<div class="node-detail-row"><span class="node-detail-label">Files</span>' + escHtml(node.filesTouched.join(', ')) + '</div>';
  }
  html += '<div class="node-detail-body-text">' + escHtml(node.summary) + '</div>';
  // Slice 4 — ExternalUpdate nodes carry the KnownGoodState pair RepositorySyncService marks
  // immediately before/after applying the drift, letting the host resolve a real file-level diff
  // on demand rather than trusting the summary's own added/modified/deleted counts.
  if (node.externalSyncStateIdBefore && node.externalSyncStateIdAfter) {
    html += '<div id="external-diff-' + escHtml(node.nodeId) + '">';
    html += '<button class="ghost" data-view-external-diff="' + escHtml(node.nodeId) + '" '
      + 'data-state-before="' + escHtml(node.externalSyncStateIdBefore) + '" '
      + 'data-state-after="' + escHtml(node.externalSyncStateIdAfter) + '">View file changes</button>';
    html += '</div>';
  }
  return html;
}

interface KnownGoodDiffResult {
  differences: Array<{ relativePath: string; status: 'Added' | 'Removed' | 'Modified' | 'Unchanged' }>;
  addedCount: number;
  removedCount: number;
  modifiedCount: number;
}

function renderExternalDiff(diff: KnownGoodDiffResult): string {
  if (!diff.differences || !diff.differences.length) { return '<div class="diff-empty">No file changes.</div>'; }
  return diff.differences.map((d) => {
    const cls = d.status === 'Added' ? 'diff-add' : d.status === 'Removed' ? 'diff-del' : '';
    return '<div class="diff-line ' + cls + '">' + escHtml(d.status) + '  ' + escHtml(d.relativePath) + '</div>';
  }).join('');
}

// ── Slice 2 — agent context + file diffs ────────────────────────────────────
// Diff rendering mirrors decisionConvergence.js's renderInlineHunks/renderFileChanges (same
// ProposalFileChange/DiffHunk/DiffLine REST shape, same .diff-line/.diff-add/.diff-del classes)
// so a proposal's diff looks the same whether opened from Merge Review or from a Pathways node.
// Split-view and "Open Diff in Editor" are not reproduced here — that requires the VS Code
// TextDocumentContentProvider MergeReviewPanel.ts registers, which is out of scope for this
// compact drawer; deferred, not forgotten (see plans/pathways-workspace-history.md).
function hunkHeader(h: DiffHunk): string {
  return '@@ -' + h.beforeStart + ',' + h.beforeCount + ' +' + h.afterStart + ',' + h.afterCount + ' @@';
}

function renderInlineHunks(hunks: DiffHunk[]): string {
  if (!hunks || !hunks.length) { return '<div class="diff-empty">No textual changes.</div>'; }
  return hunks.map((h) => {
    const rows = h.lines.map((l) => {
      const prefix = l.kind === 'Added' ? '+' : l.kind === 'Removed' ? '-' : ' ';
      const cls = l.kind === 'Added' ? 'diff-add' : l.kind === 'Removed' ? 'diff-del' : '';
      return '<div class="diff-line ' + cls + '">' + prefix + escHtml(l.text) + '</div>';
    }).join('');
    return '<div class="diff-meta">' + escHtml(hunkHeader(h)) + '</div>' + rows;
  }).join('');
}

// Populated by the nodeDetail handler right before rendering — data-open-diff="{idx}" click
// delegation below looks the full before/after content up here rather than round-tripping to
// the host again (same window-global convention as decisionConvergence.js's __fileChanges,
// scoped to a module-level array here instead since this bundle isn't inline HTML).
let currentFileChanges: ProposalFileChange[] = [];

// Slice 3 (pathways-workspace-history.md) — the pathways node currently shown in the detail
// drawer, set by the click handler before the async inspect/nodeDetail round-trip resolves.
let currentInspectedNode: PathwaysNode | null = null;

// "Branch from here (new steering)" is offered only for proposal-backed nodes — Integration/
// Rejection/Superseded are the only kinds with a base/{proposalId} snapshot branch
// (InMemoryMergeService writes it at propose time) for ICounterfactualService to seed from.
// GoalStarted/DeadBranch nodes already have an equivalent affordance (the existing "Branch from
// here" playback-bar button, which seeds from the branch's current tip via branchFromCursor).
const COUNTERFACTUAL_KINDS = new Set(['Integration', 'Rejection', 'Superseded']);

function renderCounterfactualAction(node: PathwaysNode): string {
  if (!COUNTERFACTUAL_KINDS.has(node.kind) || !node.proposalId) { return ''; }
  return '<div class="node-detail-section">'
    + '<button class="ghost" data-branch-with-steering="' + escHtml(node.proposalId) + '">Branch from here (new steering)</button>'
    + '</div>';
}

// "Materialize to scratch" — any node with a workUnitId (every kind except ExternalUpdate,
// which is workspace-wide and owned by no work unit). When the node carries a snapshotId
// (Integration nodes whose MergeApplied event recorded one), this is TRUE point-in-time
// reconstruction — the repository exactly as that integration left it, via RepositorySnapshot +
// CAS. Without one it falls back to the work unit's branch's *current* content; the button
// label tells the user which they're getting.
function renderMaterializeAction(node: PathwaysNode): string {
  if (!node.workUnitId && !node.snapshotId) { return ''; }
  const label = node.snapshotId
    ? 'Materialize this point in time to scratch'
    : 'Materialize current branch state to scratch';
  return '<div class="node-detail-section">'
    + '<button class="ghost" data-materialize="' + escHtml(node.workUnitId ?? '') + '"'
    + (node.branchId ? ' data-branch="' + escHtml(node.branchId) + '"' : '')
    + (node.snapshotId ? ' data-snapshot="' + escHtml(node.snapshotId) + '"' : '')
    + '>' + label + '</button>'
    + '</div>';
}

function renderFileChangesSection(changes: ProposalFileChange[]): string {
  if (!changes || !changes.length) { return ''; }
  currentFileChanges = changes;
  let html = '<div class="node-detail-section"><h3>File diffs</h3>';
  html += changes.map((fc, idx) => {
    let entry = '<details class="file-change" open>';
    entry += '<summary>' + escHtml(fc.path) + '<span class="file-change-badge">' + escHtml(fc.changeKind) + '</span></summary>';
    entry += renderInlineHunks(fc.hunks);
    if (fc.changeKind !== 'Deleted') {
      entry += '<button class="ghost" data-pw-open-diff="' + idx + '">View Diff in Editor</button>';
    }
    entry += '</details>';
    return entry;
  }).join('');
  html += '</div>';
  return html;
}

// One delegated click handler for every drawer action, attached to the Pathways pane itself, not
// document. Two reasons, both learned the hard way: (1) the Studio Shell is one document hosting
// every view's bundle, and decisionConvergence.js registers its own document-level listener for
// its "[data-open-diff]" buttons — a document-level listener here (plus the shared attribute name
// this originally used) meant one click fired both views' handlers, opening duplicate and
// potentially wrong-content diff tabs; (2) drawer content is rebuilt on every node click, so
// per-button listeners would leak/vanish — delegation is still right, just at the pane boundary.
// Falls back to document only when the pane div is absent (defensive; the fragment always ships it).
const pathwaysPane = document.getElementById('shell-pane-trajectory-replay') ?? document;
pathwaysPane.addEventListener('click', (e: Event) => {
  const target = e.target as HTMLElement;

  const diffBtn = target.closest('[data-pw-open-diff]') as HTMLElement | null;
  if (diffBtn) {
    const idx = parseInt(diffBtn.getAttribute('data-pw-open-diff') ?? '', 10);
    const fc = currentFileChanges[idx];
    if (!fc) { return; }
    vscode.postMessage({
      type: 'pathways.openDiff',
      path: fc.path,
      beforeContent: fc.beforeContent,
      afterContent: fc.afterContent,
    });
    return;
  }

  const cfBtn = target.closest('[data-branch-with-steering]') as HTMLElement | null;
  if (cfBtn) {
    const proposalId = cfBtn.getAttribute('data-branch-with-steering');
    if (!proposalId) { return; }
    vscode.postMessage({
      type: 'createCounterfactual',
      branchId: replayState.cursor.branchId,
      proposalId,
      workUnitId: currentInspectedNode?.workUnitId,
    });
    return;
  }

  const matBtn = target.closest('[data-materialize]') as HTMLElement | null;
  if (matBtn) {
    const workUnitId = matBtn.getAttribute('data-materialize') || undefined;
    const snapshotId = matBtn.getAttribute('data-snapshot') || undefined;
    if (!workUnitId && !snapshotId) { return; }
    vscode.postMessage({
      type: 'materializeNode', workUnitId, snapshotId,
      branchId: matBtn.getAttribute('data-branch') ?? undefined,
    });
    return;
  }

  const extBtn = target.closest('[data-view-external-diff]') as HTMLElement | null;
  if (extBtn) {
    const nodeId = extBtn.getAttribute('data-view-external-diff');
    const stateBefore = extBtn.getAttribute('data-state-before');
    const stateAfter = extBtn.getAttribute('data-state-after');
    if (!nodeId || !stateBefore || !stateAfter) { return; }
    const container = document.getElementById('external-diff-' + nodeId);
    if (container) { container.innerHTML = '<div class="diff-empty">Loading…</div>'; }
    vscode.postMessage({ type: 'viewExternalDiff', nodeId, stateIdBefore: stateBefore, stateIdAfter: stateAfter });
  }
});

// Capped to the most recent 10 cycles — a long-running work unit could have dozens; the drawer
// is meant for "what led to this decision," not a full transcript (that's the dedicated
// conversation-log view elsewhere in Studio).
const MAX_CONVERSATION_ENTRIES = 10;

function renderConversationLogSection(entries: ConversationLogEntry[]): string {
  if (!entries || !entries.length) { return ''; }
  // occurredAt, not cycleNumber: a work unit's log interleaves multiple agents (planner, worker,
  // reviewer), each with its own cycle counter — cycle numbers only order within one agent.
  const ordered = [...entries].sort((a, b) => a.occurredAt.localeCompare(b.occurredAt)).slice(-MAX_CONVERSATION_ENTRIES);
  let html = '<div class="node-detail-section"><h3>Agent context</h3>';
  html += ordered.map((e) => {
    const modelBit = e.model ? ' · ' + e.model : '';
    let entry = '<div class="conv-entry">';
    entry += '<div class="conv-entry-meta">cycle ' + e.cycleNumber + ' · ' + escHtml(e.agentRole) + modelBit + ' · ' + fmtDate(e.occurredAt) + '</div>';
    if (e.assistantText) { entry += '<div class="conv-entry-text">' + escHtml(e.assistantText) + '</div>'; }
    if (e.toolCalls && e.toolCalls.length) {
      entry += '<div class="conv-tool-call">tools: ' + escHtml(e.toolCalls.map((t) => t.name).join(', ')) + '</div>';
    }
    entry += '</div>';
    return entry;
  }).join('');
  html += '</div>';
  return html;
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

// plans/pathways-workspace-history.md slice 1 — WorkspacePathways projection node/edge shape.
// Kind: "GoalStarted" | "Integration" | "Rejection" | "Superseded" | "DeadBranch" |
// "ExternalUpdate". ActorKind: "Agent" | "Human" | "External".
interface PathwaysNode {
  nodeId: string;
  kind: string;
  workUnitId?: string | null;
  branchId?: string | null;
  actorKind: string;
  actorId?: string | null;
  actorModel?: string | null;
  actorProvider?: string | null;
  summary: string;
  occurredAt: string;
  proposalId?: string | null;
  artifactId?: string | null;
  filesTouched?: string[] | null;
  snapshotId?: string | null;
  externalSyncStateIdBefore?: string | null;
  externalSyncStateIdAfter?: string | null;
  reviewedBy?: string | null;
}

interface PathwaysEdge {
  fromNodeId: string;
  toNodeId: string;
  kind: string;
}

interface PathwaysPayload {
  nodes: PathwaysNode[];
  edges: PathwaysEdge[];
  generatedAt: string;
}

// Slice 2 (pathways-workspace-history.md) — node detail context types, mirroring
// MergeReviewPanel.ts's local copies of the same REST shapes (this codebase doesn't share
// types across webview bundles).
interface DiffLine {
  kind: 'Context' | 'Added' | 'Removed';
  text: string;
}

interface DiffHunk {
  beforeStart: number;
  beforeCount: number;
  afterStart: number;
  afterCount: number;
  lines: DiffLine[];
}

interface ProposalFileChange {
  path: string;
  changeKind: 'Added' | 'Modified' | 'Deleted';
  hunks: DiffHunk[];
  beforeContent?: string | null;
  afterContent?: string | null;
}

interface ConversationLogEntry {
  logId: string;
  agentId: string;
  agentRole: string;
  cycleNumber: number;
  assistantText?: string | null;
  toolCalls: Array<{ name: string }>;
  stopReason: string;
  occurredAt: string;
  model?: string | null;
  provider?: string | null;
}

// Node kinds the backend's /studio/replay/inspect endpoint can actually resolve. It looks a node
// up via IArtifactLineageService.GetChainAsync(workUnitId), which is keyed per work unit —
// Integration/Rejection/Superseded nodes work because MergeCommandService records the
// MergeProposal's own ArtifactRef under proposal.ProposalId (same id as PathwaysNode.proposalId).
// GoalStarted/DeadBranch nodes are bare WorkUnitIds with no artifact behind them, and
// ExternalUpdate artifacts are workspace-wide (OwnedByWorkUnitId: null — see
// RepositorySyncService), so GetChainAsync can never find them by any branchId. All three render
// straight from the node's own fields instead of round-tripping to a lookup that would only ever
// 404 — richer node detail for those kinds is plans/pathways-workspace-history.md slice 2 work.
const INSPECTABLE_KINDS = new Set(['Integration', 'Rejection', 'Superseded']);

function actorLabel(node: PathwaysNode): string {
  if (node.actorKind === 'Human') { return 'human' + (node.actorId ? ':' + node.actorId : ''); }
  if (node.actorKind === 'External') { return 'external'; }
  return 'agent' + (node.actorModel ? ':' + node.actorModel : node.actorId ? ':' + node.actorId : '');
}

function pathwaysOpSummary(node: PathwaysNode): string {
  return `${node.kind} (${actorLabel(node)}): ${node.summary}`;
}

// The raw id /studio/replay/inspect expects for a node kind that can resolve — null for kinds
// rendered client-side only (see INSPECTABLE_KINDS above).
function pathwaysInspectId(node: PathwaysNode): string | null {
  if (!INSPECTABLE_KINDS.has(node.kind)) { return null; }
  return node.proposalId ?? node.artifactId ?? null;
}

function applyStageChange(workUnitId: string, stage: string | null): void {
  const branchId = workUnitIdToBranchId[workUnitId];
  if (!branchId) { return; }
  stages[branchId] = stage;
  render();
}

// Lane a node belongs to for the existing per-branch-lane SVG renderer. GoalStarted/Integration/
// Rejection/Superseded/DeadBranch nodes always carry their owning branchId; ExternalUpdate nodes
// are workspace-wide (not owned by any branch — see RepositorySyncService), so they collect into
// one synthetic lane instead of being dropped.
const EXTERNAL_LANE_ID = 'external-updates';

function laneIdFor(node: PathwaysNode): string {
  return node.branchId ?? EXTERNAL_LANE_ID;
}

// The full WorkspacePathwaysEdge set from the latest poll — drawn by the renderer as cross-lane
// connectors (child proposal → parent's node, a proposal's Integration → its own later
// Superseded, external-update chains). Replaced wholesale per poll.
let pathwaysEdges: PathwaysEdge[] = [];

// Registers branches (lanes) and nodes from the WorkspacePathways projection. Called from both
// init and workUnits message handlers so new integrations/rejections/external updates appear
// without waiting for a full re-init.
function registerPathways(pathways: PathwaysPayload): void {
  pathwaysEdges = pathways.edges ?? [];

  // Reducer applied directly, one render() at the end — dispatch() renders per action, and a poll
  // cycle replays every node every 2s (append is idempotent per nodeId), so dispatching here meant
  // a full SVG re-render per node per poll.
  let state = replayState;

  const laneIds = new Set(pathways.nodes.map(laneIdFor));
  for (const laneId of laneIds) {
    if (!state.branches[laneId]) {
      const label = laneId === EXTERNAL_LANE_ID ? 'External updates' : (goals[laneId] ?? laneId);
      state = replayReducer(state, {
        type: 'register-branch',
        branchId: laneId,
        roomId: laneId,
        label,
        basedOnBranchId: null,
        basedOnNodeId: null,
      });
    }
  }

  const ordered = [...pathways.nodes].sort((a, b) => a.occurredAt.localeCompare(b.occurredAt));
  for (const node of ordered) {
    const laneId = laneIdFor(node);
    state = replayReducer(state, {
      type: 'append-branch-node',
      branchId: laneId,
      roomId: laneId,
      opSummary: pathwaysOpSummary(node),
      payloadJson: JSON.stringify(node),
      // payloadRef carries the pathways kind so the renderer can pick per-kind shape/color
      // without JSON-parsing every node's payload on every render frame.
      payloadRef: node.kind,
      replayOpId: node.nodeId,
      // In-lane order comes from compareNodeIds, which sorts by lamport first and falls back to
      // nodeId.localeCompare when lamports tie — and every pathways node used to arrive with a
      // null lamport, so lanes ordered alphabetically by prefixed id ("failed:" sorted before
      // "goal:", proposals by GUID). Epoch millis as the lamport makes lanes chronological.
      lamport: Date.parse(node.occurredAt) || null,
      atIso: node.occurredAt,
    });
  }

  replayState = state;
  render();
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
    sessionScopeActive = msg.sessionActive === true;
    for (const wu of wus) {
      goals[wu.branchId] = wu.goal;
      stages[wu.branchId] = wu.currentStage ?? null;
      workUnitIdToBranchId[wu.workUnitId] = wu.branchId;
    }

    replayState = createInitialReplayState(roomId, roomId, goals[roomId] ?? roomId);

    // plans/pathways-workspace-history.md slice 1 — register pathways lanes/nodes before
    // connecting the WebSocket (which now only handles live stage-change events).
    if (msg.pathways) {
      registerPathways(msg.pathways as PathwaysPayload);
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
    sessionScopeActive = msg.sessionActive === true;
    for (const wu of (msg.workUnits as WorkUnitRef[])) {
      goals[wu.branchId] = wu.goal;
      workUnitIdToBranchId[wu.workUnitId] = wu.branchId;
      if (wu.currentStage !== undefined) { stages[wu.branchId] = wu.currentStage; }
    }
    // plans/pathways-workspace-history.md slice 1 — merge updated pathways into existing state
    if (msg.pathways) {
      registerPathways(msg.pathways as PathwaysPayload);
    }
    render();
    // Piggyback the Plan sub-tab's refresh on the host's existing 2s workUnits poll — only while
    // it's the active pane — so status colors climb the tree live without a second timer.
    if (planPaneActive) { requestPlanTree(); }
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

  if (msg.type === 'pathways.planTree') {
    planData = (msg.data as PlanTreeData | null) ?? null;
    planContractsById = new Map((planData?.contracts ?? []).map((c) => [c.contractId, c]));
    renderPlan(msg.sessionId as string | undefined);
    return;
  }

  if (msg.type === 'nodeDetail') {
    // Stale-response guard: only render if this response answers the *latest* clicked node.
    // requestNodeId is absent on responses from hosts predating it — render those unguarded
    // rather than dropping every detail (same graceful-degrade posture as the pathways fetch).
    if (msg.requestNodeId && currentInspectedNode && msg.requestNodeId !== currentInspectedNode.nodeId) {
      return;
    }
    if (nodeDetailBody) {
      let html = renderNodeDetail(msg.error ? { error: msg.error } : msg.detail);
      // Slice 2 — present only when DagReplayPanel resolved a MergeProposal artifact with a
      // workUnitId (see its inspectNode handler); absent for every other inspect response.
      if (msg.conversationLog) { html += renderConversationLogSection(msg.conversationLog as ConversationLogEntry[]); }
      if (msg.fileChanges) { html += renderFileChangesSection(msg.fileChanges as ProposalFileChange[]); }
      // Slice 3 — offered whenever this response resolved a proposal-backed node, regardless of
      // whether file changes/conversation log came back (both are best-effort on the host side).
      if (currentInspectedNode) {
        if (currentInspectedNode.reviewedBy) {
          html = '<div class="node-detail-row"><span class="node-detail-label">Reviewed by</span>'
            + escHtml(currentInspectedNode.reviewedBy) + '</div>' + html;
        }
        html += renderCounterfactualAction(currentInspectedNode);
        html += renderMaterializeAction(currentInspectedNode);
      }
      nodeDetailBody.innerHTML = html;
    }
    return;
  }

  // Slice 4 — result of a "View file changes" click on an ExternalUpdate node (see
  // renderPathwaysNodeDetail). Targets its own container by nodeId rather than replacing the
  // whole drawer, since the rest of the drawer's content (goal/actor/summary rows) is already
  // correct and doesn't need re-rendering.
  if (msg.type === 'externalDiff') {
    const container = document.getElementById('external-diff-' + (msg.nodeId as string));
    if (container) {
      container.innerHTML = msg.error
        ? '<div class="diff-empty">' + escHtml(String(msg.error)) + '</div>'
        : renderExternalDiff(msg.diff as KnownGoodDiffResult);
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
