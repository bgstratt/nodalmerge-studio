# Repo-identity convergence — make two clones of one repo land in one repo room

## Status

**Phase 0 + Phase 1 SHIPPED & green (2026-07-17), branch `cas-distribution-storage`.** The
deterministic root-SHA repoId is implemented (`RepositoryIdentityMatcher.DeterministicRepoId`),
wired into both the in-memory and engine `WorkgroupRepositoryDirectory.Register` paths and into
`RepositoryRegistryService.RegisterAsync` (local `RepositoryId` now deterministic when the signal
is strong, with guid fallback on degraded hints or a same-peer fork collision). Fork-split added to
`RepositoryIdentityMatcher.Match` (single root match + disjoint non-empty remotes →
`NeedsDisambiguation`). Schema amended (`docs/STUDIO_ROOM_SCHEMA.md` (b) mint format + D2). Tests:
3 new (convergence-without-shared-map, determinism vectors, single-entry fork-split) + updated
continuity test, all green; repository/workgroup/registry suite 55/55; broader sweep 148/149 (the
one failure is the known SQLite-pool teardown flake — the two-peer test body passes on isolated
re-run). **Net effect: two clones of one repo now converge on the same repoId with zero
replication dependency** — the reported "no pathways on the remote" bug is fixed for the common
(strong-signal) case at registration time.

**What this changed for the remaining phases:** determinism solved the common case outright, so
**Phase 2 (re-resolution) shrank in value** — it now matters only for degraded-hint repos (no root
SHA) and the rare mode-B fork race, not the everyday case. **Phase 3 (repair) is what the user's
two already-diverged machines need** (their eval repo was registered under old guids before this
fix); the cheap interim is to re-register the repo on both machines (deterministic id → converge),
with a durable repair as follow-up.

Diagnosis + code seams: [`docs/multi-user-repo-convergence-findings.md`](../docs/multi-user-repo-convergence-findings.md).
Sits on top of CAS-distribution Phase 6/7 (`plans/cas-distribution-and-storage.md`) — closes the
convergence hole Phase 6's "workgroup map as authority" left open under a startup race.

## The problem in one paragraph

Two independent clones of the same physical repo (identical root SHA, shared remote) mint
**different** workgroup repoIds, so their per-repo state (branches, files, pathways,
activity) lives in two different `repo/{repoId}` rooms that never see each other. Only the
goal node crosses, because goals ride the shared `workgroup1` room. Binding is done once,
edge-triggered at `RegisterAsync` time, against the local in-process view of the workgroup
`repositories` map — which, at host startup, has usually not yet caught up the peer's
authoritative entry. `NoMatch` → mint. And nothing ever re-resolves: binding is sticky and
`docs/STUDIO_ROOM_SCHEMA.md` (b) **D2** forbids re-deriving identity after first contact.

## Design goal

The intent the user stated: *both peers connect to the workgroup room; as repos are added
they get persisted and shared between both; if two peers are on the same repo, they see the
repo room's DAG (pathways, activity, …) when they select the session.* Concretely:

