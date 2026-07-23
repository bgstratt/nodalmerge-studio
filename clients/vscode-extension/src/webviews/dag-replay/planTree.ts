// Read-only "Plan" sub-tab renderer for the Pathways panel: the session's recursive plan
// decomposition as a top-down SVG tree (root goal at top, children below), with parent→child,
// dependsOn, and provides/consumes contract edges. Fed by GET /studio/sessions/{id}/plan-tree
// (server-composed), so this module only lays out and draws — no plan parsing or joins here.
//
// Self-contained (own SVG helpers) because the dag-replay bundle can't import the studio-views
// helpers. HTML escaping is injected (`esc`) rather than duplicated, so it reuses main.ts's vetted
// escHtml and can never regress to a no-op.

const SVG_NS = 'http://www.w3.org/2000/svg';

export interface PlanNode {
  workUnitId: string;
  parentWorkUnitId: string | null;
  goal: string;
  status: string;
  currentStage?: string | null;
  depth: number;
  kind: 'leaf' | 'compound';
  sliceId?: string | null;
  sliceGoal?: string | null;
  fileScope: string[];
  steps: string[];
  provides: string[];
  consumes: string[];
  dependsOn: string[];
}

export interface PlanEdge {
  from: string;
  to: string;
  kind: 'parent' | 'depends' | 'contract';
  contractId?: string;
}

export interface PlanContractDef { contractId: string; description: string; }

export interface PlanTreeData {
  rootWorkUnitId: string;
  nodes: PlanNode[];
  edges: PlanEdge[];
  contracts: PlanContractDef[];
}

/** Persisted pan/zoom of the plan canvas (a transform on the viewport group). */
export interface PlanView { k: number; tx: number; ty: number; }

/** Content bounds in world units, returned so the caller can compute a fit transform. */
export interface PlanBounds { width: number; height: number; }

export const MIN_ZOOM = 0.2;
export const MAX_ZOOM = 3;

/** Center `bounds` in a `vw`×`vh` viewport at a scale that fits (never enlarging past 1:1). */
export function fitView(bounds: PlanBounds, vw: number, vh: number): PlanView {
  if (bounds.width <= 0 || bounds.height <= 0 || vw <= 0 || vh <= 0) { return { k: 1, tx: 0, ty: 0 }; }
  const pad = 28;
  const k = Math.max(MIN_ZOOM, Math.min(1, (vw - pad) / bounds.width, (vh - pad) / bounds.height));
  return { k, tx: (vw - bounds.width * k) / 2, ty: (vh - bounds.height * k) / 2 };
}

// Layout constants (px).
const NODE_W = 168;
const NODE_H = 38;
const H_GAP = 26;
const LEVEL_H = 96;
const MARGIN = 20;

const EDGE_COLORS = { parent: '#8a8a8a', depends: '#cca700', contract: '#c586c0' };

// Muted status → color for the node's left status bar + border tint. Covers both the legacy and the
// queue-pipeline WorkUnitStatus values.
function statusColor(status: string): string {
  switch (status) {
    case 'Merged':
    case 'Completed':
      return '#4dac26';
    case 'Reviewing':
    case 'Proposed':
      return '#3794ff';
    case 'Executing':
    case 'Queued':
    case 'Retrying':
    case 'Active':
      return '#cca700';
    case 'Failed':
    case 'DeadLettered':
      return '#f14c4c';
    case 'Cancelled':
      return '#8a8a8a';
    default:
      return '#6a6a6a';
  }
}

function el(tag: string, attrs: Record<string, string | number>): SVGElement {
  const e = document.createElementNS(SVG_NS, tag) as SVGElement;
  for (const [k, v] of Object.entries(attrs)) { e.setAttribute(k, String(v)); }
  return e;
}

function truncate(s: string, max: number): string {
  return s.length > max ? s.slice(0, max - 1) + '…' : s;
}

interface Box { x: number; y: number; }

