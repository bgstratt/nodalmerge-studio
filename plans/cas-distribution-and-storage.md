# CAS distribution & storage — remote blob store, tree sharing, retention

## Status

- [ ] Phase 1 — Tree structural sharing (metadata fix; local-only, no server dependency)
- [ ] Phase 2 — Remote blob wiring: server-relay endpoint + chained provider (push on
      import/writeback, resolve-fetch on materialize)
- [ ] Phase 3 — zstd compression at rest
- [ ] Phase 4 — Delegated S3 / S3-compatible backends with presigned URLs
- [ ] Phase 5 — GC & retention (the answer to snapshot flux)
- [ ] Phase 6 — Multi-user peer topology enablement (extension host as connected peer)

**Sequencing: this plan follows `harness-hosting-architecture.md`.** Nothing here blocks
Phases A–E there; conversely, nothing there blocks Phase 1 here (which is local-only and
could ship early if snapshot-map growth becomes noticeable first). This plan implements
"Enumerated gap #1 — CAS blob distribution" from the harness plan's multi-developer
future state, plus the storage decisions resolved 2026-07-12.

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

## Phase 6 — Multi-user peer topology enablement

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

Slices (thin by design — most of Phase 6 is Phases 1–5 plus existing peer machinery):

| Slice | Content | Acceptance |
|---|---|---|
| 6.1 | Embedded host in connected mode: `Peer:HostUri` + full HTTP surface coexist (today's guide frames connected mode for headless; verify/fix the embedded `Build` path allows `RoomPeerClient`); extension settings expose "connect to room" | Two laptops + one server: work units/artifacts/proposals created on A appear in B's extension |
| 6.2 | Branch materialization on a remote peer: B materializes a branch it has never seen — snapshot root via room, trees + blobs via chained provider (2.2/4) — and runs a local harness on it | Cold peer B: goal visible → materialize scoped branch → edit → harvest → proposal replicates back |
| 6.3 | Sync-on-initiation across peers: `ForceSyncAsync` on any peer produces the next generation from *its* recorded base; concurrent generations from two peers reconcile through the existing proposal/reconciliation path (catch-up merge, not rebase — per harness plan) | Divergent edits on A and B both land; second one reconciles against moved canonical via recorded 3-way base |

**Explicit boundary:** promotion-ordering authority over the room protocol and per-peer
identity/attribution are harness-plan future-state gaps #2 and #3 — *not* this plan.
Phase 6 makes content and metadata flow to multi-user; who may promote, and as whom,
stays where it is until that design happens.

## Rollout order & sizing

```
Phase 1 (tree sharing)   — local-only, ship anytime; 1.1 is the urgent half
Phase 2 (server relay)   — after standalone server exists; the core unlock
Phase 3 (zstd)           — rides along with 2.1's server store
Phase 4 (delegated S3)   — when server bandwidth says so
Phase 5 (GC/retention)   — before any long-lived multi-user deployment
Phase 6 (peer topology)  — after 2; 4/5 harden it
```

| Phase | Slices | Ships alone? |
|---|---|---|
| 1 | 3 | Yes — immediate metadata relief |
| 2 | 4 | Yes — remote materialization works end-to-end |
| 3 | 1 | Yes |
| 4 | ~2 | Yes |
| 5 | 3 | Yes |
| 6 | 3 | Yes — the multi-user milestone |
| **Total** | **~16** | comparable to the harness plan |

## Out of scope / not doing

- **Delta/checkpoint storage for file content** — see decision record. Revisit only if
  a real deployment measures CAS growth that matters *after* zstd + GC; and then as
  chunk/delta encoding *inside* the blob store behind the same hash interface
  (FastCDC/similarity-style), never as op-replay for harness-edited content.
- **Op-derived CRDT operations for repo files** — contradicts the harness posture;
  character-level merge for code already ruled unsound.
- **Peer-to-peer blob fetch** — last and maybe never (NAT + availability pain for a
  niche offline-LAN win), unchanged from the harness plan.
- **Promotion ordering + per-peer identity** — harness-plan future state, not storage.
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
- Where `ChainedBlobStoreProvider` lives: `NodalMerge.Host.Composition` (alongside
  `FileBlobStoreProvider`, preferred — any host benefits) vs. Studio-side. Decide at
  2.2.
- Baseline measurement task (cheap, do first): count blobs / bytes / snapshot-node
  bytes in a real workspace after a heavy goal run — gives the before-numbers Phases
  1/3/5 get judged against.
