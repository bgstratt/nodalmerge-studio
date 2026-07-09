# Changelog

## 0.1.10 — 2026-07-08

- **Pathways is now workspace history, not an agent task list.** The Pathways
  tab renders the new `WorkspacePathways` projection — goals started,
  integrations, rejections, dead branches, and external file updates, each
  attributed to an actor (agent/human/external) — instead of the old
  per-work-unit artifact + orchestration decision-log dump. The
  NoOp/Enqueue/SpawnPlanner chatter no longer appears there (it stays in the
  per-goal views where it belongs). Lanes order chronologically; selecting a
  session dims out-of-session lanes instead of hiding workspace history.
- **Pathways node detail.** Clicking an Integration/Rejection/Superseded node
  shows the proposal's file diffs (inline, plus "View Diff in Editor") and the
  agent conversation that produced it. External-update nodes list the changed
  files and can fetch a before/after file-level diff. New actions: "Branch
  from here (new steering)" (re-run from the proposal's base state with a
  different profile/goal/constraint), "Materialize to scratch workspace"
  (reconstructs the branch's current content into
  `{extension storage}/pathways-scratch/{branch}/{timestamp}` — never the live
  repo), and a "Sync now" toolbar button that resyncs external changes using
  the host's own configured repository path.
- **Pathways history is event-sourced and tamper-proof against supersede.**
  A proposal that merged and was later superseded by reconciliation now
  keeps *both* moments in the graph (its Integration node and its Superseded
  node, chained), with true transition timestamps from the execution event
  log. Nested topology: a fan-out child's proposal chains to its parent's
  proposal node, not straight to the root goal.
- **True point-in-time materialize.** Integration nodes now carry the
  repository snapshot recorded at apply time (including multi-repo
  write-backs, previously not snapshotted at all); "Materialize this point
  in time to scratch" reconstructs the repo exactly as that integration
  left it, via snapshot + content-addressed store — never the live repo.
- **Reviewer identity.** Approve/reject now records who decided ("user" or
  the reviewer agent id) on the proposal, in the event log, and in the
  Pathways drawer ("Reviewed by").
- **Pathways DAG visual.** Per-kind node shapes/colors (goal, integration,
  rejection, superseded, dead branch, external update) with a legend row,
  and the projection's edges drawn as cross-lane connectors.
- **Fixes.** Webview HTML escaping in Pathways was a no-op (rendered LLM/diff
  text unescaped); "View Diff" clicks no longer double-fire across Studio
  Shell views (duplicate/wrong diff tabs); node detail no longer renders a
  stale response after rapid node clicks.

## 0.1.9 — 2026-07-08

- **Candidate conflict reconciliation.** When promotion branches are on and two
  proposals land on the shared `candidate` branch touching the same file paths
  (or two fan-out sibling work units conflict under the same goal — now
  distinguished as a **Task Conflict**), the Activity Center lists the
  conflict with four ways to resolve it: **View Conflict Diff** (read-only
  side-by-side of the candidate branch vs. the losing proposal), **Reconcile**
  (spawns a dedicated reconciliation work unit seeded from the conflicting
  diffs plus optional steering notes — auto-spawns if a **Reconciler** agent
  profile is configured, otherwise created for manual spawn), **Restart**
  (rejects the losing proposal and restarts its goal in Revert mode from a
  clean branch snapshot), and **Resolve manually** (submit resolved file
  content directly, recorded as a synthetic merged proposal that supersedes
  the losing one(s)).
- **Edit File / Resync Workspace** in Decision Convergence (Review): both the
  normal proposal diff view and the apply-time conflict-report view now have
  an inline **Edit File** button per changed/conflicting path, with a
  **Resync Workspace** button appearing once you've edited it, to pull that
  edit back into the work unit's branch before deciding.
- New REST surface backing the above: `GET /studio/branches/candidate/conflicts`,
  `POST /studio/branches/candidate/conflicts/{id}/reconcile`,
  `POST /studio/branches/candidate/conflicts/{id}/resolve`,
  `GET /studio/workunits/{id}/task-conflicts`,
  `POST /studio/workunits/{id}/task-conflicts/{conflictId}/reconcile`,
  `POST /studio/workunits/{id}/task-conflicts/{conflictId}/resolve`,
  `GET /studio/merges/{id}/constituents`.
- Review policy and profile/topology selection UX cleanup in Model & Agent
  Studio and the Goal Workspace (shared webview chrome, trimmed dead code in
  `AgentConfigPanel` and `modelAgentStudio.js`).

## 0.1.8 — 2026-07-06

- Bumped bundled `NodalMerge.DotNetHost` to 0.2.0, which converges blob storage
  on one canonical cross-runtime layout: a flat, global content-addressed pool
  at `data/blobs/blake3/<hash>` (no shard directories, no `.blob` extension).
- **Existing workspaces convert themselves automatically** — on the first blob
  access after upgrading, the store migrates legacy `<shard>/<hash>.blob` files
  into `blake3/`, dedupes identical content, and writes a `.layout-v2` marker so
  the migration runs exactly once. No manual steps; anything unrecognized is
  quarantined into `.migration-skipped/` rather than deleted.
- Blob writes are now atomic (temp + rename), fixing a race where concurrent
  writes of the same asset could fail with a file-sharing violation.

## 0.1.6 — 2026-07-03

- Bumped bundled `NodalMerge.DotNetHost` to 0.1.4, picking up two correctness fixes:
  - Catch-up pack imports now retry parent-ordering rejects to fixpoint, so
    reconnecting after offline edits and late-joining a room no longer silently
    drops nodes when a pack arrives out of topological order.
  - Native library resolution prefers the NuGet-packaged `runtimes/<rid>/native`
    binaries over stale dev builds found in sibling repo checkouts
    (`NODALMERGE_HOST_FFI_DLL` / `NODALMERGE_LOCAL_FFI_DLL` still override).
  - No public API, FFI, or wire changes.

## 0.1.5 — 2026-07-02

- Bumped bundled `NodalMerge.DotNetHost` to 0.1.2, picking up core engine improvements:
  - Text projection rewritten around a chunked order-statistic RGA — 50k-op replay
    throughput up from 5.5k to 214k ops/sec.
  - Incremental map/list/blob/conflict caches replace full-history replay on reads,
    and conflict detection now streams winner/loser pairs as they happen.
  - No public API, FFI, or wire changes — this is a transparent performance upgrade.

## 0.1.4 and earlier

See git history.