// Tidy top-down layout: leaves get sequential x slots; a parent centers over its children. y is the
// node's depth. Stable child order = the server's BFS order (nodes arrive parent-before-child).
function layout(nodes: PlanNode[]): { pos: Map<string, Box>; width: number; height: number } {
  const byId = new Map(nodes.map((n) => [n.workUnitId, n]));
  const kids = new Map<string, PlanNode[]>();
  const roots: PlanNode[] = [];
  for (const n of nodes) {
    if (n.parentWorkUnitId && byId.has(n.parentWorkUnitId)) {
      (kids.get(n.parentWorkUnitId) ?? kids.set(n.parentWorkUnitId, []).get(n.parentWorkUnitId)!).push(n);
    } else {
      roots.push(n);
    }
  }

  const pos = new Map<string, Box>();
  let cursor = MARGIN;
  let maxDepth = 0;
  const place = (n: PlanNode): void => {
    maxDepth = Math.max(maxDepth, n.depth);
    const children = kids.get(n.workUnitId) ?? [];
    const y = MARGIN + n.depth * LEVEL_H;
    if (children.length === 0) {
      pos.set(n.workUnitId, { x: cursor, y });
      cursor += NODE_W + H_GAP;
    } else {
      for (const c of children) { place(c); }
      const first = pos.get(children[0].workUnitId)!.x;
      const last = pos.get(children[children.length - 1].workUnitId)!.x;
      pos.set(n.workUnitId, { x: (first + last) / 2, y });
    }
  };
  for (const r of roots) { place(r); }

  return {
    pos,
    width: Math.max(cursor - H_GAP + MARGIN, NODE_W + 2 * MARGIN),
    height: MARGIN * 2 + maxDepth * LEVEL_H + NODE_H,
  };
}

function edgePath(from: Box, to: Box): string {
  // Bottom-center of `from` → top-center of `to`, cubic for a gentle S when they aren't stacked.
  const x1 = from.x + NODE_W / 2, y1 = from.y + NODE_H;
  const x2 = to.x + NODE_W / 2, y2 = to.y;
  const midY = (y1 + y2) / 2;
  return `M ${x1} ${y1} C ${x1} ${midY}, ${x2} ${midY}, ${x2} ${y2}`;
}

function siblingPath(from: Box, to: Box): string {
  // Right-center of `from` → left-center of `to` for same-row dependsOn/contract links, bowed down
  // so it doesn't run through the node row.
  const x1 = from.x + NODE_W, y1 = from.y + NODE_H / 2;
  const x2 = to.x, y2 = to.y + NODE_H / 2;
  const dip = Math.max(24, Math.abs(x2 - x1) * 0.18);
  const midX = (x1 + x2) / 2;
  return `M ${x1} ${y1} C ${midX} ${y1 + dip}, ${midX} ${y2 + dip}, ${x2} ${y2}`;
}

/**
 * Render `data` into `svg`, laying the tree out inside a transformed viewport group (id
 * `pw-viewport`) so pan/zoom is a cheap attribute update the caller can drive without a re-render,
 * and it survives the poll-driven re-render (the caller passes the persisted `view` back in).
 * Returns the content bounds so the caller can compute a fit. Click handling is delegated by the
 * caller reading `data-wu` off `.clickable` node groups.
 */
