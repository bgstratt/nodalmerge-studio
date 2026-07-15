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
- [ ] Phase 4 — Delegated S3 / S3-compatible backends with presigned URLs
- [ ] Phase 5 — GC & retention (the answer to snapshot flux)
- [ ] Phase 6 — Multi-user: replication data plane, room topology & repository identity

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

Acceptance: same cold-peer materialization test as 2.2 but bytes flow peer↔bucket, with
only URL resolution touching the server; server-relay and s3 backends swap by config
with no Studio-code change.

## Phase 5 — GC & retention (the real answer to snapshot flux)

Implements `nodalmerge/docs/delegated-storage-gc.md` for the Studio/repo domain. The
contracts, lifecycle states, and safety rules are already frozen there; this phase
supplies the missing product-side pieces:

| Slice | Content | Acceptance |
|---|---|---|
| 5.1 | **Studio `LiveHashSource`**: reachability = union of (a) trees+blobs reachable from **retained snapshot generations** (see policy), (b) generations referenced as any active work unit's branch seed / merge base, (c) admin pins. Fail closed on partial scan (per spec) | Live-set of a test repo matches hand-computed expectation; unreachable intermediate generation's unique blobs appear as sweep candidates |
| 5.2 | **Retention policy (the knobs)**: pin **promoted/canonical** generations (those referenced by applied merge proposals + bootstrap) forever by default; intermediate generations on merged/abandoned branches age out after `RetainIntermediateDays` (default e.g. 30); anything referenced by an in-flight work unit is live regardless of age | Promoted-history materialization works after aggressive GC; a retired branch's unique intermediate blobs are reclaimed after grace |
| 5.3 | **Coordinator wiring**: mark → soft sweep → hard sweep with grace window (24 h prod default), `max_deletes_per_run` ramp, run ledger — per spec; runs on the server (the only place with global reachability) | Dry-run/MarkOnly/SweepSoft/SweepHard staged rollout; re-referenced blob during grace returns to `Active` |

Standing rule (from the harness plan, restated as normative here): **never build
anything that assumes a pushed blob can be synchronously deleted.** Offline peers may
hold references the server hasn't seen; grace windows + the merge-base liveness rule
(5.1b) are the protection.

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

| Slice | Content | Acceptance |
|---|---|---|
| 6.1 | **Replication data plane**: bidirectional pack exchange — outbound push of Studio DAG writes as packs at write time; inbound pack application into the Studio node store; real frontier-based catch-up on connect/reconnect. Retire the 30 s one-way checkpoint broadcast and `RoomPeerClient`'s log-and-discard handling | Work unit created on peer A appears in peer B's store without restart on either side; kill/reconnect B mid-stream → frontier catch-up converges; no polling promoter left in the path |
| 6.2 | **Workgroup room + repository identity** (D1/D2): repositories map schema (repoId → label, repoRoomId, hints); mint-on-register; hint matching (root-SHA set + normalized remotes, fork tiebreak, one-time disambiguation prompt); binding cached in peer workspace storage; `RepositoryRegistryService` demoted to "local candidate pending workgroup registration" | Two peers with independent clones of the same repo converge on one repoId/room; a fork sharing root SHAs is **not** silently unified; shallow/no-remote clone degrades to prompt, not misfile |
| 6.3 | **Room-per-repo** (D1/D3): repo-room provisioning at registration; Studio room usage keyed by repoId (delete the hardcoded `"studio"` constant); workgroup room carries cross-repo goal references; pinned `(repoId, generationId, path)` reference resolution via room + CAS | Peer joins only rooms of repos it materializes; a goal spanning two repos = two work units in two rooms + one workgroup goal node; a reference file resolves on a peer with no local clone of the referenced repo |
| 6.4 | **Mode collapse in extension + peer** (D4): `Room` config section (`HostUri`, `Workgroup`); embedded runtime always includes the room client; `HostManager` always adopts/spawns *its* local peer and passes room config through — delete the `isLocalUri` adopt-guessing and the spawn-shadow-host fallback (third outcome: spawn *and* join room) | Standalone→connected is a settings change + restart with no other local behavior difference; a second developer's window joins the room instead of silently creating an isolated universe |
| 6.5 | **The multi-user milestone** (previous 6.1–6.3): connected-mode visibility; cold-peer branch materialization via room + chained provider (2.2/4) + local harness; cross-peer `ForceSyncAsync` divergence reconciling through the recorded 3-way base (catch-up merge, not rebase) | Two laptops + one server: work on A appears in B's extension; cold peer B: goal visible → materialize scoped branch → edit → harvest → proposal replicates back; divergent edits on A and B both land and reconcile |

**Explicit boundary:** promotion-ordering authority over the room protocol and
per-*user/peer* attribution are harness-plan future-state gaps #2 and #3 — *not* this
plan. (*Repository* identity, by contrast, is in scope here — slice 6.2.) Phase 6 makes
content and metadata flow to multi-user; who may promote, and as whom, stays where it
is until that design happens.

## Rollout order & sizing

```
Phase 1 (tree sharing)   — local-only, ship anytime; 1.1 is the urgent half
Phase 2 (server relay)   — after standalone server exists; the core unlock
Phase 3 (zstd)           — rides along with 2.1's server store
Phase 4 (delegated S3)   — when server bandwidth says so
Phase 5 (GC/retention)   — before any long-lived multi-user deployment
Phase 6 (multi-user)     — 6.1–6.4 don't depend on 2 and can start early;
                           the 6.5 milestone needs 2; 4/5 harden it
```

| Phase | Slices | Ships alone? |
|---|---|---|
| 1 | 3 | Yes — immediate metadata relief |
| 2 | 4 | Yes — remote materialization works end-to-end |
| 3 | 1 | Yes |
| 4 | ~2 | Yes |
| 5 | 3 | Yes |
| 6 | 5 | 6.1–6.4 individually; 6.5 is the multi-user milestone |
| **Total** | **~18** | comparable to the harness plan |

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

## Open items before starting

- Confirm the Rust server's existing blob HTTP surface (fixtures/tests exist —
  `blob_flow_request_upload_and_fetch.json`, `blob_layout_interop.rs`) vs. what 2.1
  needs; decide reuse vs. thin new endpoint. Single verification task.
- Canonical tree-object serialization format (1.1/1.2): entry ordering, dir/file
  markers, empty-dir stance — write the format note *before* the first blob is emitted
  (it's frozen the moment one peer writes one; treat like the layout vectors).
- Workgroup-room schema note (6.2): repositories-map fields, hint formats (root-commit
  SHA set; remote-URL normalization rules), and the pinned-reference triple encoding —
  same freeze rule: written before the first workgroup room exists, because the first
  peer that writes one freezes it.
- Where `ChainedBlobStoreProvider` lives: `NodalMerge.Host.Composition` (alongside
  `FileBlobStoreProvider`, preferred — any host benefits) vs. Studio-side. Decide at
  2.2.
- Baseline measurement task (cheap, do first): count blobs / bytes / snapshot-node
  bytes in a real workspace after a heavy goal run — gives the before-numbers Phases
  1/3/5 get judged against.
