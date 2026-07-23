# Pathways → Plan view (read-only session plan tree)

A second sub-tab under **Pathways** that renders a session's **plan decomposition** as a
read-only top-down tree: root goal at top, its child work units below, recursively (the
recursive-planning tree — compound slices become child work units). Complements, and is
deliberately distinct from, the Goal Workspace **Decision Tree**:

- **Decision Tree** (Goal Workspace) — interactive, decision/fork-centric; where you *act*
  (fork, re-explore, steer, right-click actions). Indented div list, no edges.
- **Plan view** (Pathways) — read-only, slice/decomposition-centric, full-canvas; where you
  *understand the shape*. Node-link tree with dependency + contract **edges** and roll-up
  status coloring.

Session selection is shared: the Plan view uses the panel's existing
`getEffectiveSessionId()` (the Pathways session-override dropdown, falling back to the shell
session), so it tracks the same session the Trajectory sub-tab already follows.

## What a node shows

- **Identity/shape:** work unit goal (title), Leaf vs Compound (icon/shape — Compound = a
  sub-planner interior node), depth level.
- **Status color:** by `WorkUnitStatus` (+ `currentStage`) so you watch reconciliation climb
  the tree bottom-up (grandchildren Merged → interior Merged → root). Reuses the `--nm-*`
  semantic hexes already mirrored in `dagRenderer.ts` (`STAGE_COLORS`).
- **Hover:** native SVG `<title>` — truncated goal + status/stage (v1). (Custom hover card is a
  later nice-to-have.)
- **Click:** a detail drawer shows the full work-unit goal (the "actual goal" from the Decision
  Lens metadata) plus the resolved slice: kind, fileScope, steps, `dependsOn`, `provides`,
  `consumes`. Rendered from already-loaded tree data — no extra round-trip.

## Edges

- **parent → child** — the decomposition (solid, subtle).
- **`dependsOn`** — sibling sequencing (dashed). `WorkUnit.DependsOn` already holds resolved
  workUnitIds (FanOutService maps sliceId→workUnitId), so these are direct.
- **`provides` → `consumes`** — peer contract links (distinct color + arrowhead, provider →
  consumer). Computed server-side by matching a `contractId` a slice provides to the sibling(s)
  that consume it. This is the payoff — contract coordination is otherwise invisible.

## Phase 1 — backend aggregate endpoint

`GET /studio/sessions/{sessionId}/plan-tree` in `StudioRestEndpoints.cs`, next to
`/studio/sessions/{sessionId}/workunits` (`:3079-3117`). Reuses that endpoint's BFS walk
(`GetChildrenAsync`, `:3103-3113`) and `ToWorkUnitResponse` (`:1197-1238`), and for each parent
node reads its plan via `FanOutService.ReadPlanFromArtifactAsync` (`:213-236`) — last `Plan`
artifact `Body`, else branch `plan.json` fallback — deserializes `PlanDocument`
(`Contracts/Domain/PlanDocument.cs`), and joins each `PlanSlice` to its realized child work unit
via `FanOutInfo.SliceId`.

Response shape:

```jsonc
{
  "rootWorkUnitId": "WU-…",
  "nodes": [{
    "workUnitId", "parentWorkUnitId", "goal", "status", "currentStage",
    "depth", "kind": "leaf|compound", "sliceId",
    "sliceGoal", "fileScope": [], "steps": [],
    "provides": [], "consumes": [], "dependsOn": []        // workUnitIds
  }],
  "edges": [{ "from": "WU-…", "to": "WU-…", "kind": "parent|depends|contract", "contractId?": "c-…" }],
  "contracts": [{ "contractId", "description" }]
}
```

Server does the plan-parse + slice↔workunit join + producer/consumer edge resolution once, so
the webview stays dumb (no untyped plan parsing, no re-implementing the artifact fallback).
Reuses `WorkspaceReviewScope`-free, read-only services already registered. **Test:** an
Integration test that builds a 2-level plan (one compound slice + a provides/consumes peer pair)
and asserts the endpoint returns the nested nodes, the correct kind per node, and both a
`depends` and a `contract` edge.

## Phase 2 — panel host (DagReplayPanel.ts)

- Add a **Trajectory | Plan** sub-tab bar into `DAG_REPLAY_HTML` (`:676`) and the tab CSS into
  `DAG_REPLAY_CSS` (`:555`), copying the Insights `.in-tab*` rules. Two panes:
  `#pw-pane-trajectory` (wraps today's content) and `#pw-pane-plan`.
- New host fetch: `get('/studio/sessions/{id}/plan-tree')` keyed on `getEffectiveSessionId()`,
  posted to the webview as a **namespaced** message `pathways.planTree`; webview requests via
  `pathways.loadPlanTree` (mirrors the `replayModeChanged`→`replayModeData` round trip at
  `:243-256`). Use the abort-wrapped `get` like `InsightsPanel.get` in case the walk is slow.
- Fetch when the Plan sub-tab is activated and on the existing 2s poll **only while the Plan tab
  is active** (cheap live status updates so the roll-up animates).

## Phase 3 — webview renderer (src/webviews/dag-replay/)

- Sub-tab toggle in `main.ts` (mirror the `replay-mode` show/hide at `:207-232`); plain
  `display` swap of the two panes (not `.active`, which is for outer shell tabs).
- New `planTree.ts` module: a **top-down tree layout** (depth → y, subtree-width → x) feeding the
  SVG `el`/`text`/`svgTitle` helpers from `dagRenderer.ts` (`:20-32`); parent→child + `dependsOn`
  + contract edges via the bezier path builders (`:198-218`), adding one `<marker>` arrowhead def
  for contract direction. Node shape/icon by kind; fill by status color.
- Click → render a detail drawer (right side, Decision-Lens-like `.meta-grid` + plan section)
  from the node's already-loaded data. **Every field through `escHtml`** (`main.ts:241`); no
  inline handlers — delegated listeners reading `data-*` off SVG nodes (like `main.ts:106-164`).
- Reuse `window.__nmVscode` (never call `acquireVsCodeApi()` again).

## Phase 4 — polish + verify

- Legend (leaf vs compound, status colors, edge kinds), loading + empty states ("no plan yet /
  flat plan"), and graceful handling of a depth-1 flat session (still a valid 1-level tree).
- Build the extension bundle (`esbuild`), typecheck, and smoke against a live recursive session.

## Out of scope (v1)

Editing from this view (read-only by design) · custom hover cards (native `<title>` for now) ·
historical scrubbing of the plan-as-it-was-at-time-T (Trajectory already owns time; a nice future
tie-in) · contract *schema* rendering beyond id/description.

## Sequencing

Backend endpoint + test (1) → panel host wiring (2) → webview renderer (3) → polish/smoke (4).
Build after 1 (Integration test) and after 3 (esbuild + extension smoke).