> Two clones of the same repo MUST converge on one `repoId` (hence one repo room) **without
> depending on registration-time ordering or replication timing**, while genuine forks and
> hint-degraded repos still get an honest one-time disambiguation (D2's real concern).

## Root-cause recap (see findings doc for the full trace)

- **Where it mints:** `RepositoryRegistryService.BindToWorkgroupAsync` → `MatchAsync` →
  local `repositories` map → `NoMatch` → `WorkgroupRepositoryDirectory.RegisterAsync`
  (`repo-{guid:N}`).
- **Why it races:** the extension registers the open repo eagerly at host start, before
  `RoomPeerClient`'s membership loop + `workgroup1` catch-up has landed the peer's entry.
- **Why it's permanent:** binding is one-shot; `Rehydrate`/`Refresh` never re-match; D2
  forbids re-derivation.

## Two parts

- **Part A — Convergence (the bug).** Make two clones of one repo land in one repo room.
  Phases 0–4 below. This is the fix for the actual reported symptom.
- **Part B — Multi-repo workspace & lifecycle (the UX).** Surface repo/room selection to the
  user, register all open folders, and stop forcing a host restart on workspace/repo change.
  Phases B1–B4, added 2026-07-17 after confirming the storage layer is already multi-repo (see
  "Part B" section for the evidence). A bounded expansion, not a rewrite — the hard part (N repo
  rooms per host) already exists.

The two parts reinforce each other: Part A's deterministic id auto-converges the common case so
Part B's picker is an *override*, not the primary path; Part B registers more repos → more
binding events → Part A's convergence matters more. Ship Part A first (it fixes the bug); Part B
is the "having to reload the extension feels bad" follow-through.

## Decision to make first (blocks Phase 1) — DECIDED 2026-07-17: amend

**Amend D2** (user-directed, 2026-07-17). Today D2 says "git supplies matching hints, never
identity; identity is minted, not derived." That is precisely what makes a random per-peer id
diverge under concurrency. The amendment (argue it explicitly in `docs/STUDIO_ROOM_SCHEMA.md`):

> A repo with a **strong, stable, cross-peer-reproducible** signal (a non-empty root-SHA set)
> derives a **deterministic default** repoId from **that root-SHA set alone** (remotes excluded —
> they vary between clones of the same repo and would defeat convergence). This is not a claim
> that the signal is eternal identity — the workgroup map remains the authority, and a genuine
> fork (same root SHA, disjoint remotes) still splits via one-time disambiguation, and a later
> human override still wins — it is a claim that *two clones of the same repo must not need to
> race through a shared map to agree on a starting id they can both compute alone.* Hint-degraded
> repos (empty root-SHA set) keep the minted-guid + disambiguation path unchanged.

If we (or the schema's original author) reject deriving the id, Phase 1 falls back to
"guid mint + mandatory re-resolution" (Phase 2 alone), which converges too but only *after*
replication and needs the migration machinery on the hot path for every repo, not just the
degraded ones. **Recommended: derive (Phase 1) + re-resolve (Phase 2) together** — derive
removes the race for the common case, re-resolve repairs the tail (degraded, fork-collapse,
already-diverged existing state).

---

## Phase 0 — Reproduce at the storage level (RED)

Before any fix, pin the bug in a test that fails today.

- **0.1** `WorkgroupRepositoryDirectory` / `RepositoryRegistryService` two-peer test using the
  in-memory doubles (`InMemoryWorkgroupRepositoryDirectory` + a *non*-shared store per peer, to
  model "B's map hasn't caught up A's entry yet"): peer A registers repo R (root SHA S) and
  mints; peer B registers the same R (same S) against a map that does **not** yet contain A's
  entry → asserts (RED) that B mints a **different** id. This is the exact production race.
- **0.2** A convergence assertion that is RED today and GREEN after the fix: after A's entry is
  made visible to B, B's binding for R resolves to A's id (no second id survives).
- **0.3** Simultaneous-mint variant: both mint against empty maps, then both maps merge → assert
  exactly one surviving canonical id after re-resolution (RED today — two survive forever).
- Capture a real desktop+laptop timeline (host logs, both peers) to confirm mode (A) vs (B) in
  the field and attach to the findings doc. Cheap, high-signal.

## Phase 1 — Deterministic repoId for strong-signal repos

**The key design decision (locked 2026-07-17): the deterministic key is the root-SHA set
ONLY — remotes are deliberately excluded.** Rationale, proven against the real failure: the
eval repo had *different* remote sets on the two machines (desktop `{local-path-origin, github}`,
laptop `{github}`). Any key that folds in remotes would give the two clones different ids and
**re-introduce the exact divergence we are fixing**. Root SHA is the one strong signal both
clones reproduce identically. The cost — genuine forks share a root SHA and therefore a
deterministic id — is handled explicitly by fork-split (1.3), not by weakening the key.

- **1.1** Add a pure deterministic id function
  `DeterministicRepoId(IReadOnlyList<string> rootShas)` →
  `"repo-" + hex(sha256(join(sort(distinct(rootShas)), "\n")))[..32]`, or **null** when the set is
  empty (degraded). Width-agnostic on SHA input (no 40-char assumption). Put it next to
  `RepositoryIdentityMatcher` (storage-agnostic, directly unit-testable).
- **1.2** Make the **local** `RepositoryV1.RepositoryId` deterministic when the signal is strong.
  In `RepositoryRegistryService.RegisterAsync`, compute hints **before** minting the id; if
  `DeterministicRepoId(hints.RootShas)` is non-null, use it as `RepositoryId` (else keep
  `repo-{guid:N}`). The existing preferred-id continuity (`RegisterWorkgroupEntryAsync` passes
  `preferredRepoId: RepositoryId`) then propagates that same id to `WorkgroupRepoId` for free — so
  two clones of one repo converge on the **same `RepositoryId` *and* `WorkgroupRepoId`**, hence the
  same `repo/{id}` room, **with zero replication dependency** (works fully offline, works under
  simultaneous registration, because both compute the same key — a second register is an upsert of
  one map entry, not a second entry). This also keeps `WorkUnit.RepositoryId` portable across peers
  (strengthens the CAS-identity continuity path, doesn't break it).
- **1.3** Fork-split (preserve D2's real concern). Two genuine forks share a root SHA → the same
  deterministic id. On register/bind, if the workgroup map already holds an entry at that
  deterministic id whose remotes are **non-empty and disjoint** from the local remotes, do **not**
  silently join or overwrite — return `NeedsDisambiguation` (offer "join existing" vs
  "register-new", where register-new mints a `guid` id that splits the fork into its own room).
  Remotes that intersect, or either side empty → same repo → converge. This reuses the existing
  matcher's remote-tiebreak intent; only the *default* changes from random-mint to deterministic.
- **1.4** Degraded case (empty root-SHA set: shallow clone, empty repo, no HEAD) is unchanged —
  `DeterministicRepoId` returns null, so `RepositoryId` stays `repo-{guid:N}` and matching stays
  `NeedsDisambiguation`. A deterministic id from an empty set would wrongly collapse unrelated
  degraded repos. Phase 2 converges these via re-resolution.
- **1.5** Schema: amend `docs/STUDIO_ROOM_SCHEMA.md` (b) "repoId mint format" + D2 per the decision
  above — **root-SHA-only derivation**, remotes documented as fork-split signal not identity key.
  Format-compatible: `repo-{32 hex}` shape is unchanged; only the derivation of the hex changes,
  and only for strong-signal repos.
- Tests: 0.1/0.2 go GREEN; add id-determinism vectors (same root SHAs → same id on repeat and
  across two independent `Register` calls with *different remote sets*); fork-split stays
  `NeedsDisambiguation`; guid-fallback-on-degraded stays covered; the existing single-user
  continuity test (`RepositoryId == WorkgroupRepoId`) must still pass (it does — both are now the
  deterministic id).

## Phase 2 — Re-resolution safety net (converge the tail)

For degraded repos, genuine-fork-collapse, and any pre-existing diverged binding, add
eventual convergence on top of Phase 1.

- **2.1** Make `RepositoryRegistryService` re-run binding when the **workgroup** room changes.
  Hook the existing seam: `IStudioCacheRefreshCoordinator.RefreshAfterInboundPackAsync(roomId)`
  already fires after every inbound pack and fans out to `IRehydratable`s. Gate a new
  re-resolution step to `roomId == EffectiveWorkgroupRoomId`.
- **2.2** Re-resolution logic: for each locally-bound repo whose binding is **provisional**
  (self-minted with no confirmed peer match) or **degraded/pending**, re-`MatchAsync` against
  the now-updated map. If a canonical entry now matches, **migrate**: adopt its `repoId` /
  `repoRoomId`, update the `RepositoryV1` row, and let the membership loop join the new room
  (leave-old-room is acceptable to skip initially — a stale empty room is harmless — but note
  it).
- **2.3** Deterministic winner for duplicate entries (mode B): when the map holds >1 entry with
  the same root SHA and **matching remotes** (i.e. not a real fork), both peers independently
  pick the same canonical id (lexicographically smallest `repoId` is the simplest total order)
  and migrate to it; the loser entry is tombstoned/ignored. A real fork (root SHA shared,
  remotes differ) still routes to the one-time human disambiguation, unchanged.
- **2.4** Amend D2 to permit this bounded re-derivation: "identity may be re-resolved **only**
  to converge a provisional/degraded binding onto an already-registered canonical entry, never
  to re-identify a confirmed binding from freshly re-read git state." This preserves D2's real
  intent (a rebase/remote-rename must not re-identify a settled repo) while allowing catch-up
  convergence.
- Tests: 0.3 goes GREEN; a two-peer test where B binds provisionally then receives A's entry and
  migrates; a fork test that stays split (must NOT auto-collapse).

## Phase 3 — Repair the already-diverged state

The user's current two machines already hold `repo-c3296306…` (desktop) and `repo-cc249077…`
(laptop) for the eval repo. Phase 1 fixes *new* registrations; this collapses the existing pair.

- **3.1** A one-time repair that, on startup / on demand, detects two local-or-visible entries
  with the same root SHA + matching remotes and re-points the losing binding (and its repo room
  reference) at the deterministic/canonical winner. Reuse Phase 2.2/2.3 migration.
- **3.2** Decide the fate of content already written under the losing repo room (the desktop's
  eval pathways under `repo-c3296306…`). Options, cheapest first: (a) accept it as orphaned and
  let new work accrue under the canonical id; (b) re-key/replay the losing room's nodes into the
  canonical room. Start with (a); (b) only if the user needs the pre-repair history to survive.
- Manual: re-run the two-machine eval test and confirm both land in one `repo/{id}` room and
  pathways cross.

## Phase 4 — Verify

- Storage-level convergence tests (both orderings + simultaneous + fork-stays-split) all GREEN.
- Extension `tsc --noEmit` / `npm run compile` / `npm run webview-smoke` clean (no client
  changes expected, but the id shape is unchanged so nothing should break).
- Real two-machine smoke: goal on desktop → pathways visible on laptop and vice versa, both
  peers report the **same** repoId for the eval repo.
- Update `docs/guides/multi-user-smoke.md` and the multi-user room-server guide with the
  "both peers must converge on one repoId" expectation + how to check it.

# Part C — Goal replication (peers on one repo see each other's goals)

**SHIPPED & green (2026-07-17), branch `cas-distribution-storage`.** Found while validating Phase 1
on two machines: the repo rooms converged and repo-scoped work (work units, snapshots, pathways)
replicated — but **each peer still saw only its own top-level goal.**

**Root cause (distinct from convergence):** `GoalV1` (what the Goal Workspace lists via
`GoalNodeService`) is documented "workgroup/global by design" but was physically stored in the
peer-private `"studio"` room, which **Slice 7.3 made non-replicating** (to stop settings/profiles/
registry from colliding across peers). The `WorkgroupGoalDirectory` meant to carry cross-peer goal
coordination is a 6.3 stub, never wired into goal creation. So goals simply never crossed — 7.3
collateral damage, not a Phase 1 regression.

**Fix (option #1 — repo-scope goals; user-chosen over the workgroup-replicate option):** denormalize
the goal's work-unit `RepositoryId` onto `GoalNode` and route `GoalV1` to the repo room like every
other repo-scoped kind, so peers on the same repo replicate each other's goals. Exactly the 6.3a
precedent (which added `RepositoryId` to `BranchV1`/`TaskV1`).

- `GoalNode.RepositoryId` (nullable) added; set at every creation site (`POST /studio/goals`,
  `GoalControlService`, MCP `GoalTools`/`ExternalGoalTools`) from the work unit. `with`-based status
  updates preserve it.
- `GoalV1` added to `StudioNodeStore.RepoScopedKinds`; `GoalNodeService.RecordAsync` writes via the
  repo-routing overload; `GoalNodeService.RehydratedKinds = [GoalV1]` so the inbound-pack refresh
  coordinator re-reads its cache on a repo-room pack. Null repo → `"studio"` fallback (unchanged).
- Schema doc updated (`STUDIO_ROOM_SCHEMA.md` (b) `repoRoomId` list + the GoalV1-is-global stance
  revised).
- Tests: `RoomPerRepoTests.Goal_write_with_a_bound_repository_lands_in_the_repo_room_not_studio`
  (routing) + `RoomReplicationTests.A_goal_created_on_one_peer_appears_in_another_peers_goal_service_
  on_the_same_repo` (end-to-end two-peer). Repo/goal/replication/multi-user sweep green.

**Deferred (option #2):** genuinely cross-repo goal fan-out (D3) — a goal spanning multiple repos —
still needs the workgroup-goal path wired (and the refresh coordinator's workgroup-room skip
revisited). A single-repo goal is repo-scoped now; that layer can override placement per goal later.

Needs a VSIX rebuild + reinstall on both machines to take effect (registration/goal-creation runs in
each peer's extension runtime).

# Part B — Multi-repo workspace & lifecycle

Added 2026-07-17. Motivated by the user's questions: "why restart the host when I load a new
workspace?", "why not join all open folders' rooms and keep a main?", and "why not a repo/room
pick-list in the goal workspace?"

## Evidence: the storage layer is already multi-repo (so this is UI + lifecycle, not a rewrite)

Confirmed by reading the host and extension:

- **One host already routes across N repo rooms.** `NodalMergeStudioNodeStore.WriteNodeAsync`
  routes each repo-scoped write to `repo/{workgroupRepoId}` keyed by the entity's `RepositoryId`,
  lazily minting a **separate `EngineRoomMap` per repo** (`_repoRoomMaps` ConcurrentDictionary);
  `ReadNodeAsync`/`ReadAllNodesAsync` **fan out across every bound repo room**
  (`GetBoundRepoRoomIdsAsync`). `RoomPeerClient` already joins all bound repo rooms.
- **Goals already carry a repo and can target different ones.** `WorkUnit.RepositoryId` exists;
  `POST /studio/goals` (`StudioRestEndpoints.cs`) already accepts `RepositoryId` /
  `RepositoryPath` / `NewRepositoryPath`; `WorkUnitCommandService.CreateAsync` resolves it (path
  auto-registers into the registry); children inherit the parent's `RepositoryId`.
- **The registry already holds many candidates** (`IRepositoryRegistryService`).

**Remaining single-repo residue (the actual work):**

1. **Host seeding/defaults** — `WorkspaceOptions.SeedRepositoryPath` is one nullable slot
   (`WorkspaceOptions.cs:8`), mutated in place by the workspace-switch endpoint
   (`StudioRestEndpoints.cs:692`); `"main"`-branch seeding pulls from that single dir
   (`FileSystemWorkspaceService.InitBranchAsync`) and is *preferred* over per-WorkUnit resolution
   (`ResolveBranchRepositoryCasIdAsync:57-62`); a single working-dir `RootPath`; scattered
   `SeedRepositoryPath ?? cwd` fallbacks (`StudioRestEndpoints.cs:829,5347-5362`,
   `SnapshotRetentionPolicy.cs:330`, `WorkspaceCacheManager.cs:263`, `FindingDetectorService.cs:206`).
2. **Extension lifecycle** — the host bakes data dir + workspace root + `Room__*` at **spawn**
   from `workspaceFolders[0]` (`HostManager.ts:234-242,276-284`); there is **no**
   `onDidChangeWorkspaceFolders`/`onDidChangeConfiguration` watcher (grep: 0 matches), so a folder/
   room change needs `restartHost`. But `nodalmerge.repositoryPath` is read **per-request**
   (`repositoryPath.ts`), so changing which repo you *work on* already needs no restart.
3. **UI** — the goal workspace has a single repo-*path* selector that edits `repositoryPath`
   (`ArtifactExplorerPanel.ts` + `goalWorkspace.js`); **no room picker, no multi-repo dropdown**.
   Only `workspaceFolders[0]` is used anywhere except the cross-repo file-reference picker.

## Phase B1 — Repo/room selector in the goal workspace (the pick-list)

The highest-value, lowest-risk piece — surfaces state that already exists.

- **B1.1** Goal-workspace dropdown listing selectable repos: registered repos from
  `GET /studio/repositories` unioned with the workgroup `repositories` map
  (`WorkgroupRepositoryDirectory.ListAsync`), labelled by `label`/path. Selecting one sets the
  target repo for a new goal — the server **already** accepts `RepositoryId` on
  `POST /studio/goals`, so this is a UI + request-wiring change, no new host contract.
- **B1.2** On peer join with a repo open and an **ambiguous** match, present the
  `RepositoryDisambiguationPendingV1` candidates (already produced by `MatchAsync`/
  `ResolveDisambiguationAsync`) as the same pick-list — the honest human override that makes the
  D2 amendment safe. "Register as new" stays an explicit choice.
- **B1.3** Show the resolved repoId/room per goal in the UI so a user can *see* two peers are on
  the same room (the exact thing that was invisible during the 2026-07-17 test).
- No schema change. Extension `tsc`/`compile`/`webview-smoke` must stay clean.

## Phase B2 — React to workspace/config change without a respawn

- **B2.1** Add `onDidChangeConfiguration` + `onDidChangeWorkspaceFolders` handlers in the
  extension. For a **repo/room-selection** change, drive it live: register the folder(s) with the
  host registry (host binds → `RoomPeerClient` joins the room on its next reconcile tick) and
  update the selector — no restart.
- **B2.2** Separate the two concepts the current code conflates: "which repo I'm working on"
  (already live via `repositoryPath` + registration) vs. "the host's data home" (data dir, db,
  blobs). Only the latter genuinely needs a respawn. See the open design decision below.
- **B2.3** Keep `restartHost` as the explicit escape hatch, but it should stop being the *only*
  way to change repo/room.

## Phase B3 — Register all open workspace folders, not just `[0]`

- **B3.1** On activation and on `onDidChangeWorkspaceFolders`, enumerate git folders across
  `vscode.workspace.workspaceFolders` (not just `[0]`), register each with the host registry →
  each binds → each repo room is joined automatically by the existing membership loop.
- **B3.2** Keep one repo as the **active/default** selection (seeded from the current
  `repositoryPath`/first-folder default), switchable via B1's dropdown. "Main" is a default, not
  a singleton.

## Phase B4 — De-singleton the host seeding residue

The real host-side work; do it behind Part A + B1 so the room model is already converging.

- **B4.1** Make `"main"`-branch seeding and CAS-id resolution per-target-repo instead of
  preferring the single `SeedRepositoryPath` — resolve from the goal/WorkUnit's `RepositoryId` via
  the registry, falling back to `SeedRepositoryPath` only when a goal names no repo.
- **B4.2** Audit the `SeedRepositoryPath ?? cwd` fallbacks (list in the evidence section) and
  route each through the registry where a `RepositoryId` is in hand.
- **B4.3** Leave the `"workspace-default"` singleton and the peer-private `"studio"` room as-is —
  those are correctly window/peer-scoped, not repo-scoped (Phase 7.3), and goals are meant to be
  cross-repo (D3).

## Open design decision for Part B (flag, don't silently pick)

**Should the host's data dir be per-VS-Code-window (stable) rather than derived from
`workspaceFolders[0]`?** Studio's local state (settings, profiles, scheduler, registry bindings,
peer-id, the `"studio"` room) is already peer/window-scoped, not repo-scoped — per-repo state
lives in repo rooms. If the data home is window-stable, then "change the active repo" *never*
touches the data dir and never needs a respawn (B2 becomes trivial); repos are just registrations
within one window's Studio. Downside: two VS Code windows on overlapping folders would need a
clear ownership rule (the current adopt-if-`workspaceRootPath`-matches check, `HostManager.ts:124`,
already gestures at this). Recommend this direction, but it's a real decision with data-location
and adopt-semantics implications — decide before B2.

## Out of scope / follow-ups

- **Leave-on-migrate** (proactively dropping the abandoned repo room's connection) — the
  membership loop never leaves rooms today; a stale empty room is harmless, so defer.
- **Cross-peer local-id → workgroup-id reverse index** for the disambiguated-fork CAS-identity
  case — already flagged as a Phase 7.3 follow-up in `RepositoryRegistryService`
  (`ResolveWorkgroupRepoIdAsync`); this plan does not need it.
- **The snapshot-on-mutation O(n²) storm** in `RuntimeWebSocketLoopRunner` (nodalmerge repo) —
  independent bug, tracked separately; debounce the two `PersistRoomSnapshotAsync` calls.

## Frozen contracts touched (must be amended deliberately, not silently)

- `docs/STUDIO_ROOM_SCHEMA.md` (b) — repoId derivation + **D2** (identity minted-vs-derived,
  and the "never re-derived" rule). Both amendments are argued above; neither changes the
  on-wire `repo-{32 hex}` shape or the `repositories` namespace/value schema.
