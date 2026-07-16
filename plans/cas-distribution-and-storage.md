# CAS distribution & storage — remote blob store, tree sharing, retention

## Status

- [x] Phase 1 — Tree structural sharing (metadata fix; local-only, no server dependency)
      — **shipped 2026-07-15** (branch `cas-distribution-storage`): 1.1 tree-as-blob +
      `ISnapshotTreeResolver` (format frozen in `docs/TREE_OBJECT_FORMAT.md` + vectors),
      1.2 v2 per-directory sharing (1-leaf change → ≤ depth+1 new tree blobs, verified),
      1.3 `GetReachableHashesAsync` fail-closed reachability (the Phase-5 seam).
- [x] Phase 2 — Remote blob wiring — **shipped 2026-07-15** (nodalmerge branch
      `blobExpansion` + studio): 2.1 `GET/HEAD/PUT /blobs/{hash}` on **both** hosts in
      parity (contract frozen in nodalmerge `docs/BLOB_HTTP_SURFACE.md` + vectors; the
      Rust server previously had NO HTTP blob surface — WS-only — so 2.1 built it),
      2.2 `ChainedBlobStoreProvider` (`BlobStorage=ChainedRemote`, BLAKE3
      verify-on-fetch in the chain only, extension `nodalmerge.blobOrigin.*` settings),
      2.3 reconcile sweep (`POST /studio/cas/reconcile`, startup one-shot,
      `nodalmerge.reconcileBlobOrigin`), 2.4 scope-pruned resolve + prefetch
      (`POST /studio/repositories/{id}/prefetch`).
- [x] Phase 3 — zstd compression at rest — **shipped 2026-07-15**: layout contract v3
      (`BLOB_STORAGE_LAYOUT.md` §8, `blake3/<hex>.zst`, hash always of uncompressed
      bytes) on both runtimes; server default ON (level 3), local default OFF;
      `Accept-Encoding: zstd` GET negotiation live-verified (12 KB payload → 47-byte
      frame on the wire). Note: Rust rejects PUT-with-`Content-Encoding` as 415, .NET
      falls through to 422 hash-mismatch — both contract-conformant ("MAY").
- [x] Phase 4 — Delegated S3 / S3-compatible backends with presigned URLs
      — **shipped 2026-07-15** (nodalmerge `blobExpansion`): 4.1 URL-resolution +
      upload-confirm contract frozen in `BLOB_HTTP_SURFACE.md` + .NET endpoints +
      vectors (`99c57aa6`; also fixed `blob_gc_sweep` deleting foreign/`.zst` bucket
      objects), 4.2 `nodalmerge-server-s3` composition binary (Cargo-cycle-imposed,
      `dev-server` precedent) + both endpoints + MinIO e2e (`cfd864db`), 4.3
      `S3DirectBlobStoreProvider` + `RemoteBlobLinkAggregator` (3 links, single BLAKE3
      verify gate) + client-side zstd via `Content-Encoding` object metadata — the
      `.zst`-key half of layout §8 remains an unbuilt seam, clarified additively in
      that doc (`537face8`). Extension-settings mapping onto
      `NodalMerge:Storage:S3Direct:*` is a pending studio-side follow-up.
- [x] Phase 5 — GC & retention (the answer to snapshot flux)
      — **5.1 + 5.2 shipped 2026-07-15** (studio `cas-distribution-storage`): 5.1
      `ISnapshotRetentionPolicy` Pinned/Active/Intermediate classification
      (`dc617ac`; see 5.1 findings note), 5.2 retention-aware
      `GetLiveBlobHashesAsync` + staged `BlobGc:Mode`
      (DryRun-default/MarkOnly/SweepSoft/SweepHard), run ledger
      (`studio/gc-run/v1` + `GET /studio/cache/gc/runs`), background interval
      runner, aged-out materialization → 410 Gone (`e22e2f1`; bare
      `POST /studio/cache/gc` no longer live-deletes — configured mode, DryRun by
      default). **5.3 shipped 2026-07-15** (nodalmerge `c47e6c68`): studio-domain
      `LiveHashSource` parsing frozen envelopes from `repo/*` rooms (real payloads
      are PascalCase with integer enum ordinals — the schema doc's illustrative
      payload examples diverge; envelope shape unaffected), retention semantics
      mirrored from 5.1 but strictly fail-closed (this component gates deletion),
      conservative forever-retention for legacy `"studio"`/`"workgroup"` rooms,
      `SqliteGcStore` inventory/run ledger, local-disk + S3 `BlobObjectStore`,
      `GcMode off|legacy|dryrun|markonly|sweepsoft|sweephard` (default `legacy` =
      byte-for-byte today). Follow-ups recorded: WS-upload-path inventory wiring
      (HTTP path is wired; mark pass covers the gap meanwhile), never-referenced-
      blob discovery (no ListBucket by contract — needs a drift job), schema-vector
      payload examples → real-shape fixtures + parity-CI migration, op-compaction
      over-retention refinement, Mongo/Postgres inventory adapters. Phase 5
      complete.
