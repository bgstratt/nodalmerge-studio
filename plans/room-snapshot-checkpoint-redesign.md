# Room snapshot → integration-checkpoint redesign

**Status:** proposed (2026-07-18)
**Owner concern:** the replicated-room DB grows without bound — a 5 KB repo + one goal
produced a **205 MB** `nodalmerge-nodes.db`. This is a durability-model bug, not a
fundamental limit of peer-replicated studio.

## Diagnosis (measured, not theorized)

`nodalmerge-nodes.db` for the `nm-convergence-test` workspace = **205 MB**, entirely in
`accepted_nodes` (247 rows); `compaction_snapshots` is **empty** — compaction/pruning has
never run.

- Room `repo/repo-9f74…`: **191 pack records, 197.7 MB**. Each pack is a **full
  re-serialization of the whole room DAG**, grown **0 KB → 5 MB** over ~2.5 h. All retained
  (`applied=0, tombstone=0`). Sum of a 0→5 MB series over 191 records ≈ 197 MB (the O(n²) shape).
- One 5 MB snapshot decodes to **8,131 CRDT operation-nodes** for ~8 goals:
  artifact-ref 1.5 MB (2,539 — diff/message **bodies stored inline**), work-unit 993 KB (782),
  repository-op 941 KB (2,056), merge-proposal 624 KB, repository-snapshot 379 KB, branch 238 KB,
  goal 215 KB (334 *versions* of ~8 goals), task 187 KB.
- Actual work product (blobs): **11 KB**.

### Why it accumulates — three un-coordinated full-room snapshot triggers
Every one calls `PersistRoomSnapshotAsync` (full room), not a delta:

| # | Site | Fires on | Debounced? |
|---|---|---|---|
| 1 | `NodalMergeStudioNodeStore` → `EngineRoomMap.PersistAndReplicateAsync:265` | **every studio write** | ❌ no — the storm |
| 2 | `RuntimeWebSocketLoopRunner:315` (`PersistRoomSnapshotDebouncedAsync`) | every inbound `pack` | ✅ 0.2.4 |
| 3 | `RuntimeWebSocketLoopRunner:324` (`ShouldPersistSnapshotForMutation`) | every generic CRDT mutation (map-set/list-*/text-*/blob-set) | ❌ no |

The runtime/transport layer sees only generic CRDT ops — it cannot know a "goal completed"
or "merged to main." So the checkpoint decision must live in the **studio domain layer**.
And `compaction` never fires because `RuntimeDagCompactionOptions.Default` ships
`EnablePruning: false` + a **7-day** eligibility window.

## Target model

A full-room snapshot is a **"last known good" checkpoint**, taken only at real integration
points — **goal completion** and **merge to main / repo workspace** (which should line up with
git commits / PRs). Everything between is carried as **incremental deltas**. Hydration =
latest checkpoint + deltas since. Multi-part runs, failed runs, and intermediate work never
warrant a whole-room snapshot.

- **Per write:** persist only the **delta** (the promoted node), for durability. No full snapshot.
- **On checkpoint (goal-complete / merge-to-main):** persist one full-room snapshot.
- **Hydrate:** load latest snapshot, replay deltas after it.
- **Prune:** collapse superseded snapshots + deltas to the latest checkpoint.

## Implementation (incremental, each step testable)

Durability invariant to preserve at every step: **no local write is lost across a restart** —
either its delta or a covering checkpoint must be on disk.

- **A — studio per-write becomes delta, not snapshot** (`nodalmerge-studio`)
  `EngineRoomMap.PersistAndReplicateAsync` currently persists the full room on every write.
  Replace with persisting just the promoted node's pack (a delta — reuse the single-node
  export the outbound path already builds, `MstDone{ids:[promotedNodeIdHex]}`). Removes trigger #1.
  Blocker check: confirm a single-node pack export is reachable from the studio store; add one if not.

- **B — runtime stops auto-snapshotting** (`nodalmerge`)
  Remove trigger #2 (line 315; the inbound delta is already persisted at line 272) and
  trigger #3 (line 324). Guard: non-`pack` mutation types (#3) currently have *no* delta
  persistence — either add delta persistence for them or confirm studio never emits them on
  the wire (studio writes go out as packs). Keep the shutdown/disconnect `FlushRoomSnapshotAsync`
  as a safety-net checkpoint.

- **C — domain checkpoint trigger** (`nodalmerge-studio`)
  On merge-applied-to-main (`InMemoryMergeService.ApplyAsync`, `promotedToDisk`) and on goal
  completion, call `PersistRoomSnapshotAsync(repoRoomId)` once. This is the only place a
  full-room snapshot is minted. Best-effort / off the write's critical path.

- **D — enable pruning** (`nodalmerge`)
  `RuntimeDagCompactionOptions.Default`: `EnablePruning: true`, retention shortened from 7 days
  (checkpoints supersede fast; keep latest + a small recent window). Collapses existing bloat and
  keeps checkpoint count bounded.

## Follow-up (separate pass, after the above lands)

**DAG replication content review** — even one checkpoint is 5 MB of metadata for 11 KB of work.
Decide what actually needs to replicate to peers vs. ride CAS / stay peer-local:
- artifact-ref **bodies** (1.5 MB inline diffs) → CAS refs (the unfinished half of chat-as-CAS).
- Whether repository-op / intermediate work-unit-version history needs to cross at all.
- Whether CRDT op-history should periodically collapse (checkpoint-and-drop) rather than grow forever.

## Verification

- Repro workload (one goal, a few materializations) must land the room DB in **single-digit MB**,
  not hundreds.
- Hydration correctness: kill + restart mid-session, confirm no lost writes and bounded replay.
- Existing `RuntimeDagPersistenceServiceTests` stay green; add tests for delta-only-per-write and
  checkpoint-on-merge.
