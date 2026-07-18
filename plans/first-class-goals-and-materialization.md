# First-class replicating goals → supporting work units → materializable state

**Origin:** two-machine test, 2026-07-18. Goals don't replicate/show across peers (only
`work-{guid}` work units cross); goals are reconstructed per-peer from work units, not shared as
first-class entities. Follow-up to [[room-persistence-bloat]] (Layer 2 reasoning replication) and
[[repo-identity-convergence]] (repo-scoped replication). Sibling memory:
`nodalmerge_repo_identity_convergence_gap`.

## Target end-state (user's model)

In Pathways, on **any** peer sharing the repo:

```
Goal  (goal text, status)            ← first-class, repo-scoped, replicates
 ├─ work unit A                      ← supporting work, each a node
 │    · CAS-backed agent conversation (lazy)   ← Layer 2 ConversationRef (done)
 │    · diffs (lazy)                            ← MergeProposal / file-changes (done)
 │    · ⤓ materialize this state → temp folder  ← NEW (per-WU, throwaway dir)
 ├─ work unit B
 └─ …
```

- Goal node carries **just the goal text + status** (lightweight; the "why").
- Each work unit lazy-loads its reasoning + diffs from the CAS content plane (small ref
  replicates; bytes pulled on demand — already shipped in Layer 2).
- Any work unit can **materialize the tree it produced** into a fresh throwaway folder
  (never the live checkout) — user chose **per-work-unit** granularity + **new temp/worktree**
  target.

## Current reality (code-verified, 2026-07-18)

- **Goal write:** `GoalNodeService.RecordAsync` routes `GoalV1` by `goal.RepositoryId` (repo room
  when set → replicates). Correct. But the extension never creates a `GoalNode` — every run path
  posts `POST /studio/workunits` (root work unit), no goal. `GoalNode`s only come from
  `/studio/goals`, the goal MCP tools, or `GoalControlService.GetOrSynthesizeAsync` (pause).
- **Goal display bug:** `GET /studio/goals` is either/or — if ANY stored `GoalNode` exists it
  returns ONLY stored goals (`source:"goal-store"`, StudioRestEndpoints.cs:4114) and drops every
  work-unit-derived goal, incl. replicated peers'. Else it synthesizes from work units
  (`source:"work-units"`, :4170). A local stored goal masks all peers' work-unit goals.
- **Pathways projection:** `ProjectionManager.BuildWorkspacePathwaysAsync` (:1107) reads domain
  services (work units, merges, event stream, artifacts) — NOT node kinds. Emits `GoalStarted`
  per **root** work unit (:1125-1140); child work units surface only through their proposal
  lifecycle nodes, edge-anchored up `ParentWorkUnitId` (`ResolveAnchorNodeId` :1507-1541). No
  standalone work-unit node; no goal-grouping container in the client (lanes keyed by branchId).
  Endpoint `GET /studio/projections/WorkspacePathways`.
- **Materialization:** `MaterializationEngine.MaterializeAsync` (content-addressed checkout) +
  `POST /studio/repository-snapshots/{snapshotId}/materialize?targetPath=` (point-in-time, target
  required) are fully wired. `SnapshotTreeResolver` resolves inline/cas-tree manifests.
  `WorkUnit.SeedSnapshotId` = the base state a WU started from (materializable today). **Gap:** no
  snapshot is minted for the state a WU **produced** — `RepositorySnapshot.CreateAsync` accepts
  `workUnitId` but no caller passes it; `source:"WorkUnitCompletion"` is unbuilt. `WorkUnit`'s
  produced content lives only as its live branch dir + emitted `RepositoryOperation`s.
- Client already has a `materializeNode` path (DagReplayPanel.ts:349) using `SnapshotId` → the
  materialize endpoint, else `/studio/projections/{workUnitId}/materialize` (live-branch copy).

## Phases

### Phase 1 — Goals replicate as first-class entities (write + display) — the unblock
- **Server-side auto-goal:** in `POST /studio/workunits` (StudioRestEndpoints.cs:1496), when the
  created work unit is a **root** (`ParentWorkUnitId is null`), persist a repo-scoped `GoalNode`
  (goal text, `Status = Exploring`, `GoalId == WorkUnitId`, `RepositoryId = wu.RepositoryId`).
  Idempotent by `GoalId`. Covers extension + MCP + eval-harness paths, no client rebuild for the
  write side. Inject `IGoalNodeService`.
- **Union display fix:** `GET /studio/goals` returns stored goals ∪ synthesized-from-work-units
  (a synthesized goal for any root work unit lacking a stored `GoalNode`), deduped by
  `workUnitId`. So a replicated peer work unit always surfaces as a goal regardless of local
  stored goals. Read-side only.
