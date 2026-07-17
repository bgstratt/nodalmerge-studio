# Blob/CAS remediation — hardening the blobExpansion surface

Follow-up hardening for the CAS work shipped on nodalmerge branch `blobExpansion`
(see [cas-distribution-and-storage.md](./cas-distribution-and-storage.md), Phases
2–6). A max-effort review of `git diff main...HEAD` on that branch surfaced **29
confirmed defects** plus efficiency and duplication debt. This plan sequences the
fixes into validate-as-you-go phases and slices so no single change is too large
to review or roll back.

**Finding #30 was added during Phase 0** (2026-07-16), found by slice 0.4 while
trying to assert a doc comment's claim — see the map below and slice 1.6.

## Status

- [x] Phase 0 — Reproduce-first scaffolding (shared harnesses + contract vectors) — **complete 2026-07-16** (0.1 ✅ 0.2 ✅ 0.3 ✅ 0.4 ✅; 10 gated RED tests + shared `blob-conformance` crate; uncommitted on `blobExpansion`)
- [ ] Phase 1 — GC data-loss & the crash (P0 — can permanently destroy blobs / abort the server) — **1.1 ✅ 1.3 ✅ 1.4 ✅ 1.5 ✅ 1.6 ✅ committed 2026-07-16** (`a4c4a694`, `043df73d`, `ef693f39`, `5664c2b1` + studio `58d9a5c`, `20aaba88`); **only 1.2 remains, blocked** on 7.5's composition seam. **1.3 closed the 1.4↔1.3 coupling: the write-through race is now genuinely fixed on BOTH the Dir and S3 paths** (proven end-to-end against a real bucket, not traced).
- [ ] Phase 2 — Non-hydrating (S3) backend correctness — **2.1 ✅ 2.2 ✅ 2.3 ✅ committed 2026-07-16** (`9fd95355`, `fe56ad34`, `5f34ab26`); **only 2.4 remains** (explicitly low-priority/latent). `blob_nonhydrating_conformance` has **no gated REDs left** (4 passed/1 ignored → 13 passed/0 ignored). **2.3 made 6.1 urgent — see Phase 6.**
- [ ] Phase 3 — Blob integrity & cross-runtime interop — **3.1 ✅ committed 2026-07-16** (`6cf2c8fd`); **3.2 code half ✅** (`37374294`) — **its doc half stays deferred until 3.3 lands** (sequencing note in 3.2)
- [ ] Phase 4 — HTTP-surface robustness & backward compatibility
- [ ] Phase 5 — Migration safety
- [ ] Phase 6 — Runtime, async-safety & efficiency
- [ ] Phase 7 — Duplication & altitude cleanup
- [ ] **Phase 8 — CI coverage (DEFERRED)** — the "CI is green" guarantee is much weaker than it looks. Not scheduled; **slices needing a step before this phase lands add the step where they think it belongs and move on** (see the phase for the stub convention).

## Guiding principles

1. **One plan, two repos.** Nearly all fixes land in **nodalmerge** (`server/**`,
   `hosts/dotnet/**`, `core/**`); a few are **studio**-side (`StudioInboundPackObserver`,
   the `WorkUnitStatus` enum, GC live-source composition). Each slice is tagged
   **[NM]**, **[ST]**, or **[BOTH]**.
2. **Don't change nodalmerge just for studio.** A nodalmerge change is in scope only
   when it fixes a system-wide correctness/robustness problem (data loss, crash,
   contract divergence) that any consumer would hit — not to paper over a studio
   need. Where a fix is purely studio's, it stays in studio.
3. **Preserve nodalmerge backward compatibility.** nodalmerge shipped `0.2.0` on
   `main`; external hosts consume its wire surface and its .NET package APIs. Prefer
   additive changes, keep frozen HTTP contracts and response shapes, and where a
   guard must tighten, **warn-don't-throw** on config that used to work. Slices that
   knowingly touch a compat boundary carry a **⚠ compat** note. Internal server
   trait changes (not on the wire, not in a published surface) are fair game.
4. **Studio is greenfield.** studio (`0.x`, pre-release) can take breaking changes
   freely — lean on that to fix things at the right altitude rather than bolting on
   compatibility shims studio doesn't need yet.
5. **Reproduce before fixing.** Every correctness slice starts by adding a test that
   fails against today's code, then makes it pass. Phase 0 builds the shared
   harnesses the later phases reuse so this is cheap.
6. **Slices are independently shippable.** A slice = one reviewable PR with its own
   validation gate. Phases order by severity and dependency; within a phase, slices
   are mostly parallelizable unless a dependency is noted.

## Finding → slice map

| # | Finding (short) | Repo | Slice |
|---|---|---|---|
| 1 | `Composite`/Postgres/Mongo don't forward `known_room_ids` → cold-room blobs deleted | NM | 1.1 |
| 2 | new coordinator live set is `studio/`-only → non-studio SetBlob blobs swept | NM | 1.2 |
| 3 | `S3BlobStore::blob_gc_sweep` ignores grace + skips inventory | NM | 1.3 |
| 5 | `sweep_blobs` races hydrating rooms / post-snapshot write-through | NM | 1.4 |
| 9 | `tree_walk` unbounded recursion → stack-overflow process abort | NM | 1.5 |
| 10 | `tree_walk` via `get_blob`=None on S3 → GC fails closed forever | NM | 2.2 |
| 7 | archive export/digest/hydration silently drop `get_blob`=None on S3 | NM | 2.3 |
| — | .NET HEAD/PUT-idempotency full-read → remote hydration (existence probe) | NM | 2.1 |
| — | WS blob-request fallthrough has no persistence fallback (latent) | NM | 2.4 |
| 4 | `BlobCompression.TryDecompress` rejects Rust zstd frames (no content-size) | NM | 3.1 |
| 13 | identity-path reads unverified; encoded pass-through unverified | NM | 3.2 |
| 6 | s3-direct client zstd default-on; web/wasm readers can't decode | ~~BOTH~~ NM | 3.3 |
| — | `Accept-Encoding: zstd;q=0` treated as accept (both hosts) | NM | 3.4 |
| — | .NET PUT ignores `Content-Encoding` (Rust 415) — parity gap | NM | 3.4 |
| 12 | legacy `/sync/blob-url` breaking changes vs `main` | NM | 4.1 |
| 8 | `put_blob` returns 201 + Active inventory despite failed persist | NM | 4.2 |
| — | `BlobHttpOptions` no `Validate()`; negative `MaxBlobBytes` → 500/413 | NM | 4.3 |
| — | root-relative request URIs drop `BaseUrl` path prefix | NM | 4.3 |
| — | `S3DelegatedBlobOptions` dropped `PutPath`/`GetPath` w/o startup guard | NM | 4.3 |
| 15 | capability-profile `limits` now profile-supplied, no hard-ceiling clamp | NM | 4.4 |
| — | Rust migration writes `.layout-v2` marker despite skipped rooms | NM | 5.1 |
| — | .NET migration re-quarantines `.migration-skipped`; `.tmp` leak; `SanitizeHash` mangles | NM | 5.2 |
| 11 | s3-blobs per-op thread+Runtime+timeout-less recv; delegate no timeout | NM | 6.1 |
| — | blocking blob I/O + BLAKE3 hashing on async workers (no `spawn_blocking`) | NM | 6.2 |
| — | per-hash autocommit GC upserts (100k txns); sequential S3 deletes (N+1) | NM | 6.3 |
| — | sweep reloads full room history/tick; studio-map replay/run; fresh visited-set/snapshot | NM | 6.4 |
| — | inbound-pack observer awaited inline in WS receive loop | BOTH | 6.5 |
| — | `server-s3` CLI/wiring copy; circuit-breaker ×3; date-arith ×2; canonical-hash/s3-key/bridge dup | NM | 7.1–7.3 |
| — | `_global` vs `default` delegate room-id divergence (unfrozen) | NM | 7.4 |
| — | GC coordinator hard-wires studio live-source into generic server crate (altitude) | NM | 7.5 |
| 14 | `WorkUnitStatus` ordinals hand-mirrored, unpinned by any vector (latent) | BOTH | 0.4 |
| 30 | `Failed` retention-aged as terminal despite a legal `Failed`→`Cancelled`→`Queued` revival path | BOTH | 1.6 |

---

## Phase 0 — Reproduce-first scaffolding

Builds the shared test surfaces the later phases assert against. No production
behavior changes. Ship these first so every subsequent slice can open with a
failing test.

### 0.1 — Cross-runtime zstd fixture **[NM]**
A golden fixture of a zstd frame produced by Rust `zstd::stream::encode_all` (the
at-rest encoder) and one by .NET `ZstdSharp.Compressor.Wrap`, each decoded by the
**other** runtime. This is currently untested — every `.zst` interop test is
same-runtime, which is exactly why finding #4 shipped. Land the fixtures + a RED
test that decodes the Rust frame in .NET (fails today).
- **Validation:** RED test committed and failing, referenced by 3.1.

### 0.2 — Non-hydrating backend conformance harness **[NM]**
A fake `BlobPersistence`/`IBlobStoreProvider` whose `get_blob`/point-read always
returns `None`/Missing (mirroring `S3BlobStore`), plus a conformance suite exercising
GC live-set, archive export, tree-walk, and HEAD/existence against it. Reproduces
findings #7, #10, and the 2.1 existence-probe gap in one place.
- **Validation:** suite compiles; the S3-seam assertions fail today (become the
  gates for Phase 2).

