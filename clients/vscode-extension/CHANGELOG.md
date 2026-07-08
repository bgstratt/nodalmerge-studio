# Changelog

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
