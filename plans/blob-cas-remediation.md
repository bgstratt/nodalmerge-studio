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
- [ ] Phase 1 — GC data-loss & the crash (P0 — can permanently destroy blobs / abort the server) — **1.1 ✅ 1.5 ✅ 1.6 ✅ committed 2026-07-16** (`a4c4a694`, `ef693f39`, `5664c2b1` + studio `58d9a5c`); **1.2 blocked** on 7.5's composition seam, **1.3 blocked** on the MinIO test + time-bounding design, **1.4 open**
- [ ] Phase 2 — Non-hydrating (S3) backend correctness
- [ ] Phase 3 — Blob integrity & cross-runtime interop
- [ ] Phase 4 — HTTP-surface robustness & backward compatibility
- [ ] Phase 5 — Migration safety
- [ ] Phase 6 — Runtime, async-safety & efficiency
- [ ] Phase 7 — Duplication & altitude cleanup

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

### 1.3 — S3 sweep honors grace + consults inventory **[NM]** (#3)
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

### 1.4 — `sweep_blobs` hydration/write-through race **[NM]** (#5)
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

### 2.1 — Existence probe on the blob abstraction **[NM]**
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

### 2.2 — Tree walk on S3 uses a hydrating read or fails loud **[NM]** (#10)
`nodalmerge:server/server/src/tree_walk.rs:101`. Tree blobs are fetched via
`get_blob`, hardwired `None` on S3, so every new-mode GC run over a cas-tree snapshot
fails closed forever.
- Walk trees through a resolution path that works on S3 (pull via the presigned/origin
  path used elsewhere), or — if a run genuinely cannot resolve a tree — surface a
  distinct, actionable error and metric rather than a generic per-run Failure, so
  "GC never runs on S3" is impossible to miss.
- **Validation:** 0.2 GC-over-cas-tree scenario completes on the non-hydrating stub
  (via the resolve path) instead of erroring every tick.

### 2.3 — Archive export/hydration must not silently drop blobs **[NM]** (#7)
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

### 3.1 — .NET accepts unknown-content-size zstd frames **[NM]** (#4)
`nodalmerge:hosts/dotnet/…/BlobCompression.cs:111`. `TryDecompress` requires
`written == buffer.Length` after sizing from `GetDecompressedSize`, which returns an
**upper bound** for frames without the content-size header — i.e. every frame from
Rust `zstd::stream::encode_all`. Result: all cross-runtime compressed fetches throw
"corrupt zstd frame."
- On unknown/bound size, accept `written <= buffer.Length` and slice, or switch to a
  streaming `DecompressionStream`. Keep the existing hash-verify-after-decompress.
- **Validation:** 0.1 fixture decodes green in both directions.

### 3.2 — Verify-on-read policy for the file provider **[NM]** (#13)
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

### 3.3 — s3-direct client zstd vs web/wasm readers **[BOTH]** (#6)
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

Confirmed performance/robustness debt. 6.1 is the most impactful (a hung delegate can
wedge a worker indefinitely) — treat it as near-P1.

### 6.1 — s3-blobs runtime bridge + timeouts **[NM]** (#11)
`nodalmerge:server/s3-blobs/src/lib.rs:289` (8 sites). Each S3 op spawns a fresh OS
thread + a new multi-threaded tokio `Runtime` and blocks on a **timeout-less**
`mpsc::recv()`, called synchronously from async contexts; the delegate path uses
`reqwest::Client::new()` with **no timeout**, so a hung endpoint wedges a tokio worker
forever.
- Hold **one** shared runtime handle (or a dedicated dispatcher) in `S3BlobStore` and
  reuse it across all sites; factor the bridge into a single helper (ties to 7.3).
- Give the delegate `reqwest::Client` an explicit request timeout; add a bounded
  timeout to the blocking receive.
- **Validation:** a stalled delegate endpoint fails the op within the timeout instead
  of pinning a worker; a soak test shows no per-op runtime construction.

### 6.2 — Move blocking blob work off async workers **[NM]**
`nodalmerge:server/server/src/blob_http.rs:234`. GET/PUT handlers do blocking
`std::fs` I/O, up-to-64 MiB BLAKE3 hashing, and zstd encode/decode directly on tokio
workers; enough concurrent blob ops starve WS/sync traffic sharing the runtime.
- Wrap the synchronous persistence calls in `spawn_blocking` (or make the persistence
  API async). Depends on 6.1 for the S3 path.
- **Validation:** a concurrent-blob load test shows WS latency stays flat.

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

## Sequencing summary

1. **Phase 0** unblocks everything (RED tests + harnesses).
2. **Phase 1** is P0 — schedule immediately after 0.3; each slice ships behind its
   0.3 gate.
3. **Phase 2** depends on 0.2 and shares the existence/hydration seam — 2.1 first
   (others build on it).
4. **Phases 3–5** are independent of each other; parallelizable across contributors.
5. **Phase 6** — start **6.1** early (near-P1: the wedge risk); the rest after the
   surface is correct.
6. **Phase 7** last, behavior-preserving, guarded by earlier tests. 7.5 absorbs the
   1.2 composition seam; 7.3's bridge helper absorbs 6.1's.

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
  - **Root cause, unfixed:** these filters enumerate implementation files *by hand*, so every
    suite's real coverage depends on someone remembering each file it transitively exercises. Same
    shape as the missing `.gitattributes` (fixed `10723954`): correct by luck, silent when wrong.
    See the follow-up below.
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

- ⚠ **Postgres/Mongo node stores have no CI coverage at all** (raised by 1.1, 2026-07-16 —
  **the highest-value item on this list**). `server/stores/postgres/tests/postgres_round_trip.rs`
  and `server/stores/mongo/tests/mongo_round_trip.rs` (and `server/stores/conformance/`) are run by
  **no workflow** — they need Docker/testcontainers, which CI never sets up. This is now
  safety-critical rather than merely thin: 1.1 makes those two stores assert
  `can_enumerate_rooms() == true`, which instructs the sweep to **trust** their `known_room_ids()`
  and proceed with deletes. A regression making either query return empty/partial reintroduces
  finding #1's permanent blob deletion **with the fail-closed guard vouching for it** — the guard
  only defends against *absent* enumeration, never *wrong* enumeration. 1.1's implementations were
  verified only by a local Docker run. Wire a services/testcontainers job.
- **CI `paths:` filters are hand-maintained and silently under-trigger** (raised by 1.5/1.6). Both
  Phase 1 CI gaps were the same root cause: coverage depends on a human remembering to list every
  source file a suite guards. Consider dropping the filters for the correctness workflows (they are
  cheap), deriving them, or adding a meta-test asserting each named `--test` target's guarded
  sources appear in its trigger list. Until then, **every slice must re-check its own trigger.**

- **Unbounded retention of revivable statuses.** After 1.6, `Failed` joins
  `Cancelled`/`DeadLettered` in being retained indefinitely. That is consistent, not new,
  but three statuses now never age out. If it matters, the fix is a real revival-window
  policy applied to all three — a design question this plan should not absorb.
- **`--blob-upload-grace` knob** (if 1.3 introduces one): needs a documented default,
  ideally tied to the presign TTL, and a CLI/config surface on all three binaries — which
  lands in 7.1's shared bootstrap module.
- **7.4** — which placeholder wins, `_global` or `default`/`blobs`?