- **Tests:** root-WU create persists a repo-scoped goal; child-WU create does not; goals-list
  union (stored + synthesized, no dupes); RoomReplicationTests goal-across-peers.
- **Outcome:** peers see each other's goals in the goal list (and, since the goal is the root
  work unit, in pathways' `GoalStarted`).

### Phase 2 — Pathways: goal → supporting work units visible
- Emit a node per **supporting work unit** under its goal (not just proposal nodes), so the work
  breakdown is visible even before/without a proposal. New node kind `WorkUnit` (or
  `SupportingWork`) in `WorkspacePathwaysNode` (ProjectionContracts.cs:487), emitted in
  `BuildWorkspacePathwaysAsync`, edge parent→child by `ParentWorkUnitId`. Goal node uses the
  replicated `GoalNode`'s status when present (Exploring/Paused/Converged/Abandoned).
- Client: add kind to `PATHWAY_KIND_STYLES` (dagRenderer.ts:69) + legend (DagReplayPanel.ts:693);
  decide grouping (nest under goal lane vs. own lane); add to `INSPECTABLE_KINDS` (main.ts:677).
- **Design checkpoint with user** before building — this shifts pathways from pure event-history
  to a work-breakdown view; confirm the grouping UX.

### Phase 3 — Per-work-unit reasoning + diffs in the drawer (mostly shipped)
- Layer 2 already: `/studio/workunits/{id}/conversation-log` (local-first, else peer-published
  transcript) + `/studio/merges/{id}/file-changes`. Ensure the new work-unit nodes are
  inspectable and fetch conversation-log (works for any workUnitId) + diffs (via the WU's
  proposal if any). Mostly wiring the new node kind into the existing `inspectNode` path.

### Phase 4 — Materialize at a state → temp folder (three-level model)
Materialization is structured into **three checkout points**, mapping onto the pathways hierarchy
(user-refined 2026-07-18):

```
Goal   · base  = pre-work state    → root WorkUnit.SeedSnapshotId   (exists today)
       · final = integrated result → snapshot minted at merge-apply, stamped on GoalNode  [NEW]
 ├─ work unit A · produced = what A built → WorkUnitCompletion snapshot per WU  [NEW]
 └─ …
```
The `MergeProposal` remains the **change record** (its diff + file-changes drawer); the goal's
**final** state is the *resulting tree* persisted as a goal-level snapshot, minted when the winning
proposal is applied — so a checkout is a single materialize call, never "apply this diff onto some
base."

- **Produced-state snapshot (per work unit):** on work-unit completion/proposal, fold the branch
  dir (or its emitted `RepositoryOperation`s onto `SeedSnapshotId`) into a `RepositorySnapshot` via
  `snapshotService.CreateAsync(..., workUnitId: wu.Id, source:"WorkUnitCompletion")`. Adapt
  `RepositoryImportService.SyncFromFilesystemAsync`'s diff-and-snapshot logic, scoped/attributed
  per WU.
- **Final-state snapshot (per goal):** on merge-apply / goal convergence, mint a
  `RepositorySnapshot(source:"GoalConverged")` of the integrated repo state and stamp its id on the
  `GoalNode` (new `FinalSnapshotId` field). Hook the `MergeApplied` event seam.
- **Base-state (per goal):** already the root work unit's `SeedSnapshotId` — expose it on the goal
  (new `BaseSnapshotId` denormalized from the root WU, or resolved on read).
- **Resolver + node wiring:** resolve a goal/work-unit id → the right `SnapshotId` (goal→base|final,
  WU→produced; lookup by `RepositorySnapshot.WorkUnitId` + `Source`); stamp onto the pathways
  node's `SnapshotId` so the existing `materializeNode` → `POST /studio/repository-snapshots/
  {snapshotId}/materialize` path works. Fallback to `SeedSnapshotId` when no produced/final yet.
- **Target:** client picks a fresh temp/worktree dir (scratch path), never the live checkout.
- **Cross-peer caveat:** materializing a peer's snapshot pulls its blobs via
  `blobStore.TryGetBlobAsync` — needs the `ChainedRemote` blob origin configured (same deploy
  caveat as Layer 2 reasoning pull). Document.
- **Tests:** WU completion mints a WU-attributed snapshot; merge-apply mints + stamps the goal's
  final snapshot; resolver returns base/produced/final correctly; materialize round-trips a tree to
  a temp dir; retention-aged-out → 410.

## Notes
- All studio-only; no nuget/host rebuild (materialization engine, tree resolver, blob store all
  already in the shipped 0.2.4 host surface via `IBlobStoreProvider`/`BlobHasher`). Rides the VSIX
  rebuild.
- Phase 1 is the immediate value + fixes the replication bug; ship it first. Phases 2/4 are
  additive UX/capability; Phase 3 is largely verification.
