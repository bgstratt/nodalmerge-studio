import type { ReplayState } from './branchReplay';
import { computeBranchOffsets, computeMaxBranchColumn } from './replayLayout';

// ── Layout constants ───────────────────────────────────────────────────────

const NODE_R   = 6;
const MERGE_R  = 8;
const LANE_H   = 48;
const STEP_W   = 38;
const PAD_L    = 190;
const PAD_T    = 32;
const PAD_R    = 40;
const PAD_B    = 24;
const MAX_LABEL = 24;

// ── SVG helpers ────────────────────────────────────────────────────────────

const SVG_NS = 'http://www.w3.org/2000/svg';

function el(tag: string, attrs: Record<string, string | number>): SVGElement {
  const e = document.createElementNS(SVG_NS, tag) as SVGElement;
  for (const [k, v] of Object.entries(attrs)) { e.setAttribute(k, String(v)); }
  return e;
}

function text(content: string, attrs: Record<string, string | number>): SVGElement {
  const e = el('text', attrs);
  e.textContent = content;
  return e;
}

function svgTitle(content: string): SVGElement {
  const t = document.createElementNS(SVG_NS, 'title');
  t.textContent = content;
  return t;
}

function truncate(s: string, max: number): string {
  return s.length > max ? s.slice(0, max - 1) + '…' : s;
}

// ── Colours ────────────────────────────────────────────────────────────────

const C = {
  lane:         '#3c3c3c',
  connector:    '#555',
  merge_edge:   '#7c4dff',
  node:         '#505050',
  head:         '#4dac26',
  cursor_live:  '#4dac26',   // head node in live mode = green
  cursor_play:  '#3794ff',   // cursor in playback mode = blue
  merge_node:   '#7c4dff',
  label:        '#888',
  label_main:   '#bbb',
};

// ── Main render ────────────────────────────────────────────────────────────