- [x] Phase 6 — Multi-user: replication data plane, room topology & repository identity
      — **sliced 2026-07-15**; decision recorded: Studio state rides the engine DAG.
      **6.0 + 6.1a + 6.1b shipped 2026-07-15** (studio `cas-distribution-storage`):
      6.0 `docs/STUDIO_ROOM_SCHEMA.md` freeze + vectors (`52ef883`), 6.1a engine-backed
      `NodalMergeStudioNodeStore` + legacy migration (`de64155`; discovered the
      live-maps↔sync-graph split — see Phase 6 note), 6.1b bidirectional
      `RoomPeerClient` + retirement of the 30 s promoter tick (`a6085fc`) — two-host
      replication integration test green. **6.2 shipped 2026-07-15** (`e765243` +
      `0c703e4`): `RepositoryIdentityHints` (LibGit2Sharp, frozen normalization),
      `IWorkgroupRepositoryDirectory` over a generalized `EngineRoomMap`, preferred-id
      continuity (no dual identities for existing workspaces), REST disambiguation
      surface; user-approved pre-replication amendment fixed the normalization
      step order (slash-strip before `.git`-strip). NOTE for 6.3/6.4:
      `RoomPeerClient` is still single-room — workgroup-room writes are durable
      locally but not yet pushed upstream; multi-room membership is the gap 6.3
      closes. **6.3 shipped 2026-07-15** (`4259cc6`): kind→room routing for the 5
      direct-`RepositoryId` kinds + marker-guarded lazy-per-room migration,
      multi-room `RoomPeerClient` (one socket per room — ws_handler binds room from
      the URL path once, verified no multiplexing), workgroup replication live
      end-to-end, minimal workgroup goal nodes (`"goals"` namespace), pinned-ref
      resolver + `POST /studio/references/resolve` (no-local-clone verified). 942
      tests green. **New gate discovered: ~25 repo-scoped-indirect kinds
      (`MergeProposalV1`, `TaskV1`, `BranchV1`, …) carry no `RepositoryId` — they
      remain peer-local until a denormalize-and-route slice (6.3a below) lands;
      5.3's server-side retention classification cannot see proposals until then.**
      **6.4 shipped 2026-07-15** (`eb69639`): `Room:HostUri`/`Room:Workgroup` config
      (+ `Peer:HostUri` deprecated fallback), extension `nodalmerge.room.*` +
      `nodalmerge.blobOrigin.s3Direct.enabled` settings bridged, `isLocalUri`
      adopt-guessing and remote-mode branch deleted from `HostManager` (single-
      instance local adoption kept — it's process management, not URL guessing).
      947 tests green. Note: `Peer:RoomId` (`"studio"`, the peer's own room) stays
      hardcoded — out of D4's scope; flag at 6.5 if it needs configuring.
      **6.3a shipped 2026-07-15** (`9c7c60c`): `RepositoryId` denormalized at
      creation (single-hop inheritance — every stored work unit is already
      resolved, so children never walk further than their parent) onto
      `MergeProposalV1`/`TaskV1`/`BranchV1`/`KnownGoodStateV1`/`DecisionV1`/
      `ArtifactRefV1`/`CandidateConflictV1`/`TaskConflictV1`, all routed to repo
      rooms; second marker generation `repo-migrated-v2/{roomId}` with FK-chain
      resolution for legacy rows. Execution-event kinds deliberately stay local
      (highest-volume, no 5.3 need — replication-plane-compaction territory).
      Proposal-on-A-visible-on-B verified. **5.3 is unblocked**: everything
      retention classification needs is room-resident.
      **6.5 shipped 2026-07-15** (`8bb732d`) — **the plan is complete.** Inbound
      cache refresh (`IStudioCacheRefreshCoordinator` + per-service `RefreshAsync`;
      inbound-wins-for-unmodified rule, engine LWW is the only conflict
      resolution), seed-snapshot pinning (`WorkUnit.SeedSnapshotId`, retention
      Active class now exact for new rows, proxy kept for legacy), two-peer
      integration test, and an **executed real-stack smoke** (Rust server + two
      `dotnet run` hosts, recipe in `docs/guides/multi-user-smoke.md`):
      bidirectional goal visibility, cold-peer materialize, propose/merge — all
      observed working over the real wire.

## Next revision — consolidated follow-up ledger (2026-07-15 close-out)

Ranked; the first two are the gaps the 6.5 smoke identified as separating this
from production multi-user.

1. **Server-role replication bridging** (nodalmerge `RuntimeWebSocketLoopRunner`):
   a host that is also the room-server never bridges inbound peer-authored packs
   into its own Studio replication sink/cache refresh. Two-process topology works
   (proven); the embedded-server topology has the gap.
2. **Portable repository identity for snapshots/workspaces**:
   `RepositorySnapshot.RepositoryId` + `SeedRepositoryPath` are keyed by physical
   path — cross-machine peers currently must fake matching paths. The registry
   already has `WorkgroupRepoId`; snapshots/workspaces need to move onto it.
3. **Peer-private room isn't private**: every peer joins the same server-side
   `"studio"` room (`Peer:RoomId` hardcoded); 6.5 had to no-op
   `RepositoryRegistryService.RefreshAsync` because live refresh absorbed other
   peers' registrations. Per-peer room ids (or splitting peer-private kinds out)
   is the clean fix.
4. Engine-side `ImportPack` → live-maps hydration (retire the replay-after-import
   pattern; engine release + repack).
5. 5.3 ledger: WS-upload-path inventory wiring; never-referenced-blob drift job;
   schema-vector payload examples → real-shape fixtures + parity-CI migration;
   op-compaction over-retention; Mongo/Postgres inventory adapters.
6. Reconcile-sweep multi-target push (relay-only today) + presign-time `.zst` key
   selection (Phase 4 leftovers).
7. Extension UI for identity disambiguation (REST surface exists);
   `ReadFileAsync` callers → pinned refs (needs a generation pin on
   `FileReferenceV1`); execution-event kinds replication-plane compaction;
   in-memory cache refresh for kinds beyond the rehydratables.

Baseline measurement (`tools/measure-cas-baseline.ps1`, run 2026-07-15): no heavy-use
workspace exists yet — real repos so far are small, so the projected ~400 KB/generation
inline-map pressure never materialized locally. Active workspace (post-Phase-1 build):
64 blobs / 49,543 B; 11 snapshot nodes / 23,963 B payload (avg 2,178 B — 4 newest are
`TreeFormat:"cas-tree"` with `TreeEntries:null`, confirming the new write path live in
the extension). Largest legacy data root (2026-07-13): 5,727 studio rows / 10.7 MiB
payload, of which snapshot nodes were only 2 rows / 5,594 B — the bulk of node-store
growth in practice is *other* node types (tasks/messages/history), which is Phase-5
retention territory, not tree-map territory. Re-run against a genuinely large repo
before judging Phase 1/3 wins quantitatively.
Local packages bumped to **0.2.1** (includes the new host surface + real win-x64/
linux-x64 native runtimes); stale `nodalmerge-host/` fallback paths in studio fixed
to `hosts/dotnet/`.

**Sequencing: this plan follows `harness-hosting-architecture.md`.** Nothing here blocks
Phases A–E there; conversely, nothing there blocks Phase 1 here (which is local-only and
could ship early if snapshot-map growth becomes noticeable first). This plan implements
"Enumerated gap #1 — CAS blob distribution" from the harness plan's multi-developer
future state, plus the storage decisions resolved 2026-07-12.

## Architectural model (recorded 2026-07-14)

Principles the rest of this plan is a consequence of. Most of these describe behavior
the code already has — they're recorded so the design stops being implicit.

1. **The DAG is the authoritative history of observed repository states within a
   NodalMerge workspace.** Git remains the authoritative persistence mechanism for
   repositories *outside* NodalMerge; within it, git is one producer and one consumer
   of observed states. The DAG explains *why* the repository evolved; git serializes
   states for exchange with the outside world.
2. **NodalMerge never reinterprets repository history — it only records newly observed
   states.** A generation produced by `git pull`, cherry-pick, manual edits, rsync, an
   IDE rename, or a harness run is the same thing: an observed state, one new node.
   The "user started a goal from a stale branch" case is not an error — it's a branch
   in the DAG, descended from the older recorded generation, reconciled through the
   normal proposal/reconciliation path. (This is already how `ForceSyncAsync` behaves;
   it's doctrine now, not accident.)
3. **`RepositorySnapshotId` and `TreeHash` are the authoritative identities of a
   state.** Git SHAs are useful hints but can vanish (rebase, force-push, squash);
   snapshot IDs and content hashes never have to. Work units carry snapshot identity,
   not git ancestry.
4. **Two planes, joined by hashes — and writing vs. fan-out is not a real
   distinction.** The *replication plane* carries rooms (maps, lists, references) as
   CRDT packs over WebSocket; a write is ops appended to the local replica plus pack
   exchange — there is no separate "push API" vs "subscribe channel", and the server
   is just a peer with more uptime that also persists. The *content plane* carries
   immutable CAS blobs over HTTP/presigned URLs — big, self-verifying by BLAKE3,
   pull-on-demand, no ordering or consensus. Rooms hold references; the CAS holds
   bytes. (Same split SpeechSlate runs in production: board rooms hold button/image
   references, delegated storage holds the assets.)

```
Git (outside world)
   ↕  import / export — observations in, materializations out
Repository DAG — rooms                 why states evolved: snapshots, work units,
   │                                   proposals, decisions   [replication plane: ws packs]
Repository trees — CAS tree objects    what each state was: root TreeHash → dirs → file hashes
   │
CAS blobs                              immutable content      [content plane: HTTP / presigned]
```

## Decision record (2026-07-12): full blobs, not deltas

The question that prompted this plan: should the CAS store checkpoints + operation/delta
chains per file instead of a full blob per file version, to curb growth from
snapshotting every change?

**Decision: no. Content-addressed full blobs stay the storage model for repo file
content.** Rationale, recorded so future-us doesn't re-litigate:

1. **The duplication worry was miscalibrated.** The CAS already dedups perfectly by
   content: `RepositoryImportService` skips any file whose BLAKE3 matches the stored map
   (sync only PUTs *changed* content), and identical content is stored once ever, across
   all generations and branches. A snapshot generation is a path→hash map, not a copy.
   The true cost model is one full blob per *distinct version of a changed file* — git's
   loose-object model.
2. **The math says it's cheap.** Source files average ~10 KB; a work unit touches tens
   to low hundreds of files. 1,000 work units × 200 changed files × 10 KB ≈ 2 GB before
   compression; zstd gets 3–4× on code; object storage runs ~$0.015/GB-mo. Years of
   heavy use is single-digit dollars per month.
3. **Content addressing is the property that makes distribution easy.** Every blob is
   immutable and independently verifiable by hash — distribution is a pure caching
   problem (no ordering, no conflicts, no consensus). A delta chain trades that for
   chain-walking, where one broken link corrupts everything downstream.
4. **Op-replay for file content contradicts the harness-hosting posture.** External
   harnesses write opaque bytes; Studio observes results at harvest ("constrain the
   edges, never the middle"). Operations derived from harvest diffs are not more
   truthful than the blob — just a different encoding with worse failure modes. The
   WAL/checkpoint analogy holds only for systems that mediate every write; Studio
   deliberately doesn't. And character-level merge is already ruled semantically
   unsound for code (harness plan, leases-are-advisory decision).
5. **Encoding is invisible under the hash anyway.** Whether blob `X` is physically a
   literal or `(base + delta)` is a blob-store-internal decision under
   `TryGetBlobAsync(hash)` — exactly git packfiles vs loose objects, same IDs. So delta
   or chunked encoding can be added later, inside the store, with zero changes to the
   DAG, snapshots, ops, contracts, or replication. It is not a viability gate for
   anything, which is why it's deferred until measurement demands it.

**Checkpoint+replay stays where it is native**: CRDT-resident content in core
(`text.rs`/RGA, `compaction.rs`, `replay.rs`) and future collaborative documents. The
split already exists — *ops for what the engine mediates, blobs for what it observes.*

**What actually answers "constant flux of intermediate snapshots" is retention, not
representation** — Phase 5. Promoted/canonical generations are pinned truth;
intermediate work-unit generations are cache, reclaimable once their branches retire.

### Related stance: lazy sync-on-initiation is accepted (2026-07-12)

Studio discovers external/user disk changes only when something initiates a sync
(`EnsureBootstrappedAsync` at goal start, `ForceSyncAsync` after merge/refresh). Yes,
that means the CAS view of an externally-edited repo can be stale between initiations.
Accepted, because the failure mode is benign by construction:

- Every branch is seeded from a **recorded** snapshot generation, so the 3-way merge
  base is always materializable — divergence discovered late still reconciles through
  the normal proposal/reconciliation path (`MergeReconciliationService`).
- Because snapshots are content-addressed maps, "catching up" is trivial: whatever is
  on disk at sync time simply becomes the next generation (Case 2 diff walk). No
  replay-consistency question exists at this layer.
- A repo-level `FileSystemWatcher` (continuous discovery) is an opportunistic later
  add-on, same family as harness-plan Phase E's branch-workdir watcher. Not scheduled.

## Decision record (2026-07-14): room topology, repository identity, process modes

Resolves the room-granularity and repo-identity questions Phase 6 previously deferred.

### D1 — Room-per-repository, under a workgroup directory room

```
workgroup room   (small, cheap, always joined)
├── repositories map: repoId → { label, repoRoomId, matching hints }
├── membership / presence
└── cross-repo references (goals spanning repos, pinned reference files)

repo room        (one per repository — joined only when a peer touches that repo)
├── snapshot/generation DAG (nodes carry TreeHash, never inline trees — Phase 1)
├── work units, branches, proposals, decisions, conflicts
└── artifacts (as CAS references)
```

A room is simultaneously the **replication boundary** (peers sync only repos they
materialize), the **auth boundary** (the existing per-room token mint/validate with
capabilities gives repo-level access control — the future SaaS story is JWT into a
workgroup, capabilities per repo room), and the **GC-reachability boundary** (Phase 5's
`LiveHashSource` walks retained snapshot roots per repo room). SpeechSlate's
user-room → board-room pattern, applied to workgroup → repo.

Repo-scoped state lives in the repo room, full stop; the workgroup room holds only the
directory and cross-repo *references*. The hardcoded `"studio"` room constant
(`RuntimeGraphPromoter`) becomes room-per-repo keyed by `repoId`.

### D2 — RepositoryId is minted at registration; git supplies matching hints, never identity

Every derivation candidate fails somewhere: remote URLs rename and multiply; root-commit
SHAs fail on shallow clones and empty repos, and *collide on forks* — exactly the repos
that must not be silently unified; content hashes differ per working state; a committed
marker file writes into the user's repo, which we don't do. So: **the workgroup room's
repositories map is the authority; `repoId` is minted once at first registration.**
Consistent with the architectural model — the DAG never derives truth from git, it
observes git.

Matching flow when a peer comes online with a local folder:

1. Compute hints: the **set** of root-commit SHAs (`git rev-list --max-parents=0 HEAD`
   — a set, because merged unrelated histories yield several) + normalized remote URLs
   (strip scheme/credentials/`.git`, lowercase host).
2. Look up the workgroup repositories map.
3. Exact root-SHA match → join that repo room. Fork ambiguity (shared root SHAs) →
   remote-URL tiebreak, else one-time user prompt.
4. No match → register: mint `repoId`, create the repo room, record hints.
5. Cache the binding in the peer's local workspace storage — hints are consulted only
   at first contact, so later rebases or remote renames never re-identify a repo.
6. Degraded cases (shallow clone, no remote, empty repo) → one-time user prompt
   ("which registered repo is this, or register new") — honest, because no algorithm
   can know.

Today's `RepositoryRegistryService` minting `repo-{guid}` keyed by local path becomes
"local candidate pending workgroup registration", not the end of the story.

### D3 — Work units are single-repository; multi-repo goals fan out

There is no cross-repo atomic merge anywhere (git doesn't have one either), so the
design doesn't pretend. A goal that touches N repositories is a **workgroup-level goal
node referencing N work units, one per repo room**, each with its own
branch/proposal/merge lifecycle; coordination ("all N landed", ordering) is goal-level
state in the workgroup room. Promotion stays per-repo-DAG.

Cross-repo *read* references become pinned triples `(repoId, generationId, path)` —
resolved via that repo's room (generation → tree root) + CAS fetch. Strictly better
than today's `ReadFileAsync` live-disk read: reproducible, works on peers that never
cloned the referenced repo, prefetchable with the work unit's scope. A multi-root
VSCode workspace maps naturally: each root folder matches/registers to its own
`repoId` in the same workgroup.

### D4 — One runtime, always the local peer; connection is config, not a mode

The extension **always spawns the same local peer runtime**, which always owns local
persistence (workspace storage: DB, CAS cache, materialization dirs) and always serves
localhost HTTP/MCP for the UI. The extension never talks to a remote server directly —
one wire contract, local-first preserved, offline works. Standalone vs connected is
solely the presence of explicit `Room` config — never inferred from URL shape:

```jsonc
"nodalmerge.room.hostUri":   "wss://team.example.com" | "ws://127.0.0.1:9090" | ""  // "" = standalone
"nodalmerge.room.workgroup": "acme-platform"
```

Hosting the server on your own machine is not a special mode — it's
`hostUri: ws://127.0.0.1:9090` with the server binary run separately. The server is
never spawned by the extension. This kills `HostManager`'s `isLocalUri` adopt/spawn
guessing; the user's repo folder stays an external construct that is observed
(sync-in) and materialized (checkout-out), never used as the store.

### D5 — Server blob origin: no new storage crate, no "S3-compatible server" build

The Rust server's existing blob request/upload/fetch flow + `blob_gc` + the
parity-refactored global CAS layout **is** the v1 server-relay origin (Phase 2), backed
by local disk in the shared layout. True S3/MinIO/R2 arrives via the existing presigned
delegate seam (`IBlobUrlResolverProvider`, `nodalmerge-s3-blobs`) as Phase 4. Clients
speak `TryGet/Put(hash)` to a chained provider; which link serves the bytes is config.
One global CAS — content-addressed dedup across repos is automatic and safe; per-repo
*access* control is a token/capability concern at the endpoint, and GC liveness is
per-room roots per `delegated-storage-gc.md`.

## Where the seams already are (verified 2026-07-12)

| Piece | Location | State |
|---|---|---|
| Provider interface | `NodalMerge.Host.Abstractions.Providers.IBlobStoreProvider` (`TryGetBlobAsync`/`PutBlobAsync` by hex hash) | Exists; Studio consumes it in `MaterializationEngine`, `RepositoryImportService`, `GitAdapter`, `FileSystemWorkspaceService`, `ConflictResolutionService`, `RepositoryBlobTools`, `McpToolDispatcher` |
| Presigned-URL seam | `IBlobUrlResolverProvider` (`ResolvePutUrlAsync`/`ResolveGetUrlAsync`) | Exists (delegated-storage pattern, SpeechSlate precedent) |
| Local store | `FileBlobStoreProvider` (registered in `NodalMerge.Host.Composition`); `WsOnlyBlobStoreProvider` for ws-relay hosts | Exists |
| S3 backend | `nodalmerge-s3-blobs` crate (Rust); BlobStorageParity refactor aligned layouts (`docs/BLOB_STORAGE_LAYOUT.md`, layout-vector interop tests) | Exists |
| Server blob flow | Rust server has blob request/upload/fetch flow (`server/tests/fixtures/blob_flow_request_upload_and_fetch.json`, `blob_layout_interop.rs`, `blob_gc.rs`) | Exists — exact HTTP surface to be confirmed in Phase 2 slice work |
| Scoped materialization | `MaterializationEngine.MaterializeAsync` — `FileScope` filter + skip-if-disk-matches-hash | Exists — the local-store-as-cache behavior is already written |
| Generation delta materialize | `MaterializationEngine.RematerializeAsync` — map-diff between generations | Exists |
| GC contracts | `nodalmerge/docs/delegated-storage-gc.md` — `LiveHashSource`, `AssetInventoryStore`, `GcRunStore`, `AdminPinStore`, `BlobObjectStore`, mark/soft/hard sweep, grace windows | Spec frozen; Rust `blob_gc` tests exist; Studio-side reachability source missing |
| Peer machinery | Embedded Studio host and headless peer are the same binary (`StudioWebApplication.Build` vs `BuildPeer`); connected mode = `Peer:HostUri` → `RoomPeerClient` room replication | Exists (`docs/guides/headless-peer.md`) |

The work in this plan is overwhelmingly **wiring, not invention**.

## Phase 1 — Tree structural sharing (do before it gets out of hand)

**The problem — and a correction of a natural misreading:** materialization does *not*
replay ops to reconstruct a repo. Every `RepositorySnapshot` carries its **complete**
`TreeEntries` map inline (path → BLAKE3); the materializer reads the map directly. Op
replay is used in exactly two places: `ConsiderCompactionAsync` folding accumulated ops
into the next map, and the legacy fallback for pre-Phase-2 snapshots whose `TreeEntries`
is null. So correctness is fine; the issue is **metadata duplication**: a 5,000-file
repo ≈ 400–500 KB of map JSON *per generation*, stored in the DAG node and replicated
through the room every generation. Under frequent snapshotting, metadata growth outpaces
blob growth — and it rides the replication plane (bandwidth + node store), not cheap
object storage.

**The fix: move the tree into the CAS; the snapshot node carries only a root hash.**
`RepositorySnapshot.TreeHash` already exists as a field — Phase 1 makes it load-bearing.

| Slice | Content | Acceptance |
|---|---|---|
| 1.1 | **Tree-as-blob**: serialize the map deterministically (sorted paths, canonical JSON), store as a CAS blob, `TreeHash` = its BLAKE3; write path emits it, node drops inline `TreeEntries` for new snapshots. Read path: resolve `TreeHash` → blob → map, with fallback to inline `TreeEntries` for legacy nodes (never migrate/rewrite old nodes — append-only, AP-5) | New snapshots carry no inline map; materializer + Case-2 sync + compaction all resolve via `TreeHash`; legacy snapshots still materialize; two runs → byte-identical tree blob |
| 1.2 | **Directory-level sharing** (git-tree style): per-directory tree objects, each hashing its children (entries + subtree hashes); unchanged subtrees share hashes across generations, so a generation costs O(changed paths × depth), not O(repo) | A 1-file change in a 5,000-file repo writes ≤ depth-of-path new tree objects; root hash changes; all unchanged subtree hashes are byte-identical to the previous generation |
| 1.3 | **Tree objects enter GC reachability** — live set = blobs *and* tree objects reachable from retained snapshot roots (feeds Phase 5's `LiveHashSource`) | Reachability walk from a root enumerates exactly the referenced tree + file blobs |

Notes:
- 1.1 alone removes the map from the replication plane (it becomes content, fetched via
  the blob channel like any blob); 1.2 removes the per-generation storage duplication.
  Ship 1.1 first — it's the urgent half.
- Core has `mst.rs` (Merkle search tree) — the long-term convergence-friendly structure
  if trees ever need CRDT-side diffing/sync. Not required for 1.2; noted so 1.2's
  serialization at least doesn't preclude an MST re-encoding later (encoding under a
  hash is swappable, per the decision record).
- `RematerializeAsync`'s map diff gets cheaper with 1.2 for free (skip identical
  subtree hashes without loading them).

## Phase 2 — Remote blob wiring (push on import/writeback, resolve-fetch)

The core unlock for standalone server + remote extensions. Rollout source #1 from the
harness plan's design notes: **server-relay HTTP first** — the coordination server is
required anyway.

| Slice | Content | Acceptance |
|---|---|---|
| 2.1 | **Server blob surface**: confirm/expose `GET/PUT /blobs/{hash}` (or the existing Rust server blob flow's equivalent) on the standalone server; conditional PUT (already-exists = no-op 200); content-length limits; auth = same channel as room protocol | Round-trip put/get against a running server; layout matches `BLOB_STORAGE_LAYOUT.md` so Rust/.NET stores stay interchangeable |
| 2.2 | **`ChainedBlobStoreProvider`** (local cache → remote origin): `TryGet` = local hit, else remote fetch → **verify BLAKE3** → write-through to local → return; `Put` = local write + remote push. Registered by config; existing Studio consumers unchanged (they already take `IBlobStoreProvider`) | Cold local store materializes a branch entirely from the server; corrupted remote payload is rejected (hash mismatch) and surfaces as a fetch failure, never written to cache |
| 2.3 | **Push semantics + reconcile**: remote push is synchronous-with-retry on the import/writeback path (`RepositoryImportService` Case 1/2, merge writeback via `ForceSyncAsync` — these already call `PutBlobAsync`, so the chained provider gets push for free); plus a **reconcile sweep** (enumerate local blobs referenced by retained snapshot roots, push any the server lacks) for blobs written before the remote was configured or during outages | Kill the server mid-import; reconcile sweep afterward converges server to complete; a fresh peer can then materialize every retained generation |
| 2.4 | **Scoped fetch**: materialization stays `FileScope`-scoped (already is); add optional prefetch hook — a work unit's declared `FileScope` prefetches while connected (bounds offline exposure, per harness plan) | Materializing a 40-file scope in a 5,000-file repo fetches ≤ 40 file blobs + the tree path |

Notes:
- Verification-on-fetch (2.2) is the one behavior that must live in the provider, not
  callers — `MaterializationEngine` currently trusts the store.
- Offline behavior unchanged from the harness plan's stance: a peer can't materialize
  blobs it never cached; local-first ≠ serverless; prefetch bounds it.

## Phase 3 — zstd compression at rest

Biggest storage win per line of code, zero architectural footprint.

- **Invariant: the hash is always BLAKE3 of the *uncompressed* bytes.** Compression is a
  storage/transport encoding, never an identity change. (Same rule that keeps future
  chunk/delta encodings honest.)
- Server-side store compresses at rest (zstd, mid level ~3–6); stores
  `contentEncoding` metadata beside the object; decompresses on serve *or* serves
  compressed with `Content-Encoding: zstd` when the client advertises support
  (chained provider decompresses before verify/cache).
- Skip-compress heuristic for already-compressed content (images, archives, wasm) —
  by declared content type + entropy sniff on first N KB.
- Local `FileBlobStoreProvider` compression is optional/config — local disk is cheaper
  than local CPU on every materialize; default off locally, on server-side.
- S3 note (Phase 4): compress client-side before upload — S3 won't do it for you; the
  presigned-URL path uploads the zstd bytes with metadata.

Acceptance: put/get round-trips are byte-identical to input; a code-heavy corpus shows
≥ 3× at-rest reduction; mixed corpus never *grows* (skip heuristic works).

## Phase 4 — Delegated S3 / S3-compatible backends

Rollout source #2: offload bandwidth from the coordination server.

- **Config-pluggable backend**, one knob family:
  `BlobStore:Backend = local | server-relay | s3`, with `Endpoint`, `Bucket`, `Prefix`,
  credentials. "Pseudo-S3" (MinIO, R2, SeaweedFS, Garage) is the same code path — the
  S3 API + endpoint override *is* the pluggability; no per-vendor adapters.
- **Presigned URLs via `IBlobUrlResolverProvider`** (the seam exists): server resolves
  `hash → presigned GET/PUT`; peers transfer directly against the bucket; the server
  never proxies bytes. `WsOnlyBlobStoreProvider` already models the
  "URL-resolution-only host" shape.
- Layout must match `nodalmerge-s3-blobs` / `BLOB_STORAGE_LAYOUT.md` (the parity
  refactor was for exactly this — one layout, any server).
- Chained provider grows one link: local → server-relay → s3-direct, by config; the
  BLAKE3 verify-on-fetch from 2.2 applies to every link identically (which is why
  source order is a pure availability/cost decision, never correctness).
- Upload integrity: verify hash before requesting the put URL (content is
  self-identifying); server marks the asset `Uploading → Active` per the GC lifecycle
  after a HEAD confirms arrival (contract already in `delegated-storage-gc.md`).

**Verified seams (2026-07-15) — more exists than the table above implied:**

- .NET host already exposes presign resolution over HTTP: `GET /sync/blob-url`
  (`WebApplicationExtensions.HandleBlobUrlAsync` → `IBlobUrlResolverProvider`), backed
  by `S3DelegatedBlobUrlResolverProvider` (delegate presign protocol v1, retry +
  circuit breaker, per `BLOB_STORAGE_LAYOUT.md` §7).
- Rust `server/s3-blobs` crate (`S3BlobStore : BlobPersistence`) is complete: Direct
  (server mints presigned URLs from IAM creds) *and* Delegate auth modes,
  `resolve_get_url`/`resolve_put_url`/`verify_uploaded` hooks, MinIO round-trip test.
  The trait hooks are already consumed by the WS blob flow (`blob-redirect` /
  `blob-uploaded`).
- What does **not** exist: the server binary has no way to *select* the S3 backend
  (`main.rs` wires only the disk store; the crate is never composed in), and the
  frozen HTTP blob surface (`docs/BLOB_HTTP_SURFACE.md`) has no URL-resolution
  endpoint — presign redirect today is WS-only.

**Decision (2026-07-15): URL resolution is an explicit endpoint, not a 307 redirect on
`GET/PUT /blobs/{hash}`.** Redirect-with-body semantics for PUT are fragile across HTTP
clients (auth headers stripped cross-origin, bodies not reliably replayed on 307), and
the chained provider's s3-direct link needs the URL as a value anyway (it GETs/PUTs the
bucket itself). The relay endpoints stay exactly as frozen; URL resolution is a new
optional capability — servers without a delegated backend answer 501, and clients fall
back to the relay path. This also keeps relay-only deployments (Phase 2) conformant
with zero change.

| Slice | Content | Acceptance |
|---|---|---|
| 4.1 | **Contract: blob URL resolution over HTTP** — extend `BLOB_HTTP_SURFACE.md` (new §): `GET /blobs/{hash}/url?op=get\|put[&size=&contentType=]` → `{ "url": "...", "expiresAtUtc": "..." }`, plus `POST /blobs/{hash}/uploaded` (upload confirm → server HEADs bucket, flips `Uploading → Active` per `delegated-storage-gc.md`); 501 when the backend can't presign; 400 on malformed hash (same rule as existing endpoints). Align .NET's existing `/sync/blob-url` semantics to the frozen shape (keep the old route as alias or retire — implementer's call, note it in the doc). Freeze bucket object layout in the same §: objects live at `{Prefix}blake3/{hex}[.zst]`, hash of *uncompressed* bytes (v3 rule) — verify `nodalmerge-s3-blobs` key derivation matches and fix if not. Contract vectors added like the S2 ones | Vectors check in both repos' CI; .NET endpoint conforms (`ProviderHttpEndpointTests` extended); doc merged before any server code ships |
| 4.2 | **Rust server: selectable S3 backend + URL endpoints** — config/CLI (`--blob-backend s3` + endpoint/bucket/prefix/creds via env) composes `Composite(DirPersistence nodes, S3BlobStore blobs)`; implement 4.1's two endpoints on the HTTP origin (`blob_http.rs`) over `resolve_get_url`/`resolve_put_url`/`verify_uploaded`; `HEAD /blobs/{hash}` answers via bucket HEAD; relay `GET/PUT /blobs/{hash}` keep working against the S3 backend (server-proxied — the escape hatch, not the fast path) | MinIO-backed integration test (same pattern as `minio_round_trip.rs`, gated/ignored in default CI): put via presigned URL + confirm → HEAD 200 → get-URL → direct GET returns bytes; disk-backend server answers 501 on `/url` |
| 4.3 | **.NET s3-direct chain link + client-side zstd** — `S3DirectBlobStoreProvider : IBlobStoreProvider` (resolve URL via origin's 4.1 endpoint → GET/PUT the bucket directly → `POST .../uploaded` confirm after PUT); chain config grows `BlobStorage=ChainedRemote` → `local → server-relay → s3-direct` (order/selection by config; BLAKE3 verify stays only in `ChainedBlobStoreProvider`, unchanged); client-side zstd before presigned PUT (S3 won't compress for you — upload `.zst` bytes + metadata per layout §8, skip-heuristic reused from S3.1a) and decompress-before-verify on GET; `nodalmerge.blobOrigin.*` extension settings extended for the new link | Cold-peer materialization test from 2.2 rerun with bytes flowing peer↔MinIO and only URL resolution touching the server; corrupted bucket object rejected by the chain, never cached; server-relay ↔ s3 swap is config-only, no Studio code change |

## Phase 5 — GC & retention (the real answer to snapshot flux)

Implements `nodalmerge/docs/delegated-storage-gc.md` for the Studio/repo domain. The
contracts, lifecycle states, and safety rules are already frozen there; this phase
supplies the missing product-side pieces.

**Verified seams (2026-07-15) — the machinery is mostly built; the policy is what's
missing:**

- `IWorkspaceCacheManager.GetLiveBlobHashesAsync` exists (Phase 1.3/2.3 work) with the
  fail-closed contract already normative (throws on partial scan, never returns a
  partial set; both `/studio/cache/gc` and the reconcile sweep honor it). Its current
  liveness rule is **every stored snapshot + every op** — maximally safe, zero
  reclamation. Phase 5 = replacing that rule with a retention policy.
- `FileBlobGcCoordinator` (Host.Composition) already does mark → tombstone → grace →
  delete over the file layout with dry-run/live modes and `MaxDeletesPerRun`.
- Rust: `core/gc` crate (`GcCoordinator`, contracts, conformance + fault-injection
  tests) exists; `server/src/gc_adapter.rs` runs it **MarkOnly with noop stores** as a
  preflight — real deletion still flows through legacy `BlobPersistence::blob_gc_sweep`
  (tombstone+grace semantics, tested in `blob_gc.rs`). 5.3's job is real inventory/run
  stores, not a new coordinator.

**Retention doctrine (append-only preserved):** aging out an intermediate generation
reclaims its *unique blobs and tree objects*, never the snapshot node itself — the DAG
row stays (AP-5, nodes are never rewritten or deleted by GC). Materializing a retired
generation fails gracefully with "aged out per retention policy", which is honest: the
history of *why* is permanent; the bytes of *what* are cache. (Node-store growth from
tasks/messages/history is a separate, non-CAS retention question — explicitly out of
scope here; see Out of scope.)

**5.1 findings (shipped 2026-07-15, `dc617ac`) — data-model gaps the classification
works around, recorded for eventual hardening:** (1) No persisted branch-seed /
merge-base snapshot reference exists (`WorkUnitFanOutInfo.SeedFromBranchId` is a
BranchId; `RepositorySnapshot.WorkUnitId` is never set by any call site; branch seeding
is a filesystem copy) — the Active class uses a documented proxy: latest snapshot per
repo with `CreatedAt <= WorkUnit.CreatedAt` for each non-terminal work unit. Exact
seed-pinning would require persisting the seed snapshot id at branch-creation time — a
small, worthwhile hardening candidate before 6.5 relies on merge-base liveness. (2) The
Intermediate age-out clock falls back to the snapshot's own `CreatedAt` (the intended
branch-terminal timestamp needs `RepositorySnapshot.WorkUnitId`, unset today). (3)
Terminal work-unit statuses per `WorkUnitTransitions` are `{Completed, Merged, Failed}`
— deliberately narrower than the cache-eviction terminal set (`Cancelled`/`DeadLettered`
have revival edges). Bootstrap marker: `Source == "Bootstrap"`; current head: max
`Generation` per repo (`RepositorySyncStateV1.Generation` is an unrelated drift
counter).

| Slice | Content | Acceptance |
|---|---|---|
| 5.1 | **Retention classification (pure policy, no behavior change)**: `ISnapshotRetentionPolicy` classifies every snapshot generation: **Pinned** (bootstrap generation; generations referenced by applied merge proposals — the `appliedSnapshotId` stamp from the apply-time resync; admin pins) — live forever by default; **Active** (referenced as any non-terminal work unit's branch seed or merge base, or the current head of any repo) — live regardless of age; **Intermediate** (everything else) — live until `RetainIntermediateDays` (default 30) past its branch reaching a terminal state. Ships as a service + tests only; nothing consumes it yet | Classification of a seeded test DAG matches hand-computed expectation for all three classes; an in-flight work unit's seed is Active even when >30 d old |
| 5.2 | **`LiveHashSource` v2 + staged local sweep**: `GetLiveBlobHashesAsync` switches to union of (a) trees+blobs reachable from Pinned ∪ Active ∪ unexpired-Intermediate generations (via `GetReachableHashesAsync`, fail-closed unchanged), (b) op-referenced blobs, (c) pins; `/studio/cache/gc` + a scheduled background run gain staged modes (`DryRun` default → `MarkOnly` → `SweepSoft` → `SweepHard` by config), `MaxDeletesPerRun` ramp, and a run-ledger row per run (mode, counts, duration — queryable) | Promoted-history materialization works after aggressive GC; a retired branch's unique intermediate blobs are reclaimed after grace; a DryRun run mutates nothing and reports the same candidates the live run would delete |
| 5.3 | **Server-side coordinator (depends on 6.1/6.3 — server must hold the replicated repo rooms)**: server `LiveHashSource` = walk studio snapshot nodes in every repo room it persists (same retention classes, computed from replicated proposal/work-unit nodes) + tree-object walk against its own CAS; replace `gc_adapter`'s noop stores with real `AssetInventoryStore`/`GcRunStore` (server store-backed); full mark → soft → hard with 24 h grace, `require_head_before_delete` during rollout; S3 backend deletes via `BlobObjectStore` HEAD/DELETE (no ListBucket in the nightly path) | Staged `DryRun`/`MarkOnly`/`SweepSoft`/`SweepHard` rollout works by flag; a blob re-referenced during grace returns to `Active` (extends `blob_gc.rs`); fail-closed: a room that fails to scan aborts the run with no deletes |

**5.2 findings (shipped 2026-07-15, `e22e2f1`) — for 5.3's implementer:** (1) **Op
protection is the dominant local retention leak**: rule (b) protects every
`RepositoryOp`'s blob refs forever, so import-driven generations effectively never
reclaim until op rows are compacted/retired — 5.3 (or a small follow-up) should decide
whether ops already folded into a snapshot (`ConsiderCompactionAsync`) drop out of the
live-set contribution. (2) The local `FileBlobGcCoordinator` can't express single-pass
hard delete (`RequireTombstoneBeforeDelete` always on) — SweepHard is two calls
(mark, then delete), an intentional safety property; the Rust `core/gc` crate has real
Mark/Soft/Hard stages, so do NOT copy the local grace-window encoding server-side.
(3) `SnapshotTreeResolver`'s immutable memo cache can keep a reclaimed generation
materializable from process memory until restart — benign, documented in tests.

Standing rule (from the harness plan, restated as normative here): **never build
anything that assumes a pushed blob can be synchronously deleted.** Offline peers may
hold references the server hasn't seen; grace windows + the merge-base liveness rule
(the Active class in 5.1) are the protection.

This phase is what makes "we snapshot every change" permanently a non-problem: truth is
the promoted history; everything else is cache with a TTL.

## Phase 6 — Multi-user: replication data plane, room topology & repository identity

**Decided stance on the "extension as peer vs. integrated client" question
(2026-07-12): the extension's local embedded host *is* the peer; the extension stays a
UI client of localhost; the server stays a separate entity.** Rationale:

- The embedded host and the headless peer are already the same binary — "user peer" is
  the embedded host with `Peer:HostUri` set (room presence via `RoomPeerClient`), *plus*
  its HTTP/MCP surface kept on for the extension UI. That's configuration, not a new
  mode. A user peer is a headless peer + UI + a human executor.
- It's the pods architecture (harness plan north star) and satisfies contract
  principle 9 (no field requires a live central server): local replica, local CAS
  cache, local materialization, local harness with the user's own credentials.
- A human editing their locally materialized branch is just another executor — diff
  harvest → proposal → AP-4 — no human-specific machinery (already noted in the
  harness plan's hybrid topology).
- The alternative (thin client: extension → remote server REST, server owns the
  replica) means no offline, credentials/server entanglement, and a *second* wire
  contract to maintain. Not the design center. (A degraded browser/thin mode can exist
  someday as a *view* over the server peer — out of scope here.)

The server's three roles stay cleanly separated: **just another peer** (with more
uptime) for replication; **coordination authority** for promotion ordering;
**well-known blob origin** (Phases 2/4). Nothing else migrates to it.

**Reality check (verified 2026-07-14) — Phase 6 is *not* thin.** The room plane today
is presence plus one-way broadcast, not replication: the embedded `Build()` path never
registers `RoomPeerClient`/`HeadlessPeerOptions` (only `BuildPeer()` does — the old
"verify/fix" is confirmed a **fix**); `RoomPeerClient` sends a `hello` with an empty
frontier and then *logs* inbound `catch-up-pack` messages without applying them;
nothing ingests packs into `IStudioNodeStore`; outbound, the only flow is the 30-second
`StudioCrdtSyncBackgroundService` checkpoint promotion broadcast, host→peers, which
receivers discard. A connected peer's work units live in its own SQLite DB and never
leave the process. The headless-peer guide's "replicated to the room" describes intent,
not code. Slice 6.1 below is the largest single lift in this plan.

**Deeper reality check (verified 2026-07-15) — the two-worlds finding, and why 6.1 is
tractable:**

- The .NET host's node store today holds **two parallel worlds** that never touch: (1)
  Studio's own rows — `NodalMergeStudioNodeStore` writes `AcceptedNodeRecord`s with
  payload kind `"studio"`, node IDs `studio:{kind}:{entityId}:{ticks}` (append-only,
  LWW-by-latest-tick per entity), **directly** to `INodeStoreProvider`, bypassing the
  engine entirely; (2) engine CRDT packs — `RuntimeDagPersistenceService` persists
  payload-kind-`"pack"` rows and replays them into the engine on hydrate. The 30 s
  promoter broadcasts engine packs — which contain none of Studio's rows. That is the
  precise mechanism by which "work units never leave the process."
- The primitives 6.1 needs all exist: engine `HostCommand::MapSet/MapGet/MapAll`
  (LWW map writes that produce DAG nodes), `ImportPack`/`RequestServerPack{known_ids}`/
  `InspectPack` (pack exchange + frontier), and the Rust server's WS room protocol
  already accepts peer `{"type":"pack", nodes: b64}` messages with scope-filtered
  rebroadcast and frontier catch-up (SpeechSlate-proven). Inbound pack persistence
  (`PersistInboundPackAsync`) and hydrate-with-compaction already exist on the .NET
  side.

**Engine-behavior discovery (6.1a, 2026-07-15, verified empirically):** the engine's
`MapSet/MapGet/MapAll` live map state and its CRDT *sync graph* (what
`RequestServerPack`/`ImportPack` export/import, i.e. what pack persistence and packs
on the wire actually carry) are **separate stores bridged only by explicit commands**:
a `MapSet` reaches the sync graph only via `PromoteCheckpointToGraph{latest}`, and
`ImportPack` repopulates only the graph — never the live maps. 6.1a established the
pattern that bridges this with existing commands (promote-then-persist on every write;
replay `GetCanonicalResolution` back through `MapSet` once after hydrate — see the
`NodalMergeStudioNodeStore` class comment, the authoritative writeup). **Consequence
for 6.1b:** a peer applying an *inbound* pack has the same gap — after `ImportPack` it
must replay canonical resolution into the live maps or Studio reads see nothing. A
cleaner engine-side fix (e.g. `ImportPack` hydrating maps directly) is a candidate
engine improvement, deliberately NOT taken in 6.1a/6.1b to avoid an engine release +
package repack mid-phase; revisit after 6.5.

**Wire reality recorded by 6.1b (2026-07-15):** the server (both runtimes) never sends
a literal `catch-up-pack` message type — catch-up and live broadcast both use the same
`{"type":"pack"}` envelope (optional `from` distinguishes). `hello.frontier` is
consumed as sync-graph tip ids; `welcome.missing` lists ids the server lacks.
`RequestServerPack{known_ids}` diffs over *every id the graph ever held* (not
ancestry-aware), so instead of known-ids bookkeeping, outbound push uses
`HostCommand::MstDone{ids}` as a precise inclusion-based fetch of the just-promoted
node (`CheckpointPromoted.node_id_hex`). Also fixed: `Build()` (embedded host) now
registers `RoomPeerClient` — previously only `BuildPeer()` did, confirming the plan's
"fix, not verify" call. Echo suppression is structural (inbound apply never touches
the outbound seam). Seams added for later slices: `IStudioReplicationOutbound`
(outbound notify) and `IStudioNodeStoreReplicationSink` (canonical→live-map replay
after inbound apply); in-memory service caches still don't see mid-run inbound changes
— that refresh story is 6.5 territory.

**Decision (2026-07-15): Studio state rides the engine DAG — no parallel replication
protocol.** `NodalMergeStudioNodeStore` is rewritten to write through the embedded
engine (`MapSet{namespace: "studio", key: "{kind}/{entityId}", value: payload
envelope}`) and read back from engine state; SQLite keeps its existing role as the
engine's pack persistence (already built). Rationale: the architectural model already
says the replication plane carries rooms as CRDT packs — this is that doctrine applied,
not a new invention; per-entity LWW is exactly the semantics the tick-suffixed rows
have today; and the engine brings pack format, frontier catch-up, scope filtering,
signing/policy, and MST sync for free, all of it already conformance-tested against the
Rust server. The alternative (a bespoke studio-record pack protocol beside the engine)
duplicates every one of those and leaves the two-worlds split in place permanently.
Migration: one-shot import of legacy `"studio"`-kind rows into engine maps on first
start (legacy rows stay readable, never rewritten — AP-5).

| Slice | Content | Acceptance |
|---|---|---|
| 6.0 | **Schema/encoding freeze (doc-first, like `TREE_OBJECT_FORMAT.md`)**: one note freezing (a) the studio-node→engine-map encoding: namespace/key scheme, payload envelope (payload JSON + kind + schema version), delete/tombstone encoding; (b) the workgroup-room repositories map: fields (`repoId → {label, repoRoomId, hints}`), hint formats (root-commit SHA **set**, remote-URL normalization rules), `repoRoomId` naming (`repo/{repoId}`); (c) the pinned cross-repo reference triple encoding `(repoId, generationId, path)`. Frozen before the first replicated write exists, because the first peer that writes one freezes it | Doc merged + review; encoding vectors (sample records → packed bytes) checked into the parity CI like the S2/S3 vectors |
| 6.1a | **Studio store on the engine (local-only, no network)**: `NodalMergeStudioNodeStore` v2 writes via `IRuntimeCommandBridge` `MapSet` per 6.0's encoding and reads via `MapGet`/`MapAll` after hydration; one-shot legacy-row migration on first start; `InMemoryStudioNodeStore` stays for tests; the entity-LWW semantics of `ReadAllNodesAsync` (latest per entityId) preserved exactly | Full Studio test suite green with the engine-backed store; restart rehydrates all studio state from packs (existing hydrate path); a workspace created pre-6.1a upgrades in place and loses nothing |
| 6.1b | **Bidirectional peer wire**: `RoomPeerClient` sends real frontier in `hello` (from engine tips), applies inbound `catch-up-pack`/`pack` via `ImportPack` + `PersistInboundPackAsync`, and pushes outbound delta packs at write time (delta via `RequestServerPack{known_ids: last-acked frontier}` or engine write events — implementer's choice, tested either way). Retire the 30 s `StudioCrdtSyncBackgroundService` promoter and the log-and-discard handler | Work unit created on peer A appears in peer B's store without restart on either side; kill/reconnect B mid-stream → frontier catch-up converges; no polling promoter left in the path |
| 6.2 | **Workgroup room + repository identity** (D1/D2, encoding per 6.0): repositories map lives in the workgroup room; mint-on-register; hint matching (root-SHA set + normalized remotes, fork tiebreak, one-time disambiguation prompt); binding cached in peer workspace storage; `RepositoryRegistryService` demoted to "local candidate pending workgroup registration" | Two peers with independent clones of the same repo converge on one repoId/room; a fork sharing root SHAs is **not** silently unified; shallow/no-remote clone degrades to prompt, not misfile |
| 6.3 | **Room-per-repo** (D1/D3): repo-room provisioning at registration; Studio room usage keyed by repoId (delete the hardcoded `"studio"` constant in `NodalMergeStudioNodeStore` + `RuntimeGraphPromoter`'s successor); peer joins/leaves repo rooms on materialize/retire; workgroup room carries cross-repo goal references; pinned `(repoId, generationId, path)` reference resolution via room + CAS | Peer joins only rooms of repos it materializes; a goal spanning two repos = two work units in two rooms + one workgroup goal node; a reference file resolves on a peer with no local clone of the referenced repo |
| 6.4 | **Mode collapse in extension + peer** (D4): `Room` config section (`HostUri`, `Workgroup`); embedded runtime always includes the room client; `HostManager` always adopts/spawns *its* local peer and passes room config through — delete the `isLocalUri` adopt-guessing and the spawn-shadow-host fallback (third outcome: spawn *and* join room) | Standalone→connected is a settings change + restart with no other local behavior difference; a second developer's window joins the room instead of silently creating an isolated universe |
| 6.5 | **The multi-user milestone**: connected-mode visibility; cold-peer branch materialization via room + chained provider (2.2/4) + local harness; cross-peer `ForceSyncAsync` divergence reconciling through the recorded 3-way base (catch-up merge, not rebase) | Two laptops + one server: work on A appears in B's extension; cold peer B: goal visible → materialize scoped branch → edit → harvest → proposal replicates back; divergent edits on A and B both land and reconcile |

**Explicit boundary:** promotion-ordering authority over the room protocol and
per-*user/peer* attribution are harness-plan future-state gaps #2 and #3 — *not* this
plan. (*Repository* identity, by contrast, is in scope here — slice 6.2.) Phase 6 makes
content and metadata flow to multi-user; who may promote, and as whom, stays where it
is until that design happens.

## Rollout order & sizing

```
Phase 1 (tree sharing)   — shipped 2026-07-15
Phase 2 (server relay)   — shipped 2026-07-15
Phase 3 (zstd)           — shipped 2026-07-15
Phase 4 (delegated S3)   — independent; 4.1 → 4.2 → 4.3 strictly in order
Phase 5 (GC/retention)   — 5.1 → 5.2 anytime (Studio-side, immediately useful
                           locally); 5.3 REQUIRES 6.1/6.3 (the server can only
                           compute reachability over rooms it actually holds)
Phase 6 (multi-user)     — 6.0 → 6.1a → 6.1b is the critical path; 6.2/6.3
                           follow 6.1a; 6.4 anytime after 6.1b; 6.5 last
```

Recommended interleaving for the 4–6 push (dependency-honest, keeps every slice
independently landable): **4.1 → 4.2 → 4.3** and **6.0 → 6.1a → 6.1b** can proceed as
two parallel tracks (different repos/layers, no shared files); then **5.1 → 5.2**
(Studio-side retention, wants quiet ground after 6.1a's store rewrite); then
**6.2 → 6.3 → 6.4**; then **5.3** (needs replicated rooms); then **6.5**.

| Phase | Slices | Ships alone? |
|---|---|---|
| 1 | 3 (shipped) | Yes — immediate metadata relief |
| 2 | 4 (shipped) | Yes — remote materialization works end-to-end |
| 3 | 1 (shipped) | Yes |
| 4 | 3 | Yes |
| 5 | 3 | 5.1/5.2 yes; 5.3 after 6.1/6.3 |
| 6 | 7 (6.0, 6.1a, 6.1b, 6.2–6.5) | 6.0–6.4 individually; 6.5 is the multi-user milestone |
| **Total** | **21** | comparable to the harness plan |

## Out of scope / not doing

- **Delta/checkpoint storage for file content** — see decision record. Revisit only if
  a real deployment measures CAS growth that matters *after* zstd + GC; and then as
  chunk/delta encoding *inside* the blob store behind the same hash interface
  (FastCDC/similarity-style), never as op-replay for harness-edited content.
- **Op-derived CRDT operations for repo files** — contradicts the harness posture;
  character-level merge for code already ruled unsound.
- **Peer-to-peer blob fetch** — last and maybe never (NAT + availability pain for a
  niche offline-LAN win), unchanged from the harness plan.
- **Promotion ordering + per-user/peer attribution** — harness-plan future state, not
  storage. (Repository identity is *not* deferred — it's slice 6.2.)
- **Continuous file watching of external repos** — lazy sync-on-initiation is the
  accepted model; watcher is opportunistic later.
- **Thin-client extension mode** (extension → remote server without a local peer) —
  possible someday as a view, not the design center.
- **Node-store retention for non-CAS node kinds** (tasks, messages, execution events,
  history — the bulk of real node-store growth per the 2026-07-15 baseline) — a real
  problem, but a *replication-plane compaction* question, not a CAS/blob one; Phase 5
  reclaims blob/tree bytes only and never deletes DAG nodes. Design it against the
  engine's existing pack-compaction machinery when it hurts.

## Open items before starting Phases 4–6

(The Phase 1–3 open items — tree format note, blob-surface confirmation, provider
placement, baseline — are all resolved/shipped as of 2026-07-15.)

- ~~Workgroup-room schema note~~ — promoted to slice 6.0 (now also carries the
  studio-node→engine-map encoding, which 6.1a needs first).
- 4.1 decision recorded above: URL resolution is an explicit endpoint, not a redirect.
- 6.1 decision recorded above: Studio state rides the engine DAG (no parallel
  replication protocol).
- Small verification for 4.1's implementer: confirm `nodalmerge-s3-blobs` key
  derivation (`path_prefix` + hash) against `BLOB_STORAGE_LAYOUT.md` §3/§8 before
  freezing the bucket-layout §; fix the crate if they diverge (it predates the parity
  refactor).
- For 5.1's implementer: confirm the exact field carrying the applied-proposal →
  snapshot link (`appliedSnapshotId` stamp written by the apply-time resync — see
  `WorkspaceCacheManager`'s eviction helper comments) and the bootstrap-generation
  marker before coding the Pinned class.