### 0.3 — GC data-loss regression suite **[NM]**
Five scenarios, each a RED test: (a) cold-room blob survives a sweep, (b)
inventory-referenced-but-not-yet-in-DAG blob survives, (c) `blob_gc_sweep` honors a
non-zero grace on the S3 path, (d) a blob write-through during a still-hydrating
room survives, (e) a `Failed` work unit's blobs survive retention age-out, since
`Failed` has a legal revival path (finding #30 — gates 1.6). These become the
acceptance gates for Phase 1.
- **Validation:** all five fail against current code.

### 0.4 — Pin the hand-mirrored contracts **[BOTH]** (finding #14)
Freeze what the review found unpinned: a `WorkUnit` payload vector carrying real
`WorkUnitStatus` ordinals + PascalCase casing, asserted by **both** the studio C#
enum ([WorkUnit.cs](../src/NodalMerge.Studio.Core/…/WorkUnit.cs)) and the Rust
consumer (`nodalmerge:server/server/src/studio_live_hashes.rs:96`). Add a
cross-repo parity check so a future enum reorder breaks CI, not production GC.
Likewise pin the delegate room-id placeholder (defer the value decision to 7.4;
here just add the vector slot).
- **Validation:** reordering the C# enum locally makes the new vector test fail.
- **Note:** studio owns the enum; nodalmerge owns the parser. The vector file is the
  shared contract — put it where the existing frozen vectors live in nodalmerge and
  have studio's test read it.

---

## Phase 1 — GC data-loss & the crash (P0)

The highest-severity cluster: five paths that can permanently delete live blobs, and
one remotely-reachable process abort. Everything here fails **closed** — when the
system can't prove a blob is dead, it must not delete it.

### 1.1 — `known_room_ids` completeness + fail-closed global sweep **[NM]** (#1) — ✅ **DONE 2026-07-16** (`a4c4a694`)
**As shipped:** `can_enumerate_rooms()` defaults `false`; `sweep_blobs` refuses the
**entire** two-phase sweep (warn + `nodalmerge_blob_gc_skipped_total{reason="cannot_enumerate_rooms"}`
+ 0 deletions) when it is false — not just the physical delete, since tombstoning against a
known-incomplete live set still starts a deletion clock on possibly-live blobs. Verified no
production impl is left behind: `Composite` forwards, `Dir`/`Postgres`/`Mongo` return `true`,
`NoPersistence` is short-circuited by the pre-existing `is_durable()` check. A not-enumerable
stub test was added alongside 0.3(a), which only covered the forwarding half.
⚠ **Residual risk — see the CI follow-up below.** The guard makes *absent* enumeration safe, but
Postgres/Mongo now assert `can_enumerate_rooms() == true`, which tells the sweep to **trust** their
`known_room_ids()` and delete. A broken/empty result from those queries reintroduces #1's data loss
*with the guard vouching for it* — and **no CI step runs the Postgres or Mongo suites**. Those two
impls are the safety-critical path and are covered only by local Docker runs today.
`nodalmerge:server/server/src/store.rs:291`. The trait default returns an empty
`Vec`; `Composite<N,B>` (the production server-s3 wiring) doesn't forward it, and
`PostgresNodeStore`/`MongoNodeStore` never implement it — so the global sweep treats
"no enumerable rooms" as "no cold rooms have blobs."
- Forward `known_room_ids` through `Composite`; implement it on the Postgres and
  Mongo node stores.
- **Altitude fix:** stop treating an empty enumeration as authoritative. Add a
  capability signal (e.g. `can_enumerate_rooms()`), and make the global sweep
  **refuse to delete** (log + skip) when the backing store can't enumerate, rather
  than deleting everything not resident. The empty default becomes safe by
  construction.
- **Validation:** 0.3(a) passes on `Composite`+S3 and on a store stubbed to
  not-enumerable.
- ⚠ compat: internal trait surface only — no wire/package break.

### 1.2 — New coordinator live set unions room-DAG blobs **[NM]** (#2)
`nodalmerge:server/server/src/studio_live_hashes.rs:638`. `collect_studio_live_hashes`
filters to `studio/`-prefixed keys, so any ordinary `SetBlob`-referenced blob in the
gc inventory is unmarked and swept.
- The coordinator's live set must be the **union** of studio-domain hashes and
  `blob_hashes_referenced_by` over every room DAG (the protection the legacy sweep
  already had). Compose the sources rather than replacing one with the other — this
  dovetails with the pluggable `LiveHashSource` in 7.5, so build the composition seam
  here and let 7.5 relocate the studio-specific source.
- **Validation:** 0.3(b) passes under `--gc-mode sweepsoft/sweephard`.

### 1.3 — S3 sweep honors grace + consults inventory **[NM]** (#3) — ✅ **DONE 2026-07-16** (`043df73d`)

**As shipped.** Real two-phase grace on S3, tombstones as bucket objects at
`<prefix>.tombstones/blake3/<hex>.<unix_millis>` — a sibling of `<prefix>blake3/` (never
enumerated by the blob listing), keyed by bare hex so one tombstone covers `<hex>` and
`<hex>.zst`. Branch-for-branch `DirPersistence`'s semantics. The bucket-versioning premise is
**deleted, not preserved** (it was off by default, never checked or set by the crate, the
comment invited operators to disable the one thing standing in for grace, a noncurrent version
isn't re-linked when a blob becomes live again, and it silently no-op'd 1.4's floor).
Versioning remains a fine *backstop*; it is no longer load-bearing.

> **The tombstone time lives in the KEY, not in the object's `last_modified`.** `last_modified`
> is the **bucket's** clock while `grace` is measured against **this process's**; skew in the
> unsafe direction (bucket behind) deletes early — exactly the failure this slice exists to
> prevent. Writing the time into the key means one clock both writes and compares it, and costs
> one LIST instead of a GET per candidate. A clock stepped back reads as age 0
> (`saturating_sub`) → wait, never delete. Worth remembering: **the obvious implementation
> (ask the bucket when the tombstone was written) is the unsafe one.**

**`blob_upload_grace` = 3600s (1h).** **DECIDED 2026-07-16 (user).** The window bounds
**bytes-exist → referenced-by-a-CRDT-op** — i.e. *client latency*. It is **not the presign
TTL**, and that is the whole subtlety: both upload paths stamp `last_seen_at` at the moment
the bytes exist (`blob_http.rs:406` after `verify_uploaded`, `:510` after the hash check), so
`presign_put_ttl` (15 min) bounds the interval that **ends before this one starts**. The plan's
original "ideally tied to the presign TTL" instinct conflated two intervals and would have
reclaimed legitimate slow uploads. 1h coincides with `presign_get_ttl` — a coherent story
("the longest URL we'd hand out") — but is chosen on its own merits. **Any finite window bounds
retention** (disk ≈ upload rate × window), so this is a "how slow a client do we tolerate"
call, not a safety-vs-unbounded one.

> **The knob is on `Rooms`, NOT on `S3BlobStoreConfig`** — a deliberate deviation from this
> section's original wording, and the right one. `S3BlobStore` has no inventory handle, and the
> union is on the *server-side legacy live set*, so a field on the S3 config would be read by
> nothing: **dead config that looks like a knob, on a P0 data-loss path.** On `Rooms` it also
> protects `DirPersistence` deployments, whose anonymous `PUT /blobs/{hash}` writes the same
> `Active` rows. Config-struct-only; no CLI surface (7.1's shared bootstrap module owns that).

> **The unbounded-retention hole was shipped on purpose to prove the gate catches it.** With the
> `last_seen_at` cutoff removed (a naive union of every `Active` row), **exactly one test
> failed** — `blob_gc_reclaims_unreferenced_upload_once_upload_window_elapses`. The *protection*
> test passed happily. Reproduced independently before accepting the slice. This is the plan's
> "without it, this hole ships silently and looks like a fix", made visible: **the reclaim gate,
> not the protection gate, is what proves you didn't just make everything live forever.**

**Trap 1 (the gate hazard) was avoided, not sidestepped.** The real backend was fixed **first**
and is asserted by three bucket-touching MinIO tests, verified RED against the *unmodified*
`S3BlobStore` and a real `minio/minio:latest` container (*"first sighting must only tombstone,
never delete"*, left: 1, right: 0). The `blob-conformance` fake was then brought back into
agreement — **updated, not deleted** (deleting it would have taken 0.3(c) and the relay suites'
S3-shaped fake with it). Its doc now states outright that it can only gate a *copy*, names the
three MinIO tests as the real gates, and says: *"if this port and `S3BlobStore` ever disagree,
the MinIO tests are right and this file is wrong."*

**CI:** new `.github/workflows/blob-s3-gc-minio.yml` (`minio_round_trip.rs` previously ran in
**zero** workflows — the test this slice's correctness rests on). Fail-open closed with
`NODALMERGE_REQUIRE_DOCKER=1`, verified both directions against a bogus image tag: **without it,
4 passed while testing nothing.** Also added `gc_store.rs` to `blob-layout-parity.yml`'s
`paths:` — `blob_gc.rs` now drives a real `SqliteGcStore` and that file could not previously
trigger the workflow (**fifth** instance of that bite).

**Gates:** `blob_gc` 9 passed/1 ignored → **12 passed / 0 ignored**; MinIO **4 passed**.

**Carried over knowingly (not regressions):**
- **`collect_recent_upload_hashes` fails *open*** on an inventory read error (`warn!` + empty
  set), so a transient SQLite error un-protects recent uploads for that tick. Same "a failed
  query is indistinguishable from an empty result" shape already filed against `known_room_ids()`
  on Postgres/Mongo — it wants **one** decision across all three, not three. See follow-ups.
- **1.4's "time as a proxy for call count" gap** applies verbatim to the S3 port: the guarantee
  is stated in ms but the invariant needed is *two separate sightings*, and nothing enforces
  separateness. Now in two places; if the sweep cadence ever tightens, both need the same
  "tombstone must predate this call" hardening.
- **Two concurrent sweeps can leave two tombstone keys for one hash** — handled fail-closed (age
  by the *newest* → smallest age → never delete early; all keys removed when the blob goes).
  No cross-process sweep lock exists; strictly safer than the previous behavior.
- **`iter_unmarked_candidates` is the only enumeration on `AssetInventoryStore`**, so the union
  calls it with a sentinel run id to mean "all non-Deleted rows". Works, reads/writes nothing,
  but is a load-bearing use of a method named for another purpose; an `iter_active_since(cutoff)`
  would be cleaner. A *defaulted trait method* was deliberately **not** added — that is exactly
  2.1's `RemoteBlobLinkAggregator` trap (a default that compiles, answers correctly, and hides a
  missing override).

**Original section text follows.**
`nodalmerge:server/s3-blobs/src/lib.rs:524`. `blob_gc_sweep` binds `_grace` and
never reads it (single-pass immediate delete), and the legacy live set never consults
the gc-inventory `Active` rows written by the upload-confirm path.
- Implement a real two-phase grace (tombstone/pending-delete then delete-after-grace)
  for the S3 backend, matching the Dir backend's semantics. Do **not** rely on bucket
  versioning as the implicit grace period — make it explicit.
- Union inventory `Active` rows into the live set on the legacy path so a
  confirmed-but-not-yet-referenced upload is protected. ⚠ **The union MUST be
  time-bounded** — see the retention hazard below.
- ⚠ **This slice can create an unbounded-retention hole. Do not union naively.**
  Liveness today is decided *per-run by marking*, not by the `state` column:
  `gc_store.rs:257` selects candidates `WHERE state != 'Deleted' AND
  (last_marked_run_id IS NULL OR last_marked_run_id != ?1)`. The PUT/confirm path
  (`blob_http.rs:406,510`) stamps `last_marked_run_id = "upload"` (`UPLOAD_MARK_SENTINEL`),
  a value no real run id ever equals — so an unreferenced upload is unmarked on the next
  run and correctly reclaimed. **`state = 'Active'` is inventory bookkeeping, not
  protection.** If this slice unions *all* `Active` rows into the live set, it converts
  that sentinel into permanent protection and every anonymous PUT becomes live forever —
  unbounded disk growth from an unauthenticated endpoint (blob PUT is anonymous by
  default; see 1.5's note, where auth was deliberately left optional).
  **Required design:** protect only `Active` rows whose `last_seen_at` falls inside a
  bounded upload window (a new `--blob-upload-grace`-style knob, defaulting to something
  like the presign TTL), so the presign→confirm→`SetBlob` race is covered without
  granting permanent liveness. An upload that is never referenced within the window must
  become reclaimable again.
  - **Add a test:** an `Active`-but-never-referenced upload is reclaimed once the upload
    window elapses. Without it, this hole ships silently and looks like a fix.
- **Validation:** 0.3(c) passes; a presign+confirm-then-delayed-reference sequence
  survives a sweep tick.
- ⚠ **Gate hazard — read before starting.** 0.3(c) runs against
  `NonHydratingBackend::blob_gc_sweep` in `server/stores/blob-conformance`, which is a
  deliberate **byte-for-byte port of today's buggy `S3BlobStore::blob_gc_sweep`**, so
  the scenario can run without Docker. It therefore gates a *copy* of the bug, not the
  bug. Making 0.3(c) green by editing the fake **ships data loss behind a green test**.
  This slice is not done until: (1) the real `S3BlobStore` two-phase grace is asserted
  by a MinIO/testcontainers test (extend `server/s3-blobs/tests/minio_round_trip.rs`),
  and (2) the fake's `blob_gc_sweep` override is updated to match the fixed backend —
  or deleted, if the real test subsumes it. Do not treat the un-ignore of 0.3(c) as
  sufficient evidence.

### 1.4 — `sweep_blobs` hydration/write-through race **[NM]** (#5) — ✅ **DONE** (`20aaba88` + `043df73d`)

> ✅ **The 1.4↔1.3 coupling is RESOLVED (2026-07-16).** 1.4 shipped half-done — its
> `MIN_PHYSICAL_GRACE` floor was a **total no-op on S3**, because `S3BlobStore::blob_gc_sweep`
> bound `_grace` and never read it. **1.3 (`043df73d`) made S3 honor grace, so the floor is now
> genuinely effective on both paths** — verified end-to-end against a real MinIO bucket
> (`s3_min_physical_grace_floor_is_effective_end_to_end` drives the production wiring with
> `--blob-gc-grace 0`; it failed pre-1.3 with *"the S3 path must still never delete on first
> sighting"* and now tombstones first and deletes only on a second, separate sweep). **Observed,
> not traced.** The ⚠️ callout below is kept for the record — read it as history, not status.
>
> **Worth keeping as a lesson:** 1.4 was reported as complete, and its own Validation line was
> satisfiable, while the bug it fixed remained fully open on the backend most likely to be in
> production. A fix that is a no-op one layer down still passes every test written at its own
> layer.

**As shipped.** Bug 1 (hydration race): `Room::hydrated` (`AtomicBool`, set only after every
hydration stage publishes graph/blobs/lineage) now gates the resident-scan exemption; a
still-hydrating room falls through to the cold-room scan. Bug 2 (write-through window):
`MIN_PHYSICAL_GRACE = 1ms` floors the grace handed to `blob_gc_sweep`, so a first sighting only
ever tombstones and deletion waits for a later, separate sweep call.

> ⚠️ **1.4 ↔ 1.3 coupling — bug 2 is still fully open on S3.** `MIN_PHYSICAL_GRACE` protects the
> **`DirPersistence` path only**. `S3BlobStore::blob_gc_sweep` (`server/s3-blobs/src/lib.rs:524`)
> binds `_grace` and never reads it — it is *deliberately* single-phase, on the documented premise
> that "S3 object versions act as their own grace period, and operators who want hard-delete can
> disable versioning." So the floor is a **no-op** there, not partial protection. This is not a
> defect in 1.4: this section's own text already said "…or the S3 path (which 1.3 makes
> grace-honoring)." But it means **1.4's Validation line is satisfied only for `DirPersistence`,
> and Phase 1 is not actually closed on the S3 path until 1.3 ships.** Do not read 1.4's ✅ as
> "the write-through race is fixed."
>
> Two things for 1.3 to absorb: (a) that versioning premise holds *only while versioning is
> enabled*, and the comment actively invites operators to disable it for hard-delete — which
> silently opts out of the only thing standing in for grace; (b) `NonHydratingBackend::blob_gc_sweep`
> (`server/stores/blob-conformance/src/lib.rs:125`) is a byte-for-byte port of the same
> `_grace`-ignoring bug. **That is the 0.3(c) gate hazard already flagged in this plan: it gates a
> *copy* of the bug, so green there is not evidence.**

**Bug 2 shipped untested against its own regression, then was covered.** 0.3(d) reproduces bug 1
only (a persisted-before-process-start node; no live write-through during the test), so the
implementing agent verified bug 2 by hand-tracing `store.rs` alone and said so. Added
`blob_gc_zero_grace_never_deletes_on_first_sighting`, which does **not** try to test the race by
racing — a test spawning threads to hit the window between snapshot and sweep would be flaky and
prove little. It pins the *observable invariant the fix establishes*, which is deterministic and
concurrency-free: with `grace == 0`, the first sweep must return 0 and leave the bytes on disk; a
second, separate sweep deletes. RED-proven against the single line under test.

> **Convention worth reusing:** when a fix closes a race, look for the deterministic invariant the
> fix *establishes* and pin that, rather than trying to reproduce the interleaving. Racing the race
> makes a flaky test that proves little; asserting "never on first sighting" proves exactly the
> property that closes the window.

**Known weakness (assessed, not fixed).** The guarantee is stated in **time** (1ms) but the
invariant actually needed is **two separate sightings**. Nothing in `sweep_blobs` enforces
separateness — it works only because no caller invokes it twice within 1ms (true in production,
where the sweeper interval is seconds; true incidentally in tests, via real disk I/O between
calls). A "first sighting only tombstones" check — consult the tombstone's existence *before*
creating it, and allow same-call deletion only when the tombstone predates this call — would encode
the real invariant instead of using wall-clock as a proxy for call count. Not wrong today; worth
hardening if the sweep cadence ever tightens.

**Original section text follows.**
`nodalmerge:server/server/src/room.rs:644`. A still-hydrating resident room is treated
as "covered" (its persisted scan skipped) while its in-memory graph is empty; and a
blob persisted via write-through after the live snapshot isn't in the set.
- Only skip the persisted scan for **fully-hydrated** resident rooms; a room still
  replaying its background-spawned hydration must fall through to the cold-room scan
  (or the sweep must wait on a hydration barrier).
- Close the write-through window: either take the live snapshot under the same guard
  that admits new blobs, or extend grace/tombstone coverage so a blob written after
  the snapshot is never same-tick deleted regardless of `--blob-gc-grace 0` or the S3
  path (which 1.3 makes grace-honoring).
- **Validation:** 0.3(d) passes, including with `--blob-gc-grace 0`.

### 1.5 — Bound the tree walk (stop the crash) **[NM]** (#9) — ✅ **DONE 2026-07-16** (`ef693f39`)
**As shipped:** iterative heap work-stack **and** a `MAX_TREE_DEPTH = 4096` cap returning
`TreeWalkError::TooDeep`. **Both were required, despite this section's "or" phrasing** — the
work-stack removes the OS-stack dependence (the crash), but alone it would let an arbitrarily deep
chain *succeed slowly* rather than fail closed, which the Validation line requires. If this plan is
reused as a template, tighten that wording. Pre-fix crash independently reproduced against the
recursive `walk_one` (`STATUS_STACK_OVERFLOW`, exit `0xc00000fd`, no output before the abort), so
the RED state is an observation, not an inference. Tests: `server/server/tests/tree_walk_depth.rs`
(50k-deep chain on a 256 KiB-stack thread → `TooDeep`; a 300-deep chain must still succeed, guarding
against an over-aggressive cap).
`nodalmerge:server/server/src/tree_walk.rs:133`. `walk_one` recurses once per
directory level with no depth bound; a chain of distinct single-entry tree blobs
(uploadable via anonymous PUT) overflows the worker stack and aborts the process.
- Convert to an explicit work-stack (iterative) or enforce a hard depth/node-count
  cap, returning `TreeWalkError` (fail-closed, run recorded Failed) instead of
  recursing without limit.
- Track the anonymous-PUT exposure separately (see note) — the depth bound is the
  crash fix and does not depend on auth.
- **Validation:** a synthetic deep-chain fixture that aborts the server today returns
  a clean `Err` after the fix; add it to the GC suite.
- **Note — anonymous PUT (`BlobHttpConfig` default `auth_token: None`). DECIDED
  2026-07-16 (user): auth stays optional.** Rationale, recorded so it isn't relitigated:
  content-addressing already supplies the integrity properties auth is usually reached
  for. PUT verifies the body against the path hash (`blob_http.rs:497`,
  `Hash::of(&body) != path_hash` → 422), so an anonymous writer cannot forge bytes at a
  chosen hash, cannot overwrite an existing blob with different content, and cannot
  poison a hash another room depends on. Oversize bodies are already 413'd by the body
  limit. And unreferenced uploads are reclaimed by the normal mark/sweep (see 1.3) — so
  spam is bounded by the GC interval + grace, not permanent.
  This holds **only while 1.3's inventory union stays time-bounded.** If 1.3 ever grants
  `Active` rows unconditional liveness, anonymous PUT becomes an unbounded disk-growth
  vector and this decision must be revisited. The two are coupled; 1.3 carries the
  matching warning.
  Operators wanting authorization set `auth_token`; deployments needing more can front
  the surface. Do not couple any of this to the crash fix.

### 1.6 — `Failed` is not terminal **[BOTH]** (#30 — found during 0.4) — ✅ **DONE 2026-07-16** (`5664c2b1` + studio `58d9a5c`)
**As shipped:** `Failed` dropped from `TERMINAL_STATUSES`; doc comment corrected; 0.3(e)
un-ignored as written; the vacuous sibling given a unique bootstrap tree (proved non-vacuous by
disabling seed-protection → both tests RED → revert → both green).
**The load-bearing part was NOT the [NM] one-liner.** studio's
`WorkUnitStatusVectorTests.cs` bound `terminal_status_names` to `_` — because **0.4 deleted the
assertion that used it, precisely because that assertion FAILED**, which is how #30 was found.
1.6 fixed the root cause, so the assertion became true and was **restored**
(`Every_terminal_status_has_zero_outgoing_transition_edges`, driven off the vector file, with an
`Assert.NotEmpty` vacuity guard and a `DO NOT DELETE` remark).
**Why it matters:** nodalmerge's own pin is **circular** — 1.6 edited both Rust `TERMINAL_STATUSES`
and the vector's `terminal_status_names`, and `terminal_status_ordinals_match_frozen_constants`
only checks the two mirror each other. Both live in nodalmerge, so it passes for *any* terminal set.
The studio-side edge assertion is the **only non-circular pin** — it validates the set against the
real transition graph. Its RED proof isolates exactly `Failed -> Cancelled`: finding #30 as one line
of test output.
**Generalizable lesson:** when a slice fixes a root cause, check whether an earlier slice *removed*
an assertion because of that root cause. A `_`-bound variable is a scar, not an accident.
`nodalmerge:server/server/src/studio_live_hashes.rs:86-89` documents
`TERMINAL_STATUSES = [Completed, Failed, Merged]` on the stated grounds that
`WorkUnitTransitions.CanTransition` "has zero outgoing edges from these three."
**That is false for `Failed`.** studio's catch-all rule
([WorkUnit.cs:208](../src/NodalMerge.Studio.Contracts/Domain/WorkUnit.cs)) reads
`(_, Cancelled) when from is not Completed and not Merged => true` — the guard
excludes `Completed` and `Merged` but **not** `Failed` — and `Cancelled → Queued`
/ `Cancelled → Executing` are both legal. So `Failed → Cancelled → Queued →
Executing` revives a work unit out of a status the GC retention-ages, which is the
Phase 1 failure mode exactly: the GC concludes a blob is dead when it cannot prove
it. The doc comment's own reasoning is right (it excludes `Cancelled`/`DeadLettered`
*because* they have revival edges) — it just missed that `Failed` has one, one hop
further out.

**Exact mechanism** (both `is_terminal_status` call sites):
- `studio_live_hashes.rs:474` — the loop building the always-retained `active` set
  does `if is_terminal_status(wu.status) { continue; }`. `Cancelled(5)`/`DeadLettered(11)`
  are non-terminal, so their seed snapshots land in `active` and are retained
  **indefinitely**. `Failed(4)` is terminal, so it is skipped.
- `studio_live_hashes.rs:522` — the skipped snapshot falls to Intermediate age-out with
  `base_nanos = wu.updated_at_nanos`, expiring after `RetainIntermediateDays`
  (**default 30**).

**Blast radius is narrower than "always deletes."** Revival *within* 30 days is safe:
the snapshot is still retained, `Failed → Cancelled` makes the unit non-terminal, and
the seed jumps into `active`. The data loss is **revival after day 30** — a human
returns to a failed unit to steer or hand-fix it, and its base snapshot was collected
weeks earlier.

**The intent is inverted today.** `Cancelled` ("I deliberately stopped this") is kept
forever; `Failed` ("this broke, someone may retry with steering") is on a 30-day clock.
`Failed` is the odd one out among the statuses a human resumes from.

**DECIDED 2026-07-16 (user): a failed work unit *is* resumable — take the [NM] route.**
Drop `Failed` from `TERMINAL_STATUSES` so its seeds join `active` alongside
`Cancelled`/`DeadLettered`. 0.3(e) is **un-ignored as written** (no inversion). The
rejected **[ST]** alternative (exclude `Failed` from studio's `(_, Cancelled)` guard,
closing the revival path) would have contradicted that product answer.
- **Accepted consequence:** `Failed` blobs then retain indefinitely. This is not a new
  policy — it is the property `Cancelled`/`DeadLettered` already have; it makes `Failed`
  consistent with its peers rather than exempt. If unbounded retention of failed work
  later matters, the fix is a revival-window policy applied to **all three**, which is a
  larger design question than this plan should absorb — file it separately, do not
  smuggle it into 1.6.
- **Validation:** 0.3(e) passes; the vector from 0.4 still pins ordinals/casing.
- **Note:** the doc comment is wrong *today* regardless of which fix wins — 0.4's
  rework corrects the comment to state the truth and point here.
- **0.3(e) is un-ignored as written** — the [NM] decision above means the gate's
  assertion (nodalmerge retains a `Failed` unit's blobs) is already the correct one.
- **Related pre-existing defect found by 0.3 (not fixed):** `studio_gc.rs`'s
  already-green `active_work_unit_seed_stays_live_past_the_retention_window` is
  **vacuous** — its seed generation shares a tree hash with the Pinned Bootstrap
  generation, so it passes whether or not work-unit-seed protection works at all. 0.3(e)
  deliberately gives its bootstrap generation a unique tree to isolate the mechanism.
  Fix the sibling while doing 1.6.

---

## Phase 2 — Non-hydrating (S3) backend correctness

The S3 seam (`get_blob` returning `None`) silently breaks GC, archive export, and
existence probes. Fix the abstraction rather than each call site: give the blob layer
a real existence check and a hydrating-read path, and make callers that need bytes
either use it or fail **loudly**. Uses the 0.2 harness throughout.

### 2.1 — Existence probe on the blob abstraction **[NM]** — ✅ **DONE 2026-07-16** (`9fd95355`)

**As shipped:** `ExistsAsync` added to `IBlobStoreProvider` as a default interface member
(via `TryGetBlobAsync`), overridden with a real cheap probe on File (`File.Exists`),
HttpRemote (existing HEAD), S3Direct (bucket HEAD on the same presigned URL), Chained,
and — see below — `RemoteBlobLinkAggregator`. HEAD and PUT-idempotency route through it.
The additive-compat guarantee is pinned by a test (`BlobStoreProviderExistsAsyncDefaultTests`),
not just asserted in the ⚠ note.

> **The aggregator was a hole that would have silently voided this slice.**
> `RemoteBlobLinkAggregator` **is** the single `remote` argument `ChainedBlobStoreProvider`
> sees whenever more than one remote link is configured. It implements `IBlobStoreProvider`,
> so without its own override it inherited the compat default — putting the full remote
> download + verify + write-back straight back onto the HEAD path for exactly the three-link
> `local -> relay -> s3-direct` deployment that most needs the probe. **A default interface
> member makes a missing override invisible: it compiles, it returns the right answer, it is
> merely expensive** — which is precisely the failure `ExistsAsync` exists to prevent. This
> is why its tests assert `GetCallCount == 0` rather than asserting the boolean: a
> boolean-only test passes against the hydrating default and proves nothing. Any future
> `IBlobStoreProvider` implementation must be checked for the same hole.

**⚠ compat, second and unlisted in the original slice: HEAD no longer sets `Content-Length`.**
Verified this converges rather than diverges — Rust's `head_blob` has only ever set
`Content-Type` + `ETag`, and **no** vector in `blob-http-surface-vectors.v1.json` asserts a
`Content-Length` (checked, not assumed). Keeping it would mean reading and — for zstd-at-rest
blobs — decompressing the bytes to learn the plaintext length, i.e. paying exactly the cost
the slice removes; a best-effort value would be inconsistent (present for identity, absent
for encoded) and self-defeating. The .NET side setting it was the divergence.

**Accepted behavior change: PUT-idempotency loses an accidental self-healing property.**
The old full read meant a *corrupt* on-disk blob failed the existence check and got silently
re-stored by a repeat PUT. The cheap probe reports it present, so the repeat PUT is a no-op
and the corruption persists. This is **parity with Rust** (`blob_http.rs:515` already uses
`has_blob` here), not a new divergence — but it was undocumented, and it means blob healing
is now exclusively the reconcile sweep's job on both runtimes. Note 3.2 makes a corrupt
identity blob read as *Missing* while `ExistsAsync` still reports it *present* — that
asymmetry is deliberate (existence is not integrity, and a probe that verified would not be
a probe), but it is worth knowing the two answers can disagree for the same hash.

**Original section text follows.**
`nodalmerge:hosts/dotnet/…/WebApplicationExtensions.cs:758`. HEAD and PUT-idempotency
answer existence via a full `TryGetBlobAsync` read; under `ChainedRemote`/`S3Direct`
that triggers a full remote download + BLAKE3 verify + local write-back just to
answer "exists?". Rust already has `has_blob`; .NET's `IBlobStoreProvider` has none.
- Add `ExistsAsync` to `IBlobStoreProvider` with a **default** implemented via
  `TryGetBlobAsync` (so third-party providers don't break — additive), and override
  it on the file/remote/s3 providers with a cheap probe (HEAD / `File.Exists`).
- Route HEAD and PUT-idempotency through `ExistsAsync`.
- **Validation:** 0.2 asserts HEAD does not hydrate; a non-hydrating provider reports
  existing blobs as present, not 404.
- ⚠ compat: additive interface default member — no break for external providers.

### 2.2 — Tree walk on S3 uses a hydrating read or fails loud **[NM]** (#10) — ✅ **DONE 2026-07-16** (`fe56ad34`)

**As shipped.** `walk_tree` resolves tree objects through a new
`BlobPersistence::hydrate_blob` seam. Direct mode does a real bucket GET; Delegate mode (no
bucket credentials, structurally impossible) fails **loud**: `TreeWalkError::Unresolvable` →
*"tree walk could not resolve (DEPLOYMENT CONFIGURATION, not data loss)"* naming the mode and
the remedy, plus `nodalmerge_tree_walk_resolve_failed_total`. Proven not-generic by asserting
the message does **not** contain `"missing tree/blob object"`.

> **Hydrating the tree walk does not violate the offloading policy** — and the code says so
> where the next reader will look. `get_blob`'s "S3 never hydrates" contract exists to keep
> **large file payloads** out of the server process. `walk_tree` only ever fetches **tree
> objects**: v2 `"f"` entries are inserted and never read, only `"d"` entries are pushed and
> fetched, v1 entries are terminal. `get_blob`'s contract is untouched — pinned by asserting
> `get_blob_calls() == 0`, *not* a return value, since a value-only assertion passes against a
> walk that calls `get_blob` and ignores the answer.

> **`get_blob_hydrates()` is a DECLARATION, not an inference — and this is the slice's most
> valuable finding.** The tempting inference — `get_blob == None && has_blob == true` ⟹
> non-hydrating — is **unavailable**: `DirPersistence::has_blob` is pure file existence
> (`blob_path.is_file() || blob_encoded_path.is_file()`, no verify) while `get_blob` returns
> `None` for a **corrupt** blob. Corrupt therefore has the *identical signature* to
> non-hydrating, and inferring would relabel every corrupt file blob as "backend cannot
> hydrate". **Third sighting of "existence is not integrity"** in this plan, after 2.1's
> `ExistsAsync` and 3.2's verify-on-read. Never infer; ask, or match on `HydrateError`.
> *Residual gap, pinned not papered over:* a backend that overrides `get_blob → None` and
> forgets **both** new methods is still silently `Missing`. The type system can't catch it
> without breaking external implementors.

**2.1's lesson generalized, unprompted:** `Composite` must forward **both** new methods.
Inheriting the defaults would report `get_blob_hydrates() == true` for an S3-backed composite,
call `get_blob` (correctly `None`), and reinstate finding #10 **with `S3BlobStore::hydrate_blob`
sitting unused and untested** — same shape as the `RemoteBlobLinkAggregator` hole: compiles,
answers plausibly, silently wrong. Own pin: `composite_forwards_hydration_seam_to_the_blob_half`.

> **The inherited gate was weaker than it looked.** It asserted only `live.is_ok()` — satisfiable
> by a fix returning an **empty live set**, which is *worse* than #10 (GC would then delete live
> blobs). Strengthened to assert the tree and file hashes are actually present. **Ask what would
> catch you being subtly wrong, not just plainly wrong.**

**Not proven:** the metric's **value** is asserted by no test (no precedent in-repo; observing one
needs a process-global recorder — the same singleton shape as the known `archive_adapter` env-race
flake). Verified by compile and code-read only.

**Original section text follows.**
### 2.2 (original) — Tree walk on S3 uses a hydrating read or fails loud **[NM]** (#10)
`nodalmerge:server/server/src/tree_walk.rs:101`. Tree blobs are fetched via
`get_blob`, hardwired `None` on S3, so every new-mode GC run over a cas-tree snapshot
fails closed forever.
- Walk trees through a resolution path that works on S3 (pull via the presigned/origin
  path used elsewhere), or — if a run genuinely cannot resolve a tree — surface a
  distinct, actionable error and metric rather than a generic per-run Failure, so
  "GC never runs on S3" is impossible to miss.
- **Validation:** 0.2 GC-over-cas-tree scenario completes on the non-hydrating stub
  (via the resolve path) instead of erroring every tick.

### 2.3 — Archive export/hydration must not silently drop blobs **[NM]** (#7) — ✅ **DONE 2026-07-16** (`5f34ab26`)

**As shipped.** Export/describe resolve referenced blobs through 2.2's `hydrate_blob`.
`sha256:af1349b9…` (BLAKE3-of-empty) is finding #7's signature and appeared as `left` in every
pre-fix failure. Proven RED against a **real MinIO bucket**, with the Dir half passing in the
same loop — real bucket-vs-disk divergence, not a broken fixture.

> ⚠️ **The plan's "all three call sites want the same policy" is WRONG — `room.rs` wants the
> opposite, and "fixing" it would be a serious regression.** `room.rs:497` is **room-open
> hydration, not export**: resolving there would pull every file payload a room references into
> server memory **on every room open** — exactly what offloading exists to prevent, and what
> `hydrate_blob`'s own contract tells file-byte callers not to do. Its `filter_map` was **correct
> behavior with a silence problem**. It keeps its behavior and gains only visibility (asks
> `get_blob_hydrates()`, logs the skip as a design fact), because `loaded_blobs = 0` was
> indistinguishable from "the blobs are gone". Pinned non-hydrating via `get_blob_calls()`.

**Policy is split by `HydrateError` variant** — 2.2's distinction proved exactly load-bearing:
- **`Unhydratable` → FAIL.** A manifest marker still mismatches every Dir peer: #7's damage,
  annotated. An operator can act on it.
- **`Backend` (transient) → FAIL.** One 503 would otherwise mint a *signed* manifest permanently
  disagreeing with every peer. Retry is cheap; a wrong digest is forever.
- **`Missing` → tolerate, count, warn.** A fact about the **data**, not the backend: every peer
  sees it and excludes the hash, so digests still agree — the very property #7 is about. Failing
  would brick export forever for any room with a dangling `SetBlob`, with no operator remedy, and
  regress Dir behavior #7 doesn't allege is wrong.

**The line: fail on faults an operator can act on; count and continue on facts about the data
every peer shares.** Delegate mode therefore cannot export or describe at all — a hard, actionable
failure naming backend, mode and hash. It *can* still `validate` and do `metadata_only` imports.

> **"Digests match" is never asserted as the property — it is satisfied by two EMPTY digests,
> i.e. the exact bug.** Every digest is pinned against an independently computed expected value,
> plus `assert_ne!` against the empty digest, plus a sanity assert that expected != empty so the
> test cannot go vacuous. `dir == s3` is asserted last, as a restatement.

> ⚠️ **A functional regression the plan doesn't mention, hit and fixed during the slice.**
> `load_archive_from_ref` is shared by **four** entry points and only some want bytes:
> `archive.validate` **never reads `loaded.blobs` at all**, and `metadata_only` import discards
> them. Resolving unconditionally is **invisible on Dir** (a wasted local read) but on S3
> downloads the whole room to throw it away — and on **Delegate turns a working
> `validate`/`metadata_only` import into a hard failure**. Guarded by `BlobBytesNeed` + two tests
> RED against the naive version. **Anyone reviewing a "fix all three `filter_map`s" diff should
> check this specifically: it is silent on every Dir-backed test.**

**Not proven:** the `Backend`-transient path is exercised only via the fake (no real 503 against
MinIO); `Missing`-via-corruption is reasoned, not tested end-to-end (3.2 owns it); metric names
are emitted but asserted by no test — "counted" is verified by code-read only.

**Original section text follows.**
### 2.3 (original) — Archive export/hydration must not silently drop blobs **[NM]** (#7)
`nodalmerge:server/server/src/archive_adapter.rs:702`, `archive_export.rs:201`,
`room.rs:497`. All three `filter_map` away `get_blob` `None`, so S3-backed servers
export archives and compute `blobs_digest` over an empty set with no warning.
- Resolve referenced blobs through the hydrating path (2.2's mechanism); if a
  referenced hash truly cannot be materialized, **fail the export** (or emit an
  explicit, counted warning and a manifest marker) rather than producing a
  silently-empty archive whose digest mismatches a Dir-backed peer.
- **Validation:** 0.2 export scenario either includes the bytes or returns a clear
  error; digest parity between Dir- and S3-backed exports of the same room.

### 2.4 — WS blob-request persistence fallback **[NM]** (latent)
`nodalmerge:server/server/src/ws_handler.rs:1124`. The fallthrough serves only the
room's in-memory store with no `persistence.get_blob` fallback. No shipped client
triggers it today (web/js SDKs always upload), so this is **low priority** — schedule
it after 2.1–2.3, and only if the existence-probe/hydration seam doesn't already
subsume it.
- **Validation:** a peer without direct blob IO can be served a global-CAS blob
  referenced in an already-open room.

---

## Phase 3 — Blob integrity & cross-runtime interop

### 3.1 — .NET accepts unknown-content-size zstd frames **[NM]** (#4) — ✅ **DONE 2026-07-16** (`6cf2c8fd`)

**As shipped:** `TryDecompress` accepts `written <= buffer.Length` and slices to the real length.
The hash-verify is untouched — it lives one level up in `FileBlobStoreProvider.TryGetBlobAsync`,
not in `TryDecompress`, and it is what makes loosening the length check safe. Proven still to
reject genuine corruption (the existing byte-flip-mid-frame test, magic intact, still yields
`Found == false`).

**`GetDecompressedSize` on a headerless frame — measured, not assumed** (ZstdSharp.Port 0.8.1):
returns **131072**, the zstd default window size — an upper bound, *not* zero and *not* an error.
So `written == buffer.Length` was false by construction for every Rust-encoded frame. Worth
recording because "returns 0 / errors on unknown size" is the intuitive guess and it is wrong.

> **The 0.1 empty-payload fixture was never RED.** `rust-encode-all-empty-payload.zst` has a
> 0-byte plaintext, and `GetDecompressedSize` returns 0 for it — matching `buffer.Length` *by
> coincidence*, so it passed even pre-fix. Only the non-empty fixture ever gated this slice. A
> reminder that a fixture existing is not a fixture proving anything.

**Original section text follows.**
`nodalmerge:hosts/dotnet/…/BlobCompression.cs:111`. `TryDecompress` requires
`written == buffer.Length` after sizing from `GetDecompressedSize`, which returns an
**upper bound** for frames without the content-size header — i.e. every frame from
Rust `zstd::stream::encode_all`. Result: all cross-runtime compressed fetches throw
"corrupt zstd frame."
- On unknown/bound size, accept `written <= buffer.Length` and slice, or switch to a
  streaming `DecompressionStream`. Keep the existing hash-verify-after-decompress.
- **Validation:** 0.1 fixture decodes green in both directions.

### 3.2 — Verify-on-read policy for the file provider **[NM]** (#13) — ✅ **DONE 2026-07-16** (code `37374294`; doc half `90ed5b80`, after 3.3 made its premise true)

**Doc half (shipped `90ed5b80`):** `BLOB_HTTP_SURFACE.md` now scopes "corrupt → 404,
never wrong bytes" to identity responses and states the encoded pass-through exemption
in the Content-encoding section (server structurally cannot verify a frame whose hash is
of the plaintext; the client completes integrity after decompress — true for all
reference readers as of 3.3; deliberately no verify-on-encoded-GET option);
`BLOB_STORAGE_LAYOUT.md` §8 mirrors it with a cross-reference. Wording-only — no status
codes, shapes, or vectors changed; the docs now say what both runtimes already do.

**As shipped (code half only):** the identity branch of `FileBlobStoreProvider.TryGetBlobAsync`
hashes on read and, on mismatch, warns and returns `Missing` — matching `store.rs:683`
exactly: no delete, no throw, and **no fall-through to the `.zst` sibling** (Rust `return`s
from the identity branch immediately; so does this). `Missing` is what an absent file already
produces, so the origin answers 404 with no call-site plumbing. RED proven at both levels;
the HTTP-origin test reported the defect verbatim — *"expected 404 for a tampered identity
blob, got 200"*.

~~The doc half is NOT done and must not be marked done.~~ **Done `90ed5b80`** — the
sequencing held: 3.3 (`2e69b9dd`) made "the client verifies after decompress" true first,
then the exemption was documented (see the heading note above). The encoded code path was
left untouched throughout, as decided. Call graph traced to
confirm no second unverified identity-serving path hides in `TryGetEncodedBlobAsync`: the GET
handler serves its result only when `ContentEncoding == "zstd"` and otherwise routes through
`TryGetBlobAsync`.

> **Five pre-existing tests were only green because the read path never checked.**
> `ProviderDurabilityTests`, `Row19AutomatedScenarioHarnessTests` and
> `ProviderHostRestartDurabilityIntegrationTests` each keyed fixture blobs by a **fabricated
> string** (`"sha256:multi-device-a"`, `"sha256:abc"`, `"sha256:room-a-blob"`), and
> `BlobLayoutParityTests`' *runtime* identity-preference test reused a **path-derivation
> vector's placeholder hash**. Verify-on-read turned all five red. Each was fixed by keying on
> the real content hash — assertions got *stronger*, none were weakened; the Rust counterpart
> `both_exist_prefers_identity_at_runtime` already used `Hash::of(&payload)` for this exact
> reason. **Checked before accepting the slice, because the alternative reading would have been
> serious:** if any *production* caller used opaque keys, verify-on-read would strand live data.
> It cannot — the only production callers are the origin's GET/PUT, and PUT rejects a
> non-canonical name (400) and a body disagreeing with its path (422), so every reachable blob
> is keyed by the BLAKE3 hex of its own bytes. **A fabricated-key fixture is a test that has
> opted out of content-addressing; the moment a slice enforces it, they all surface at once.**

**Accepted cost:** identity reads now hash every byte on every read — symmetric with the zstd
path and with Rust, so this is parity, not a .NET-only regression. Correctness over cost, per
the plan's explicit intent.

**Original section text follows.**
`nodalmerge:hosts/dotnet/…/FileBlobStoreProvider.cs:38` and the encoded pass-through
(`get_blob_encoded`, `store.rs:678`). The identity path returns bytes **unverified**
while the zstd path and Rust `get_blob` both verify; the HTTP origin then serves
corrupt bytes as 200.
- Add BLAKE3 verify-on-read to the .NET identity path to match Rust and honor
  `BLOB_HTTP_SURFACE.md` ("corrupt → 404, never wrong bytes").
- For the **encoded** pass-through: **DECIDED 2026-07-16 (user) — document it as
  contractual; do NOT add an opt-in verify flag.**
  *What the pass-through is:* blobs are stored zstd-compressed at rest; on
  `Accept-Encoding: zstd` the server returns the stored frame byte-for-byte rather than
  decode-then-recode (`store.rs:678`). It cannot verify, structurally: the BLAKE3 hash is
  of the **plaintext**, so checking the frame means fully decompressing it — the exact
  work the pass-through exists to avoid. Integrity is completed by the client hashing
  after decompress.
  *Accepted consequence:* the same corrupt blob answers **404** on the identity GET
  (decode → verify → fail) and **200 + corrupt bytes** on the encoded GET. This
  contradicts the letter of `BLOB_HTTP_SURFACE.md`'s "corrupt → 404, never wrong bytes".
  Document "never wrong bytes" as an **end-to-end guarantee the client completes**, not a
  server-side one, and mark the encoded GET explicitly exempt in
  `BLOB_HTTP_SURFACE.md` + `BLOB_STORAGE_LAYOUT.md` §8, on **both** runtimes.
  *Why no flag:* verify-on-encoded-GET is a footgun — it silently costs a full decompress
  per GET to buy a property the client already provides.
- ⚠ **Sequencing: write 3.2's doc change AFTER 3.3 lands.** The decision above rests on
  "the client verifies after decompress" — which 3.3 shows is **not true today** for the
  web/wasm readers (they hash raw presigned bytes with no zstd handling). Documenting the
  contract while its load-bearing premise is false would enshrine a hole. 3.3 makes the
  premise true; then 3.2 documents it.
- **Validation:** a tampered identity file returns 404 on the .NET origin, matching
  Rust.

### 3.3 — s3-direct client zstd vs web/wasm readers **[BOTH→NM]** (#6) — ✅ **DONE 2026-07-16** (`2e69b9dd`)

**As shipped (both steps, in the decided order):**
- **Step 1:** `S3DirectBlobOriginOptions` compression default `Zstd` → `Off`, opt-in
  stays via `S3Direct:Compression = Zstd`; pinned by 4 new options tests (RED: 2 failed
  against the old default). Old-default sightings fixed in the provider doc-comment and
  `BLOB_STORAGE_LAYOUT.md` §8.
- **Step 2:** the ONE real presigned-GET reader — `fetchBlobViaUrl` in `clients/web/sdk.js`
  (pre-scoping was right: `bridge-wasm` never fetches; `clients/sdk-js`'s `doc.js` is a
  generated copy of sdk.js, and its `index.js` runtime client only ingests WS `blob-pack`)
  — now stores via exported `storeFetchedBlobBytes`: **verify-raw-first** (headers cannot
  say whether the runtime auto-decoded; `store_blob_bytes`' BLAKE3 throw is the oracle),
  then decode only on rejection + standard zstd magic, re-verified through the store; a
  corrupt frame surfaces the *original* integrity error. Decoder = `zstd_decompress` wasm
  export on the bridge, pure-Rust `ruzstd =0.8.1` (+84 KiB wasm), one code path for
  Safari/Node/old-browsers instead of feature-detecting `DecompressionStream`/`node:zlib`.
  Decode stays despite the default flip — pre-flip buckets still hold frames (pinned by a
  test comment + the Rust-frame test).
- **Validation met:** Node suite drives the exact shipped function against the REAL wasm
  bridge with the Phase 0 cross-runtime goldens (ZstdSharp + Rust encoder frames, never
  ruzstd's own output; anti-vacuous: pinned plaintext lens + BLAKE3s). RED 3 failed
  pre-fix with the verbatim Safari/Node failure (`blob integrity check failed`);
  independently re-proven by sabotage (magic sniff disabled → same 3 red, restored →
  33/33). Bridge native 4/4 (incl. reject-garbage/truncated); .NET full project 589/589.
- **CI:** new `clients-web-sdk-blob-decode.yml` — the **first workflow running any
  clients/ test at all** (bridge-native + wasm-pack build + Node suite vs the real
  bridge); `blob-layout-parity.yml` gains the options-test step + paths.
- **Corrected premise:** the wasm `pkg/` dirs are NOT checked in (wasm-pack emits a
  self-gitignore); they were mutually stale local artifacts (`clients/web/pkg` ~6 weeks
  behind). All three rebuilt in sync; CI drift risk now covered by the new workflow,
  local pack-time staleness filed to Phase 8-adjacent backlog.
- **Follow-ups filed:** `docs/sdk.md:10` + `bridge-wasm/README.md:12` stale build-command
  paths (`bridge` vs `bridge-wasm`); deprecated wasm-bindgen `ready(bytes)` init shape;
  pre-existing `unused import: TextOp` warning in bridge lib.rs; `nuget-build-push.yml`
  `wrapper-smoke` doesn't build the bridge crate it embeds (partially mitigated by the
  new workflow).

**Original section text follows.**
`nodalmerge:hosts/dotnet/…/S3DirectBlobOriginOptions.cs:86` defaults client-side zstd
ON; the bucket stores a zstd frame with `Content-Encoding: zstd` object metadata,
but `clients/web/sdk.js` and `clients/bridge-wasm` hash the raw presigned-GET bytes
with no zstd handling. Correctness depends on the browser/runtime transparently
decoding zstd (Chrome 123+/FF 126+ do; Safari and Node/undici do not).
- **DECIDED 2026-07-16 (user): do BOTH, in order — not either/or.**
  1. **Default s3-direct compression OFF** now (`S3DirectBlobOriginOptions.cs:86`), making
     compression explicitly opt-in. Low-risk, least code, stops correctness depending on
     which browser the user happens to run.
  2. **Then teach the SDK readers to decode** `Content-Encoding: zstd` before hashing
     (`clients/web/sdk.js`, `clients/bridge-wasm`). This is **not** an optional
     enhancement: nodalmerge must not ship a server option its own SDK cannot read, and
     correctness must not rest on transparent browser decode (Chrome 123+/FF 126+ do;
     Safari and Node/undici do not). Until (2) lands, opting compression ON is a
     documented footgun.
- **Retagged [BOTH] → [NM]** (2026-07-16): the plan assumed an `[ST]` half "wherever
  studio surfaces the web SDK". **Studio does not consume the web SDK** — verified, the
  only references to `sdk.js`/`bridge-wasm`/`clients/web` anywhere in nodalmerge-studio
  are this plan and the README; there is no code. Nothing to do studio-side.
- Step (2) is what makes 3.2's "the client verifies after decompress" premise true, so
  3.2's doc change depends on it — see 3.2's sequencing note.
- **Validation:** a Safari/Node-style fetch (no auto-decode) round-trips an s3-direct
  blob and passes the integrity check.

### 3.4 — Content-Encoding negotiation correctness **[NM]**
- Honor `q=0` in `Accept-Encoding` on both hosts
  (`nodalmerge:server/server/src/blob_http.rs:457`,
  `hosts/dotnet/…/WebApplicationExtensions.cs:725`) so `zstd;q=0` means "don't send
  zstd."
- Reject PUT carrying `Content-Encoding` with **415** on the .NET host to match Rust
  (`blob_http.rs` returns 415; .NET currently ignores it → 422 or stores a frame as
  identity). The contract makes this a **MAY**, so this is a parity/robustness fix,
  not a mandated-status fix — implement it and add a parity vector so the two hosts
  stay aligned.
- **Validation:** parity vectors for `q=0` and PUT-with-`Content-Encoding` pass on
  both hosts.

---

## Phase 4 — HTTP-surface robustness & backward compatibility

### 4.1 — Restore `/sync/blob-url` backward compat **[NM]** ⚠ compat (#12)
`nodalmerge:hosts/dotnet/…/WebApplicationExtensions.cs:600`. The legacy alias (shipped
on `main` in 0.2.0) silently changed vs `main`: `expiresAt` (unix seconds) →
`expiresAtUtc` (ISO-8601), no-backend `404` → `501`, plus new `400`/`401` from
`ValidateBlobRequest` where `main` accepted any non-empty hash anonymously. The
in-code comment claims "same response shape and status codes" and the CHANGELOG says
"no breaking changes" — both untrue.
- Restore the `main` behavior **on the legacy route only**: emit `expiresAt` (unix
  seconds), return `404` for no-backend, and keep it anonymous/loose-hash. The new
  `/blobs/**` routes keep the new shape.
- Fix the misleading comment and the CHANGELOG entry.
- **Validation:** a golden test asserts the legacy route matches `main`'s response
  byte-shape and status codes; the new routes keep their frozen shape.

### 4.2 — `put_blob` durability truthfulness **[NM]** (#8)
`nodalmerge:server/server/src/blob_http.rs:519`. The handler upserts the hash `Active`
in the inventory **before** the write, calls the infallible `persist_blob` (Dir and S3
both log-and-swallow failures), and returns `201` unconditionally.
- Make `persist_blob` **fallible** (return `Result`) at the trait level; propagate
  write failure to the handler, which returns `5xx` and does **not** mark the
  inventory `Active` unless the write is confirmed durable. Reorder so inventory
  `Active` follows a confirmed write.
- **Validation:** a disk-full/S3-failure PUT returns an error status and leaves no
  `Active` inventory row; a later GET/HEAD is consistent with that.
- ⚠ compat: server-internal trait change (`persist_blob` signature) — not on the wire,
  not a published .NET API. The wire change is `201`→`5xx` **only on genuine failure**,
  which is strictly more correct.

### 4.3 — Options validation & URL resolution **[NM]**
- **`BlobHttpOptions.Validate()`** (`hosts/dotnet/…/BlobHttpOptions.cs:24`): reject
  `MaxBlobBytes <= 0` at startup. Today a negative value throws
  `ArgumentOutOfRangeException` per chunked PUT (500) and zero silently 413s
  everything; sibling options all fail fast — make this one match.
- **Base-URL path resolution** (`hosts/dotnet/…/HttpRemoteBlobStoreProvider.cs:246`,
  `S3DirectBlobStoreProvider.cs:252/415`): requests use root-relative URIs
  (`/blobs/{hash}`) that discard any path component of a configured `BaseUrl`
  (e.g. a reverse-proxy prefix `https://host/nodalmerge`). Build request URIs
  relative to the full base path, or validate at startup that `BaseUrl` has no path
  segment.
- **`S3DelegatedBlobOptions` stale-key guard** (`S3DelegatedBlobOptions.cs:62`):
  `PutPath`/`GetPath` were removed; old config still setting them is silently ignored
  and `PresignPath` defaults to `/v1/blobs/presign`, degrading a mis-migrated deploy
  to the WS fallback with only a warning. Add a startup guard that **warns loudly**
  (per principle 3 — don't hard-fail a running upgrade) when the removed keys are
  present, naming the migration.
- **Validation:** unit tests for each: negative/zero `MaxBlobBytes` rejected at
  startup; a path-prefixed `BaseUrl` reaches the right path; stale delegate keys emit
  a warning.

### 4.4 — Capability-profile hard-ceiling clamp **[NM]** ⚠ compat (#15)
`nodalmerge:server/capability-profile/src/lib.rs:388`. The mint/validate safety caps
(128 caps, 8 KiB flattened, depth 16, 64 edges) moved from unconditional constants to
profile-file-supplied `limits` with no clamp — a `limits` block that was an inert
unknown field on `main` now lifts the RoomToken guard and unbounds the DFS.
- Clamp `resolved_limits` to the historical hard ceilings: a profile may lower a
  limit but never raise it past the built-in maximum. Legit profiles (which never set
  `limits`) are unaffected; the previously-guaranteed ceiling is restored.
- **Validation:** a profile requesting oversized limits is clamped; DFS depth stays
  bounded; a no-`limits` profile behaves exactly as on `main`.

---

## Phase 5 — Migration safety

### 5.1 — Rust migration marker only on full success **[NM]**
`nodalmerge:server/server/src/store.rs:934`. `migrate_legacy_blob_layout` writes the
`.layout-v2` marker unconditionally even when room dirs were skipped (read-dir error,
mkdir failure, copy-fallback failure), permanently orphaning those legacy blobs
(readers only consult `blobs/blake3/`).
- Track whether any room/entry was skipped; write the marker only on a fully clean
  pass, otherwise leave it absent so the next startup retries. Log skipped entries.
- **Validation:** a migration with an injected transient skip does **not** write the
  marker and re-migrates on the next run.

### 5.2 — .NET migration & write-path hygiene **[NM]**
`nodalmerge:hosts/dotnet/…/FileBlobStoreProvider.cs`:
- **Re-quarantine loop (`:203`):** exclude `.migration-skipped` (and dot-dirs) from
  the `*.blob` enumeration so quarantined files aren't re-discovered and re-quarantined
  under ever-growing GUID names (matches the Rust migration's skip).
- **`.tmp` leak (`:132`):** wrap the temp-then-move in `try/finally` (or a
  best-effort cleanup) so a cancelled/failed write doesn't leak `.{hash}.{guid}.tmp`
  under `blake3/` — which GC classifies as foreign and never reclaims.
- **`SanitizeHash` (`:173`) — latent:** mangling non-canonical hashes into storable
  filenames diverges from Rust's strict `hash_from_hex` and creates GC-invisible,
  Rust-unreadable entries. No current caller passes non-canonical hashes, so this is
  a **reject-don't-sanitize** hardening: validate canonical hex at the provider layer
  and reject, matching `store.rs`. Low urgency; do it while in the file.
- **Validation:** a crash-before-marker restart doesn't grow quarantine names; a
  cancelled PUT leaves no `.tmp`; a non-canonical hash is rejected, not stored.

---

## Phase 6 — Runtime, async-safety & efficiency

> ⚠️ **6.1 is no longer comfortably deferrable (escalated by 2.2 and 2.3, 2026-07-16).** The plan
> already called it "near-P1: the wedge risk"; Phase 2 made it worse in two steps and it should now be
> scheduled ahead of the rest of Phase 6, and arguably ahead of Phases 4–5:
> - **2.2** routed the tree walk through the bridge — per tree object, per GC tick. Small JSON, but a
>   repo with N directories now builds N tokio runtimes and spawns N threads on **every sweep**, serially.
> - **2.3** routed archive export through it — **full file payloads**, on an operator-triggered path. A
>   5,000-blob room = 5,000 threads + 5,000 runtimes, serially, and **every bridge site is timeout-less**,
>   so a hung bucket wedges a tokio worker forever.
>
> Neither slice added a *new* bridge (still 9 sites, existing pattern reused) — they changed the **traffic**
> through it from per-request to per-blob-per-sweep and per-export. Nothing here is a defect in 2.2/2.3;
> both were correct and bucket-proven. But "defer 6.1" was priced against the old traffic.
>
> **Resolved 2026-07-16:** 6.1 shipped (`ac505724`) — one shared runtime, all waits bounded, timeouts
> classify as backend errors. The *wedge* is gone. What remains of the escalation is the bounded-but-
> still-blocking caller thread (6.2, now unblocked) and sweep/export I/O shape (6.3 + the export-RSS
> follow-up).

Confirmed performance/robustness debt. ~~6.1 is the most impactful (a hung delegate can
wedge a worker indefinitely) — treat it as near-P1.~~ 6.1 done; 6.2 is next-most-impactful.

### 6.1 — s3-blobs runtime bridge + timeouts **[NM]** (#11) — ✅ **DONE 2026-07-16** (`ac505724`)
`nodalmerge:server/s3-blobs/src/lib.rs:289` (8 sites — actual count on dispatch: **9**,
the plan predated S5.3's `head_key`/`delete_key`). Each S3 op spawned a fresh OS
thread + a new multi-threaded tokio `Runtime` and blocked on a **timeout-less**
`mpsc::recv()`; the delegate `reqwest::Client` had no timeout (it was NOT rebuilt
per-call as suspected — built once in `new()` and cloned, so pooling was fine; only
timeouts were missing).
- **Shipped:** all 9 sites route through one `block_on_shared_runtime` helper (name per
  7.3, which this pre-absorbs) backed by a **process-wide `OnceLock<Runtime>`**, not a
  store-owned one — dropping a `Runtime` inside an async context panics, and
  `S3BlobStore` is constructed/dropped freely (server-s3 builds a second instance for
  `S3BlobObjectStore`). 2 worker threads named `nm-s3-bridge`. Timeouts applied
  **3-deep** (object_store `ClientOptions`, reqwest builder, bridge
  `tokio::time::timeout` + `recv_timeout(inner + 15s)`), knobs on `S3BlobStoreConfig`:
  `op_timeout` 30s / `connect_timeout` 10s / **`sweep_timeout` 15min** (deviation, and
  correct: `blob_gc_sweep` is ONE bridge call wrapping a whole LIST+PUT+DELETE batch,
  so `op_timeout` would abort every legitimate large-bucket sweep; each request inside
  is still individually bounded by `op_timeout`). server-s3 got 3 matching env vars per
  its env-only convention; no CLI flag duplication touched.
- **Classification (the load-bearing part):** a timeout is a *backend* error, never
  absence — `hydrate_blob`→`HydrateError::Backend`, GC `head_key`/`delete_key`→
  `S3BlobError::Timeout`→`GcError::Backend`, aborted sweep ≡ crashed sweep (two-phase
  tombstones tolerate it, nothing deletes without an aged persisted tombstone).
  `has_blob() -> bool` cannot carry an error; `false` is conservative **for its actual
  callers** (blob_http HEAD-404/PUT-dedupe, where spurious-absent costs a WS fallback
  and spurious-present would 200 a HEAD it can't serve) and GC liveness never reads it
  — verified + documented at the method. No trait signatures changed, no frozen
  contracts touched.
- **Validation done:** `tests/bridge_timeouts.rs` (7 tests, stalled in-test
  `TcpListener`): 0/7 pre-fix — every op still running at a 10s watchdog — 7/7 after,
  each returning via a 750ms configured timeout. Independently re-proven by sabotage
  (timeouts stripped at all 3 layers → 0/7 at 10.03s; restored → 7/7). Runtime reuse
  pinned green-side by `bridge_reuses_one_shared_runtime_across_ops`
  (`BRIDGE_RUNTIMES_BUILT == 1` across stores/auth modes; pre-fix N-runtimes shown by
  code shape, honestly reported as structural). MinIO 8/8 under `REQUIRE_DOCKER`;
  blob_gc 12, conformance 13, delegate_gc 2, both binaries build clean.
- **CI:** `blob-layout-parity.yml` gained the `bridge_timeouts` step + both `paths:`
  entries (Docker-free by design, so it lives there, not in blob-s3-gc-minio.yml —
  whose `server/s3-blobs/**` glob already picks up the change).
- **Follow-ups filed (not fixed here):** (1) the bridge still *blocks the calling
  thread* up to `op_timeout + 15s` — bounded now, but blob_http handlers still park
  tokio workers; that IS 6.2, now unblocked. (2) blob_http PUT on a timing-out
  backend: `has_blob`→false, `persist_blob` times out warn-only, handler still returns
  **201 with nothing durably stored** — pre-existing void-`persist_blob` shape, same
  family as 4.2's durability-truthfulness slice (fix it there or in 6.2). (3)
  object_store's default `RetryConfig` still applies inside the sweep; per-request
  bounds hold but retries multiply worst-case duration toward `sweep_timeout` — tune in
  6.3.

### 6.2 — Move blocking blob work off async workers **[NM]** — ✅ **DONE 2026-07-16** (`c76edf9d`)
`nodalmerge:server/server/src/blob_http.rs:234`. GET/PUT handlers did blocking
`std::fs` I/O, up-to-64 MiB BLAKE3 hashing, and zstd encode/decode directly on tokio
workers — plus, post-6.1, S3 bridge calls that block bounded (`op_timeout + 15s`) but
still park the worker.
- **Shipped:** one `off_worker` helper (`spawn_blocking` + JoinError→logged 500, never a
  hang or silent success); every store-touching handler tail (GET/HEAD reads+verify,
  /url presigns incl. the Delegate-mode HTTP round trip, /uploaded bucket HEAD + SQLite
  upsert, PUT body-hash + dedupe + persist + inventory mark) runs in ONE blocking
  closure per request, preserving in-request ordering exactly; the cheap pure prefix
  (canonical check, auth, parsing) stays async. Sync `BlobPersistence` trait untouched —
  the async-trait conversion was rejected as blast radius for zero scheduling benefit.
  PUT's known-lossy 201-on-backend-timeout deliberately preserved (4.2's job).
- **Validation met (made deterministic):** `tests/blob_http_offload.rs` — 2-worker
  runtime, 6 concurrent requests on a 500ms-blocking store fake, 200ms heartbeat bound,
  measurement gated on ≥2 ops in flight so the RED is spawn-order-independent. Pre-fix
  4/5 failed (heartbeats 1.5–3.0s, 7–15× over bound) — reproduced independently by
  stash-revert of only blob_http.rs; post-fix 5/5 in ~1s. Panic-in-closure→500 is
  behavioral too; the /uploaded green-side guard is honestly labeled not-a-RED.
- **Known pre-existing flake, NOT this slice:**
  `archive_profile_002_object_manifest_parity_reports_p50_p95` fails under the parallel
  full-lib run and passes solo — verified to fail identically on the pre-6.2 tree
  (stash-and-rerun). A timing/percentile test that is load-sensitive; worth a
  follow-up de-flake (serial marker or wider percentile budget) but do not paper over
  it inside an unrelated slice.
- **Filed, not fixed — the same sync-from-async family elsewhere:** WS handlers
  (`ws_handler.rs:1064` persist_blob on the receive loop — which also base64-decodes and
  `Hash::of`s inline — `:1100`/`:1171` presign resolutions, `:1220` verify_uploaded); GC
  (`room.rs:920` blob_gc_sweep inside async sweep_blobs, `room.rs:643` get_blob in
  live-set collection — 6.3/6.4 territory); archive control-plane
  (`archive_adapter.rs:320`/`:1360`, `archive_export.rs:269`/`:438`). The WS receive-loop
  sites pair naturally with 6.5 (inbound-pack observer off the hot path).
- **CI:** `blob-layout-parity.yml` gained the `blob_http_offload` step + both `paths:`
  entries (Docker-free, bridge_timeouts-style comment).

### 6.3 — GC batch/bulk I/O **[NM]**
- Batch the mark-pass upserts (`nodalmerge:server/server/src/gc_store.rs:223`) into a
  single transaction/prepared statement instead of one autocommit per hash (100k
  hashes = 100k txns today).
- Parallelize/bulk the S3 sweep deletes (`s3-blobs:556`) via `delete_stream` or
  `buffer_unordered`, instead of one awaited delete per object (N+1).
- **Validation:** mark-pass and sweep timings drop by an order of magnitude on a
  100k-object fixture.

### 6.4 — GC recomputation caching **[NM]**
- `sweep_blobs` reloads every cold room's full node history per tick
  (`room.rs:647`); cache the extracted hash set keyed by max seq, re-scanning only
  rooms whose seq advanced (or add a SQL projection returning only blob hashes).
- `resolve_room_studio_map` replays full history into a fresh `StateGraph` per GC run
  (`studio_live_hashes.rs:624`); cache keyed by last-applied seq and run under
  `spawn_blocking`.
- `resolve_snapshot_into` allocates a fresh visited set per retained snapshot
  (`studio_live_hashes.rs:548`), re-walking shared subtrees; thread one shared
  visited/live set across all snapshots in a room (safe — the visited set **is** the
  live output set).
- **Validation:** GC tick cost on a many-cold-room / many-shared-subtree fixture is
  sub-linear in redundant work.

### 6.5 — Inbound-pack observer off the hot path **[BOTH]**
`nodalmerge:hosts/dotnet/…/RuntimeWebSocketLoopRunner.cs:273` awaits the
`IInboundPackObserver` hook **inline** in the WS receive loop; the try/catch isolates
exceptions but not latency, so a slow/hung observer stalls all further frames on that
connection. The one shipped observer, studio's
[StudioInboundPackObserver](../src/NodalMerge.Studio.Host/StudioInboundPackObserver.cs),
does real per-pack work (live-map replay + refresh of every `IRehydratable`).
- **[NM]:** dispatch observers off the receive loop — a bounded queue/channel with its
  own worker, or `Task.Run` with backpressure — so observer latency never blocks frame
  processing/relay; give the hook its own timeout independent of connection teardown.
- **[ST]:** make `StudioInboundPackObserver` resilient to being called off-loop
  (idempotent, its own cancellation/timeout) and consider debouncing the
  refresh-every-`IRehydratable` behavior.
- **Validation:** a deliberately slow observer no longer degrades inbound throughput
  on the connection; relay to other peers is unaffected.

---

## Phase 7 — Duplication & altitude cleanup

Quality debt with no behavior change; do last, once the surface is correct. Each slice
must be behavior-preserving (guard with the tests from earlier phases).

### 7.1 — Shared server CLI/bootstrap module **[NM]**
`nodalmerge:server/server-s3/src/main.rs:423` duplicates ~310 lines of flag parsers +
~75 lines of GC/blob wiring from `server/server/src/main.rs` (with a "keep in sync by
hand" comment). Extract a `cli`/`bootstrap` module in the `nodalmerge-server` library
that all three binaries (`server`, `server-s3`, `dev-server`) call.
- **Validation:** identical CLI behavior across binaries; the duplicated helpers exist
  once.

### 7.2 — One retry/circuit-breaker component **[NM]**
The retry-classification + circuit-breaker is copied three times in
`NodalMerge.Host.Composition` (`S3DirectBlobStoreProvider.cs:526`,
`HttpRemoteBlobStoreProvider.cs`, `S3DelegatedBlobUrlResolverProvider.cs`) and already
drifts (null-on-exhaustion vs throw; a 501 cooldown in one copy). Extract one
options-driven component; map its "exhausted" outcome per caller.
- **Validation:** all three providers use it; behavior matches each provider's current
  contract (pin with tests first).

### 7.3 — Shared low-level helpers **[NM]**
- One proleptic-Gregorian date module for the two copies in
  `studio_live_hashes.rs:272` and `blob_http.rs:421` (add the round-trip test the
  split copies lack).
- One canonical-hash helper for `WebApplicationExtensions.cs:891` vs
  `FileBlobGcCoordinator.cs:185` (natural home: `NodalMerge.Host.Abstractions`).
- One `s3_object_key` derivation shared by `key_for` and `s3_key_scheme`
  (`s3-blobs:276`/`:724`).
- One `block_on_shared_runtime` bridge helper for the s3-blobs sites (folds into 6.1).
- **Validation:** each duplicate collapses to a single definition; existing tests
  green.

### 7.4 — Freeze the delegate room-id placeholder **[NM]**
`nodalmerge:server/server/src/blob_http.rs:73` sends `room="_global"` while the .NET
host sends `room="default", ns="blobs"` into the same delegate presign protocol; the
contract makes room metadata-only (never a key input), so bytes are unaffected, but a
delegate keying policy/quota/audit on room sees different per-host values. Pick one
placeholder, define it as a frozen contract constant (doc + the vector slot from 0.4),
and assert both runtimes against it.
- **Validation:** both hosts emit the same room/namespace on the room-agnostic routes;
  a vector pins it.

### 7.5 — Pluggable `LiveHashSource` (altitude) **[NM]**
`nodalmerge:server/server/src/gc_service.rs:149` hard-wires the studio-domain
live-hash source (971 lines of product-specific parsing) into the generic server GC
service, even though `GcCoordinator` is already generic over `LiveHashSource`. Thread a
`LiveHashSource` factory through `GcServiceConfig`/`spawn_gc_sweeper` (the way
`BlobObjectStore` is already threaded) and move the studio source toward the
composition layer. This is the clean home for the union built in 1.2 and keeps
studio-specifics out of the generic crate — the right-altitude version of principle 2.
- **Validation:** the generic server crate builds/tests without the studio source; the
  studio composition supplies it; GC behavior unchanged.

---

## Phase 8 — CI coverage (DEFERRED) **[NM]**

**Status: deferred by decision (user, 2026-07-16). Not scheduled, not blocking any slice.**
Promoted from a follow-up bullet to a phase because it is a body of work with its own design
question, not a chore — but deliberately parked at the bottom rather than inserted into the
critical path.

**The problem in one line:** this plan's slices have hit CI coverage gaps **four separate
times**, escalating each time, and the audit that the fourth prompted found that the failure
mode is not stale filters — it is **steps that do not exist at all**.

1. `studio_gc.rs` — test file existed, **no step ran it** anywhere (fixed, `5664c2b1`).
2. `tree_walk.rs` — step existed, **`paths:` omitted the source file it guards** (fixed, `ef693f39`).
3. `ZstdInteropTests.cs` — **both at once**: listed in `paths:` but no step ran it, *and*
   `BlobCompression.cs` — the file 3.1 actually fixed — wasn't in the filter at all. The interop
   contract was enforced only from the Rust side, and a Rust test can never catch a .NET decoder
   regression.
4. **The audit: ~15 Rust integration-test files and ~34 .NET test classes run in ZERO workflows.**
   Includes `server/s3-blobs/tests/minio_round_trip.rs` (**the very test 1.3 is blocked on**),
   `persistence.rs`, `idle_eviction.rs`, `rate_limit.rs`, `token_expiry.rs`, `metrics_endpoint.rs`,
   and — sharpest — `blob_layout_vectors_v3.rs`, a *sibling* of the already-covered
   `blob_layout_vectors.rs`. Several cross-runtime parity contracts are pinned from **one runtime
   only**: `SpecAuthVectorsTests.cs`↔`spec_auth_vectors.rs` and
   `BranchForkVectorsTests.cs`↔`branch_fork_vectors.rs` (neither side runs anywhere);
   `QueryMaterializationVectorsTests.cs` (Rust covered, .NET mirror never runs) — the exact
   one-directional shape that let finding #4 live.

Some are plausibly uncovered for the same structural reason Postgres/Mongo were (Docker / MinIO /
native FFI that CI doesn't set up) — **that is a reason to fix the setup, not evidence the gap is
benign.** Phase 2/3 added several more suites that had run in no workflow
(`HttpRemoteBlobStoreProviderTests`, `S3DirectBlobStoreProviderTests`, `ChainedBlobStoreProviderTests`,
`RemoteBlobLinkAggregatorTests`) — each closed only because a slice happened to touch it.

**Until this phase lands, "CI is green" carries much less information than it appears to.**

### 8.0 — The stub convention (IN FORCE NOW, not deferred)

Deferring the phase must not mean slices quietly skip CI wiring in the meantime. So:

- **Every slice still adds its step and its `paths:` entries** — that stays part of the
  definition of done (see Conventions).
- **When the right home for a step is unclear, or it needs CI setup that does not exist**
  (Docker, MinIO, native FFI): **add the step where you believe it should run, with a comment
  marking it as needing follow-up, and say so in the slice's report.** A stub in roughly the
  right place is the wanted outcome; silent omission is not. Phase 8 sorts them out.
- Rationale: a stub is *discoverable* and *movable*. An absent step is neither — which is the
  entire reason this phase exists.

### 8.1 — Meta-test: every test file is named by some workflow **[NM]**
The mechanical fixes considered before (drop filters on the cheap correctness workflows, or
derive them) address staleness. The audit says the **load-bearing** fix is different, because the
failure is *absence*: a meta-test asserting every `server/**/tests/*.rs` and every .NET test class
is named by at least one workflow step, failing on any that isn't. An allow-list is fine for
genuinely-excluded suites — the point is that exclusion becomes **explicit and reviewed** instead
of accidental.
- **Validation:** the meta-test, run against the tree as of this phase's start, reproduces the
  ~15 + ~34 list. That list *is* its RED.

### 8.2 — Close the audit's backlog **[NM]**
Wire in what 8.1 surfaces, including the CI setup (Docker/MinIO/FFI) that some suites need.
- ✅ **`minio_round_trip.rs` is DONE — 1.3 absorbed it** (`043df73d`, new `blob-s3-gc-minio.yml`,
  fail-open closed with `NODALMERGE_REQUIRE_DOCKER=1`). Pulled forward deliberately rather than
  left to this phase, because 1.3's correctness rested on it. **That is the pattern for the rest
  of this backlog: a suite whose slice needs it gets wired by that slice, not by Phase 8.**
  Phase 8 is for what no slice happens to touch — which is most of it.
  See the Sequencing summary.

### 8.3 — Both-directions rule for parity contracts **[NM]**
A cross-runtime contract pinned from one runtime only is not pinned. `QueryMaterializationVectorsTests`
is the live example, and finding #4 is the proof of what it costs. Make "both sides run, or the
contract is not enforced" an explicit, checked rule rather than a habit.

---

## Sequencing summary

1. **Phase 0** unblocks everything (RED tests + harnesses).
2. **Phase 1** is P0 — schedule immediately after 0.3; each slice ships behind its
   0.3 gate.
3. **Phase 2** depends on 0.2 and shares the existence/hydration seam — 2.1 first
   (others build on it). ✅ 2.1 shipped, so **2.2/2.3/2.4 are unblocked**.
4. **Phases 3–5** are independent of each other; parallelizable across contributors.
   **Within Phase 3 there is one ordering constraint: 3.2's doc half must follow 3.3.**
5. **Phase 6** — start **6.1** early (near-P1: the wedge risk); the rest after the
   surface is correct.
6. **Phase 7** last, behavior-preserving, guarded by earlier tests. 7.5 absorbs the
   1.2 composition seam; 7.3's bridge helper absorbs 6.1's.
7. **Phase 8** is deferred and unscheduled — but **8.0's stub convention applies from now on**,
   and **8.2's MinIO wiring should be pulled forward into 1.3**, not left to the phase.

**Next up (as of 2026-07-16), in order of readiness:**
- **6.1** — ⚠️ **now the highest-priority remaining item.** Already "near-P1: the wedge risk"; 2.2
  and 2.3 changed the traffic through the timeout-less runtime bridge from per-request to
  per-blob-per-sweep (GC) and full-file-payload-per-export. See the callout at Phase 6.
- **3.3** — unblocks 3.2's doc half, and is required parity work in its own right. Also keeps the
  `.zst`/`key_for` asymmetry latent.
- **Phase 4 / Phase 5** — independent, unblocked.
- **2.4** — the last Phase 2 slice; explicitly low-priority/latent, and the plan says to check
  whether 2.1–2.3's seam already subsumes it before doing it at all.
- **1.2** — the last Phase 1 slice, still blocked on 7.5's composition seam.

## Conventions settled during Phase 0

- **RED tests** are committed **gated**, asserting the correct post-fix behavior:
  Rust `#[ignore = "RED: fails until slice N.N — see plans/blob-cas-remediation.md"]`,
  C# `[Fact(Skip = "RED: ...")]`. The fixing slice removes the gate **and** adds the
  CI step. Rationale: CI stays green and the gate is real runnable code, not a comment.
- **CI is not `--workspace`.** `.github/workflows/*.yml` name every test file as its
  own step (`cargo test -p <crate> --test <file>` / `dotnet test --filter
  "FullyQualifiedName~<Class>"`). **A new test file is invisible to CI unless a step is
  added.** Adding the step is part of every slice's definition of done.
- ⚠ **A step existing is not the same as a step running** (learned the hard way in Phase 1,
  2026-07-16). Two failure modes this convention originally missed — **check both, every slice**:
  1. **No step at all.** `server/server/tests/studio_gc.rs` shipped with **no step in any
     workflow** — so its Phase 0 RED gates for 1.2/1.6 were not "skipped until their slice", they
     had *never executed in CI*. Fixed in 1.6 (`studio-live-hashes-parity.yml`). When Phase 0 says
     a gate is committed, verify the file it lives in actually runs.
  2. **The workflow is `paths:`-filtered.** `blob-layout-parity.yml` triggers only on a hand-listed
     set of files. It listed `store.rs`/`room.rs`/`blob_http.rs` but **not** `tree_walk.rs` — so
     1.5's fix would have landed with its own suite never firing. Fixed in 1.5. **Adding a step is
     half the job; the trigger must also list the source files the suite guards.**
  3. **A step can exist, run, and still prove nothing.** Wiring Postgres/Mongo in would have gone
     green while testing nothing about finding #1 — the suite had no scenario for the property
     (`known_room_ids`), and `run_all(store, room_id)` took a *single* room, so it structurally
     could not test multi-room enumeration. Both round-trip tests also fail **open** (skip on
     missing Docker → reported PASS). **Check the suite asserts the property before wiring it in
     to guard that property**, and that it cannot no-op.
  - **Root cause, unfixed:** these filters enumerate implementation files *by hand*, so every
    suite's real coverage depends on someone remembering each file it transitively exercises. Same
    shape as the missing `.gitattributes` (fixed `10723954`): correct by luck, silent when wrong.
    **Now tracked as Phase 8 (deferred)** — and note **8.0's stub convention is in force now**: if
    a step's home is unclear or needs CI setup that doesn't exist, add it where you believe it
    belongs with a follow-up comment and report it. Never omit it silently.
- **Vectors** live flat in `nodalmerge:engine/commands/*.v1.json`; binary goldens in
  `engine/commands/fixtures/`. Rust consumes via `include_str!` → collect failures into
  a `Vec<String>` → one `assert!`. Prefer an **inline** `#[cfg(test)]` vector test over
  an external `tests/` crate when the alternative is exporting test-only `pub` surface
  (precedent: `s3-blobs/src/lib.rs:775`).
- **studio's CI** checks out nodalmerge as a sibling at `ref: blobExpansion`
  (`TODO(blobExpansion-merge):`) because the shared vectors only exist on that branch.
  **Flip to `main` when `blobExpansion` merges** — this is on the merge checklist.

## Open decisions (resolve before the relevant slice)

- ~~**1.5 note** — require blob-PUT auth by default?~~ **RESOLVED 2026-07-16: no, auth stays optional.** Content-addressing already prevents forging/overwriting (PUT 422s on hash mismatch), and unreferenced uploads are reclaimed by mark/sweep. **Coupled to 1.3:** valid only while 1.3's inventory union stays time-bounded. See 1.5's note.
- ~~**1.6** — drop `Failed` from the GC terminal set **[NM]**, or make it genuinely terminal **[ST]**?~~ **RESOLVED 2026-07-16: [NM].** A failed work unit is resumable (a human steers it or hand-fixes the file), so the GC must not age its blobs out. See 1.6.
- ~~**3.2** — encoded pass-through: document unverified-serve as contractual, or add opt-in verify?~~ **RESOLVED 2026-07-16: document it, no flag.** The 200-vs-404 divergence is accepted; "never wrong bytes" is an end-to-end guarantee the client completes. **Write the doc only after 3.3 step 2** — the premise is false until then.
- ~~**3.3** — default s3-direct compression OFF, vs teach the SDK readers to decode zstd?~~ **RESOLVED 2026-07-16: both, in that order.** Default OFF now; SDK decode is required parity work, not an optional enhancement. Retagged **[NM]** — studio does not consume the web SDK.
- **All four of the plan's original open decisions are now resolved.** New ones raised by Phase 0 stay below.

## Follow-ups filed (not in this plan's scope)

- ✅ **RESOLVED 2026-07-16** (`968a215b`) — ~~Postgres/Mongo node stores have no CI coverage at
  all~~ (raised by 1.1). New `.github/workflows/nodestore-adapter-conformance.yml` runs
  `dir_persistence`, `postgres_round_trip`, `mongo_round_trip`. Two things learned that generalize:
  - **Wiring the suites in would have proved nothing.** The shared F7 conformance suite had 7
    scenarios, none touching `known_room_ids`/`can_enumerate_rooms`, and `run_all(store, room_id)`
    takes a *single* room — so it structurally *could not* test multi-room enumeration. The CI job
    would have gone green while testing nothing about finding #1. Added
    `known_room_ids_is_superset_of_written_rooms` (superset, never exact-set — the call is
    store-global, so other rooms are legitimately present). RED-proven on both stores against real
    containers, and deliberately sabotaged *two different ways* — empty enumeration (Postgres) and
    partial, dropping one room (Mongo) — because the guard's whole weakness is *wrong* rather than
    *absent* enumeration. **Check that a suite asserts the property before wiring it into CI to
    guard that property.**
  - **The tests fail *open*.** Both round-trip tests `eprintln!` + `return` when Docker is
    unavailable, which the harness reports as **PASS** — correct on a dev laptop, catastrophic in
    CI, where a broken Docker setup goes green while testing nothing. `NODALMERGE_REQUIRE_DOCKER=1`
    (set at job level) converts the skip into a panic; local runs keep skipping. Verified in both
    directions by pointing the image at a nonexistent tag. **Any "skips gracefully" test is a
    fail-open test the moment CI runs it.**
  - ⚠ **Now THREE instances of one shape — worth a single decision, not three fixes.**
  `Rooms::collect_recent_upload_hashes` (added by 1.3) `warn!`s and returns an empty set on an
  inventory read error, so a transient SQLite error silently un-protects recent uploads for that
  tick. Same shape as, one layer down: both stores' `known_room_ids()` `warn!` + return an empty `Vec` on
    query failure, so a transient DB error is indistinguishable from "no rooms" — the same
    silent-data-loss shape the conformance scenario now guards against, but a live-traffic version
    the scenario cannot reach.
- ➡ **CI coverage — MOVED to Phase 8 (deferred).** Was the highest-value item on this list;
  it outgrew a follow-up bullet, so it is now a phase of its own at the bottom of the plan
  rather than tracked in two places. **Its stub convention (8.0) is in force now, not
  deferred** — slices still add their step; when a step's home is unclear or needs CI setup
  that doesn't exist, stub it where it belongs and report it. Note `minio_round_trip.rs`
  should NOT wait for Phase 8: it is what **1.3** is blocked on, and 1.3 should absorb it.

- ⚠ **`archive.import` silently defaults to the WRITE path on a misspelled key** (found by 2.3,
  because it inverted one of the slice's own tests). `parse_archive_import_request` reads
  `message["import_mode"]` and `.unwrap_or("full_apply")`; the "must be full_apply|metadata_only"
  validation runs **after** the default, so it cannot catch a wrong key name. A client sending
  `"mode": "metadata_only"` gets **`full_apply`** — the destructive option — silently. The safe
  default for an absent mode is arguably `metadata_only`, or no default at all (reject).
- ⚠ **`ExportManifestDocument`'s signature does not cover its digests** (found by 2.3, adjacent
  but out of its scope). `signature_payload` binds format_version / source_room / compatibility
  window / `payload_digest_policy` / policy timeline — but **not `payload_digest_set`**. So the
  signature attests to which digest *policy* was used, never to the digest *values*: a signed
  manifest's blobs digest can be altered without invalidating the signature. Verified by reading
  `archive_export.rs:356`. This weakens the "a wrong signed digest is forever" reasoning 2.3 used
  (correctly, for its own purposes) and deserves its own decision.
- **`ArchiveReasonClass` has no variant for a backend/config failure**, so 2.3's Delegate-mode
  errors report as `CheckpointNotFound` — actively misleading for a configuration problem. The
  detail is in `reason_message`. Adding a variant is a **frozen cross-runtime contract change**
  (`core/crdt/archive_contracts.rs`, `engine/host-core/protocol.rs`,
  `archive_portability_vectors.rs`) with a .NET parity obligation — hence not absorbed by 2.3.
- **`S3BlobStore::hydrate_blob`'s `Unhydratable` detail is GC-specific prose** ("the server can
  never read *tree objects*", "*Studio GC* cannot run in this mode") and now surfaces verbatim in
  archive-export errors, where it reads oddly. Still accurate about the *cause*. Not changed
  because `delegate_gc.rs` asserts on that exact wording.
- **`.zst`-at-rest is invisible to every S3 reader but visible to the sweeper** (found by 2.2,
  pre-existing). `S3BlobStore::key_for` derives identity-only `blake3/<hex>`, so `has_blob`,
  `resolve_get_url`, `verify_uploaded` and now `hydrate_blob` never look at `<hex>.zst` — while
  `blob_gc_sweep` enumerates via `parse_blob_entry_name`, which understands both. 2.2 matched
  `has_blob`'s derivation deliberately (one derivation for every reader, so hydration can't drift
  from existence), which is the consistent choice, but it means the sweeper's view and every
  reader's view genuinely diverge. **3.3 defaulting s3-direct compression OFF keeps this latent
  rather than live.**
- **Export holds every blob in memory at once.** `resolve_referenced_blobs` materializes the full
  map and `canonical_hash` takes it whole, so a 10 GB room means ~10 GB RSS during export.
  Pre-existing (Dir did it too) but **free on S3 until 2.3**. Not fixable without changing
  `canonical_hash`'s frozen cross-runtime contract.
- **Unbounded retention of revivable statuses.** After 1.6, `Failed` joins
  `Cancelled`/`DeadLettered` in being retained indefinitely. That is consistent, not new,
  but three statuses now never age out. If it matters, the fix is a real revival-window
  policy applied to all three — a design question this plan should not absorb.
- **`--blob-upload-grace` knob** (if 1.3 introduces one): needs a documented default,
  ideally tied to the presign TTL, and a CLI/config surface on all three binaries — which
  lands in 7.1's shared bootstrap module.
- **7.4** — which placeholder wins, `_global` or `default`/`blobs`?
