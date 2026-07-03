# Changelog

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