export function renderPlanTree(svg: SVGSVGElement, data: PlanTreeData, view: PlanView): PlanBounds {
  svg.textContent = '';
  const { pos, width, height } = layout(data.nodes);

  // Arrowhead for contract edges (direction = provider → consumer). Lives in <defs> outside the
  // viewport — markers resolve by id regardless of tree position.
  const defs = el('defs', {});
  const marker = el('marker', {
    id: 'pw-arrow', viewBox: '0 0 8 8', refX: 7, refY: 4,
    markerWidth: 6, markerHeight: 6, orient: 'auto-start-reverse',
  });
  marker.appendChild(el('path', { d: 'M0 0 L8 4 L0 8 z', fill: EDGE_COLORS.contract }));
  defs.appendChild(marker);
  svg.appendChild(defs);

  const viewport = el('g', { id: 'pw-viewport', transform: `translate(${view.tx} ${view.ty}) scale(${view.k})` });
  svg.appendChild(viewport);

  // Edges first (under the nodes).
  for (const edge of data.edges) {
    const from = pos.get(edge.from), to = pos.get(edge.to);
    if (!from || !to) { continue; }
    if (edge.kind === 'parent') {
      viewport.appendChild(el('path', { d: edgePath(from, to), fill: 'none', stroke: EDGE_COLORS.parent, 'stroke-width': 1.5, opacity: 0.7 }));
    } else if (edge.kind === 'depends') {
      viewport.appendChild(el('path', { d: siblingPath(from, to), fill: 'none', stroke: EDGE_COLORS.depends, 'stroke-width': 1.5, 'stroke-dasharray': '4 3', opacity: 0.8 }));
    } else {
      viewport.appendChild(el('path', { d: siblingPath(from, to), fill: 'none', stroke: EDGE_COLORS.contract, 'stroke-width': 1.5, opacity: 0.9, 'marker-end': 'url(#pw-arrow)' }));
    }
  }

  // Nodes.
  for (const n of data.nodes) {
    const p = pos.get(n.workUnitId);
    if (!p) { continue; }
    const color = statusColor(n.status);
    const g = el('g', { class: 'clickable', 'data-wu': n.workUnitId }) as SVGGElement;

    // Compound = a stacked-card shadow behind the node to signal "contains a sub-plan".
    if (n.kind === 'compound') {
      g.appendChild(el('rect', { x: p.x + 4, y: p.y + 4, width: NODE_W, height: NODE_H, rx: 6, fill: color, 'fill-opacity': 0.12, stroke: color, 'stroke-opacity': 0.4 }));
    }
    g.appendChild(el('rect', {
      x: p.x, y: p.y, width: NODE_W, height: NODE_H, rx: 6,
      fill: color, 'fill-opacity': 0.14, stroke: color, 'stroke-width': n.kind === 'compound' ? 2 : 1.2,
    }));
    // Status bar (left edge).
    g.appendChild(el('rect', { x: p.x, y: p.y, width: 4, height: NODE_H, rx: 2, fill: color }));

    // Label fill comes from a CSS rule (#plan-svg text.pw-node-label) — SVG presentation attributes
    // can't resolve var(--nm-fg), so theming the text has to go through the stylesheet.
    const label = truncate(n.goal || n.sliceGoal || n.workUnitId, 24);
    const t = el('text', { class: 'pw-node-label', x: p.x + 12, y: p.y + NODE_H / 2 + 1, 'dominant-baseline': 'middle', 'font-size': 12 });
    t.textContent = label;
    g.appendChild(t);

    // Hover tooltip: full goal + status/stage.
    const title = document.createElementNS(SVG_NS, 'title');
    title.textContent = `${n.goal}\n[${n.status}${n.currentStage ? ' · ' + n.currentStage : ''}${n.kind === 'compound' ? ' · compound' : ''}]`;
    g.appendChild(title);

    viewport.appendChild(g);
  }

  return { width, height };
}

/** Detail-drawer HTML for a clicked node — the "actual goal from the metadata" plus slice detail. */
export function renderPlanNodeDetail(
  n: PlanNode,
  contractsById: Map<string, PlanContractDef>,
  esc: (s: unknown) => string,
): string {
  const row = (label: string, value: string): string =>
    `<div class="pw-meta-row"><span class="pw-meta-label">${esc(label)}</span>${value}</div>`;
  const chips = (ids: string[]): string =>
    ids.length ? ids.map((id) => `<span class="pw-chip">${esc(id)}</span>`).join('') : '<span style="opacity:0.5">—</span>';

  let html = '';
  html += row('Kind', `<span class="pw-chip">${esc(n.kind === 'compound' ? 'Compound (sub-planner)' : 'Leaf (worker)')}</span>`);
  html += row('Status', `<span class="pw-chip">${esc(n.status)}${n.currentStage ? ' · ' + esc(n.currentStage) : ''}</span>`);
  if (n.sliceId) { html += row('Slice', `<span class="pw-chip">${esc(n.sliceId)}</span>`); }
  if (n.fileScope.length) { html += row('File scope', chips(n.fileScope)); }
  if (n.provides.length) { html += row('Provides', chips(n.provides)); }
  if (n.consumes.length) { html += row('Consumes', chips(n.consumes)); }

  html += `<div class="pw-plan-section"><h3>Goal</h3><div class="pw-goal-text">${esc(n.goal)}</div></div>`;
  if (n.sliceGoal && n.sliceGoal !== n.goal) {
    html += `<div class="pw-plan-section"><h3>Slice goal</h3><div class="pw-goal-text">${esc(n.sliceGoal)}</div></div>`;
  }
  if (n.steps.length) {
    html += `<div class="pw-plan-section"><h3>Steps</h3><ol style="margin:0;padding-left:18px">${n.steps.map((s) => `<li>${esc(s)}</li>`).join('')}</ol></div>`;
  }

  const relatedContracts = [...new Set([...n.provides, ...n.consumes])]
    .map((id) => contractsById.get(id)).filter((c): c is PlanContractDef => !!c);
  if (relatedContracts.length) {
    html += `<div class="pw-plan-section"><h3>Contracts</h3>${relatedContracts
      .map((c) => `<div class="pw-meta-row"><span class="pw-chip">${esc(c.contractId)}</span> ${esc(c.description)}</div>`)
      .join('')}</div>`;
  }
  return html;
}