export function renderDag(
  svg: SVGSVGElement,
  state: ReplayState,
  goals: Record<string, string>,
): void {
  while (svg.firstChild) { svg.removeChild(svg.firstChild); }

  const allIds = Object.keys(state.branches);
  if (allIds.length === 0) { return; }

  const sortedIds = [
    ...allIds.filter(id => state.branches[id]?.isMain),
    ...allIds.filter(id => !state.branches[id]?.isMain),
  ];

  const offsets  = computeBranchOffsets(sortedIds, state.branches, state.branchNodeIds);
  const maxCol   = computeMaxBranchColumn(sortedIds, offsets, state.branchNodeIds);
  const colCount = Math.max(maxCol + 1, 4);

  const W = PAD_L + colCount * STEP_W + PAD_R;
  const H = PAD_T + sortedIds.length * LANE_H + PAD_B;
  svg.setAttribute('width',   String(W));
  svg.setAttribute('height',  String(H));
  svg.setAttribute('viewBox', `0 0 ${W} ${H}`);

  const laneY: Record<string, number> = {};
  sortedIds.forEach((id, i) => { laneY[id] = PAD_T + i * LANE_H; });

  // Pre-compute pixel position and index for every node
  const nodePos: Record<string, { x: number; y: number; branchId: string; nodeIndex: number }> = {};
  for (const branchId of sortedIds) {
    const ids = state.branchNodeIds[branchId] ?? [];
    const off = offsets[branchId] ?? 0;
    const y   = laneY[branchId];
    ids.forEach((nid, i) => {
      nodePos[nid] = { x: PAD_L + (off + i) * STEP_W, y, branchId, nodeIndex: i };
    });
  }

  const root = document.createElementNS(SVG_NS, 'g') as SVGGElement;

  // ── Fork connectors ────────────────────────────────────────────────────
  for (const branchId of sortedIds) {
    const branch = state.branches[branchId];
    if (!branch?.basedOnBranchId || !branch.basedOnNodeId) { continue; }
    const fromPos = nodePos[branch.basedOnNodeId];
    const ids     = state.branchNodeIds[branchId] ?? [];
    if (!fromPos || ids.length === 0) { continue; }
    const off = offsets[branchId] ?? 0;
    const toX = PAD_L + off * STEP_W;
    const toY = laneY[branchId];
    const cx  = (fromPos.x + toX) / 2;
    root.appendChild(el('path', {
      d:                  `M ${fromPos.x} ${fromPos.y} C ${cx} ${fromPos.y} ${cx} ${toY} ${toX} ${toY}`,
      stroke:             C.connector,
      'stroke-width':     '1.5',
      fill:               'none',
      'stroke-dasharray': '5,3',
    }));
  }

  // ── Lane lines + labels ────────────────────────────────────────────────
  for (const branchId of sortedIds) {
    const ids  = state.branchNodeIds[branchId] ?? [];
    const off  = offsets[branchId] ?? 0;
    const y    = laneY[branchId];
    const main = state.branches[branchId]?.isMain ?? false;

    if (ids.length >= 2) {
      const x1 = PAD_L + off * STEP_W;
      const x2 = PAD_L + (off + ids.length - 1) * STEP_W;
      root.appendChild(el('line', {
        x1, y1: y, x2, y2: y,
        stroke: C.lane, 'stroke-width': main ? '2' : '1.5',
      }));
    }

    const raw   = goals[branchId] ?? state.branches[branchId]?.label ?? branchId;
    root.appendChild(text(truncate(raw, MAX_LABEL), {
      x: PAD_L - 10, y: y + 4,
      'text-anchor':  'end',
      fill:           main ? C.label_main : C.label,
      'font-size':    main ? '12' : '11',
      'font-family':  'var(--nm-mono, monospace)',
      'font-weight':  main ? '600' : '400',
    }));
  }

  // ── Merge edges ────────────────────────────────────────────────────────
  for (const node of Object.values(state.nodesById)) {
    if (node.parentIds.length < 2) { continue; }
    const toPos = nodePos[node.nodeId];
    if (!toPos) { continue; }
    for (let i = 1; i < node.parentIds.length; i++) {
      const fromPos = nodePos[node.parentIds[i]];
      if (!fromPos) { continue; }
      const cx = (fromPos.x + toPos.x) / 2;
      root.appendChild(el('path', {
        d:              `M ${fromPos.x} ${fromPos.y} C ${cx} ${fromPos.y} ${cx} ${toPos.y} ${toPos.x} ${toPos.y}`,
        stroke:         C.merge_edge,
        'stroke-width': '1.5',
        fill:           'none',
      }));
    }
  }

  // ── Nodes ──────────────────────────────────────────────────────────────
  const cursorId   = state.cursor.nodeId;
  const isPlayback = state.cursor.mode === 'playback';

  for (const branchId of sortedIds) {
    const ids = state.branchNodeIds[branchId] ?? [];
    ids.forEach((nodeId, nodeIndex) => {
      const node = state.nodesById[nodeId];
      if (!node) { return; }
      const pos = nodePos[nodeId];
      if (!pos) { return; }

      const isMerge  = node.parentIds.length > 1;
      const isCursor = nodeId === cursorId;
      const isHead   = state.branches[branchId]?.headNodeId === nodeId;

      const fill   = isCursor
        ? (isPlayback ? C.cursor_play : C.cursor_live)
        : isMerge ? C.merge_node
        : isHead  ? C.head
                  : C.node;

      const r = isMerge ? MERGE_R : NODE_R;

      const circle = el('circle', {
        cx:             pos.x,
        cy:             pos.y,
        r,
        fill,
        stroke:         isCursor ? '#fff' : '#1e1e1e',
        'stroke-width': isCursor ? '2.5' : '1',
        class:          'clickable',
        // data attrs for click delegation in main.ts
        'data-branch-id':   branchId,
        'data-node-index':  nodeIndex,
        'data-node-id':     nodeId,
      });

      circle.appendChild(svgTitle(
        node.opSummary + '\n' + nodeId.slice(0, 16) + '\n' + node.atIso.slice(0, 19),
      ));
      root.appendChild(circle);
    });
  }

  svg.appendChild(root);
}
