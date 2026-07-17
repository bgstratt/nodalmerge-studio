# .NET Host Readiness — local 0.2.3, studio integration, Phase 8 publish gate

**Created 2026-07-17.** Successor to `blob-cas-remediation.md` (PLAN COMPLETE except its
deferred Phase 8, which this plan absorbs as Phase C). Goal: the .NET-host/studio topology
(studio consuming the NuGet packages + precompiled natives, dotnet host as the server) is
**fully capable on Windows with local 0.2.3 packages**, with Phase 8 executed as the gate
before any nuget.org publish (nothing published there since 0.2.0).

**Out of scope (deliberately):** real linux-x64 deployment (later; the pack script already
cross-builds via WSL when available), nuget.org publish itself, docs-repo NuGet bump
(separate phase after studio + nm proper are finished — user decision 2026-07-17).

**Not applicable to this topology** (Rust `nodalmerge-server` binary only — do not pull in):
`persist_node` warn-only, Postgres/Mongo `room_nodes_version`, fail-open `known_room_ids` /
GC-inventory instances, ws_handler sync I/O, dev-server workflow. Studio's own GC
(`BlobGcService`) is already fail-closed on live-set computation — verified 2026-07-17.

## Conventions (carried over from blob-cas-remediation.md)

- RED-first: every behavior change lands with a test that fails on the prior code. The
  orchestrator independently reproduces every RED (production-file revert, or targeted
  sabotage when a revert can't compile) before commit.
- Implementing agents do not commit and do not edit `plans/`.
- 8.0 stub convention stays in force: every slice wires its tests into CI or stubs the step
  with a follow-up comment; silent omission is the failure mode this plan exists to kill.
- Known flakes — do not chase: Rust `archive_profile_002_object_manifest_parity_reports_p50_p95`
  (parallel-lib only, passes solo); .NET `RoomTokenEmbeddedProviderTests` FFI
  `DllNotFoundException` load-order, `SpecAuthVectorsTests`, `RuntimeMessageProcessorTests`
  meter-capture dictionary race, `BranchForkVectorsTests`.

---

## Phase A — .NET legacy-migration parity with Rust 5.1 **[NM]**

The one remaining .NET-host code fix. `FileBlobStoreProvider.MigrateLegacyLayoutIfNeeded`
(hosts/dotnet/src/NodalMerge.Host.Composition/FileBlobStoreProvider.cs) still has the two
hazards Rust's `migrate_legacy_blob_layout` fixed in slice 5.1:

1. **Dedupe branch deletes without verifying dest** (`if (File.Exists(dest)) File.Delete(legacyFile)`)
   — if the dest copy is corrupt/partial, this deletes the only good copy. The legacy bytes
   at this point are already BLAKE3-verified; the dest is not.
2. **Marker written unconditionally** (`File.WriteAllText(marker, ...)` after the loop) — a
   transient read failure (AV lock etc.) quarantines the file forever and the marker
   prevents any retry.

### A1 — verify-dest + marker deferral **[NM]**

Behavior (mirror Rust 5.1 semantics, adapted to the existing quarantine design):

- **Dedupe branch:** when `dest` exists, hash its bytes. Match → delete legacy (true dedupe,
  current behavior). Mismatch → replace dest with the already-verified legacy bytes
  (write-tmp-then-atomic-replace; never a window with no good copy on disk), log a warning,
  then remove legacy.
- **Read failure:** leave the file **in place** (do not quarantine — it may be transient),
  warn, mark the pass dirty. Next construction retries it.
- **Hash mismatch:** keep quarantining to `.migration-skipped` (genuinely corrupt, terminal),
  warn, mark the pass dirty.
- **Marker:** written **only on a clean pass** (nothing skipped/quarantined/repair-failed).
  Self-converging: quarantined files leave the scan scope, so the next boot's pass is clean
  and writes the marker. A dirty pass just means a cheap `TopDirectoryOnly` re-scan per boot.

RED tests (all must fail on current code):
- corrupt dest + good legacy → after migration, `blake3/<hash>` contains the good bytes and
  a read round-trips (current code: legacy deleted, corrupt dest survives, read = Missing).
- quarantine occurred → `.layout-v2` absent; second construction with the transient cause
  removed migrates the file and then writes the marker (current code: marker present after
  first pass).
- Regression pins: clean pass writes marker; marker present → no scan; matching dest →
  legacy deleted (dedupe still works).

CI: confirm the test class runs in a workflow step; wire or stub per 8.0 and say which in
the report.

- [x] A1 shipped (nodalmerge `9603c9d4`; RED reproduced 4/8 migration tests on revert; host suite 688/688)

## Phase B — repack 0.2.3 + studio integration verification **[ST]**

Nothing from the remediation plan is live in studio until this happens — studio pins 0.2.2.
**Depends on A1** (pack once, with the fix in).

### B1 — bump + repack + restore
- Bump the five `NodalMerge.*` pins in studio `Directory.Packages.props` 0.2.2 → 0.2.3.
- `scripts/restore-local-nodalmerge.ps1 -Version 0.2.3` (packs nodalmerge via
  `pack-local-artifacts.ps1 -SkipNpm -SkipCrates`, restores studio against the local feed;
  `pack-local-nuget.ps1` clears the global NuGet cache for the packed version itself).
- Build the studio solution. Watch for the native-DLL resolver footgun (memory:
  nodalmerge-local-dev-flow).

### B2 — full studio test suite on 0.2.3
- Unit + Integration (native available locally). Gate: green modulo the documented
  pre-existing flakes; any NEW failure is a finding against this session's host changes and
  blocks B3 until dispositioned.

### B3 — multi-user smoke + 6.5 end-to-end pairing
- Run the multi-user milestone flow (two clients, one dotnet-host server) on 0.2.3.
- First-ever end-to-end pairing of studio's `StudioInboundPackObserver` with the new
  `RuntimeInboundPackObserverDispatcher` (each side only tested in isolation so far).
  Positive evidence required: observer actually invoked through the dispatcher path
  (log/counter assertion, not just "nothing crashed").

- [x] B1 shipped (studio `9be0f39`; local repack + restore + build clean)  · [x] B2 green
  (Contracts 24 / Core 42 / Merge 69 / Projections 37 / AgentRuntime 107 / Tasks 14 /
  Integration 680 — 0 failed on 0.2.3)  · [x] B3 green (MultiUser + StudioInboundPackObserver
  6/6 on 0.2.3 — the 6.5 end-to-end pairing)

## Phase C — Phase 8 executed (CI coverage) **[NM]** — the nuget.org publish gate

Absorbs blob-cas-remediation.md Phase 8 verbatim (see that doc for the full audit text).
The audit's RED: **~15 Rust integration-test files and ~34 .NET test classes run in ZERO
workflows**, plus one-directional parity contracts.

### C1 — 8.1 meta-test: every test file is named by some workflow
- A checked-in meta-check (test or script + its own workflow step — implementer designs,
  but it must itself run in CI) asserting every `server/**/tests/*.rs` file and every .NET
  test class is named by ≥1 workflow step. Explicit, reviewed allow-list for deliberate
  exclusions (with reasons inline).
- **Validation:** run against the tree before C2 lands, it reproduces the audit's ~15 + ~34
  list. That list IS its RED.

### C2 — close the audit's backlog
- Wire everything C1 surfaces, batched by setup class:
  (a) needs nothing → add steps/paths;
  (b) needs native FFI → follow the existing gating pattern;
  (c) needs Docker/MinIO → follow `blob-s3-gc-minio.yml` + `NODALMERGE_REQUIRE_DOCKER=1`
  (the 1.3 pattern).
- Every newly wired suite must PASS or get an explicit allow-list entry with a filed reason
  — no wiring-then-ignoring. Log what was intentionally excluded.

### C3 — 8.3 both-directions rule for parity contracts
- Encode in the C1 meta-check: a declared list of cross-runtime parity pairs
  (SpecAuthVectors, BranchForkVectors, QueryMaterializationVectors, ZstdInterop,
  blob-layout vectors, capcomp) where BOTH sides must be named by workflow steps, or the
  meta-check fails.

### C4 — studio Integration CI lane **[ST]**
- Flip `NODALMERGE_NATIVE_AVAILABLE` repo variable so studio's Integration lane actually
  runs in CI (`gh variable set` or repo settings — needs user's gh auth), and verify the
  lane provisions/locates the natives it needs.

- [x] C1 shipped (nodalmerge `ff7037fc`; script + baseline + ci-coverage-meta.yml. RED: empty
  baseline reproduces 48 gaps = 17 Rust + 27 .NET uncovered + 4 one-sided pairs. Ratchet teeth
  verified: baselining a covered suite flags STALE; restoring goes green)  · [x] C2 shipped
  (nodalmerge `b226873f` Rust, `ed213f75` .NET managed, `596f2223` .NET FFI + Rust MinIO.
  Baseline 46→1: Rust 62/62, .NET 61/62, all 7 parity pairs covered. New workflows
  server-core-suites / dotnet-host-managed-suites / dotnet-host-ffi-suites; rust MinIO added to
  blob-s3-gc-minio.yml. Every wired suite verified green locally. Only S3DirectMinioEndToEndTests
  deferred — needs multi-process origin+MinIO orchestration, filed in baseline)
  · [x] C3 shipped (parity-pair rule folded into C1's meta-check; 7 pairs declared, all now
  fully covered on both runtimes)  · [~] C4 configured (studio `2296040` ci.yml local-pack step,
  nodalmerge `ec51e7d8` cross-platform pack fix; repo var `NODALMERGE_NATIVE_AVAILABLE=true` set
  2026-07-17). **Verification pending a CI run** — needs both branches pushed (nodalmerge
  `blobExpansion` + studio `cas-distribution-storage`) so studio CI packs the local 0.2.3 feed
  and runs the native Integration lane. Root cause found: nuget.org has NodalMerge.* only ≤0.2.0,
  so studio CI could not restore the 0.2.x pins at all until this local-pack step)

## Sequencing

A1 and C1 are independent — run in parallel. B needs A1 (pack the fixed code). C2/C3 need
C1's list. C4 anytime. **Publish gate = A ✓, B ✓, C ✓.** Then (separate, later): real
linux-x64 natives, nuget.org 0.2.3, docs-repo bump.

## Status log

- 2026-07-17: plan created.
- 2026-07-17: A ✓, B ✓, C1/C3 ✓ shipped and RED-verified. Publish gate now blocks only on C2
  (burn down the 46-entry baseline) and C4 (studio native CI lane — needs user gh auth).
- 2026-07-17: C2 ✓ shipped. Baseline 46→1 (Rust 62/62, .NET 61/62, all 7 parity pairs). Only
  remaining publish-gate item is C4 (flip `NODALMERGE_NATIVE_AVAILABLE` in studio — needs user's
  gh auth). One suite deferred as its own follow-up slice: S3DirectMinioEndToEndTests
  (multi-process origin+MinIO orchestration). **Publish gate: A ✓ B ✓ C ✓ except C4.**
- 2026-07-17: C4 configured — studio ci.yml now packs the local 0.2.3 feed (nuget.org has
  NodalMerge.* only ≤0.2.0, the real blocker), pack-local-nuget.ps1 made CI-linux-safe,
  `NODALMERGE_NATIVE_AVAILABLE=true` set. Last step is a verifying CI run (push both branches).
