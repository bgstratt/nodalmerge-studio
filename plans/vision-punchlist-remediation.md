# Vision Punch-List Remediation — convergence & scoping hardening

**Status:** PLANNED (2026-07-20). Scoping verified against current code on branch `org-knowledge-scope`.
Design decisions settled 2026-07-20 (see “Settled decisions”).
**Supersedes nothing** — consolidates the deferred tails of three shipped plans:
[`repo-identity-convergence.md`](./repo-identity-convergence.md),
[`organizational-knowledge-and-workgroup-scope.md`](./organizational-knowledge-and-workgroup-scope.md),
[`scoped-execute-workers-per-profile-models.md`](./scoped-execute-workers-per-profile-models.md).

## Why this exists

A vision/market assessment flagged Studio as ~at-vision on the hard substrate (deterministic convergence,
cross-repo knowledge, scoped orchestration) with a short remaining punch-list. Code verification
(2026-07-20) confirmed three of four remainders are **narrow correctness edges**, not polish — each can
quietly bite a real multi-machine / nested-worker team. This plan closes all four. The `domain`-field
retirement is explicitly **out of scope**.

**Ordering is not a constraint** — all four must land; sequence for convenience. Items 3 and 4 are isolated
with no contract gate (cheapest, fastest to de-risk); items 1 and 2 are one coherent feature (below).

## The four items

| # | Item | Subsystem | Severity |
|---|------|-----------|----------|
| 1 | Repo-identity re-resolution + repair, **user-initiated only** | Storage / identity | Correctness (degraded/shallow clones, simultaneous mint, already-diverged installs) |
| 2 | Re-link / match-up repo UI | VS Code extension | Delivery vehicle for #1 — **same feature** |
| 3 | Stale-copy-on-rescope (knowledge) | Storage / projections | **Correctness — narrowing re-scope silently reverts on peers** |
| 4 | Grandchild fan-out credential resolution | Orchestrator / agent-runtime | Correctness — nested scoped worker runs on wrong model, silent |

> **Items 1 and 2 are now one feature.** With re-resolution user-initiated (settled decision S1), there is no
> background re-resolution path — the *only* way it runs is the UI action. Build them together.

## Settled decisions

- **S1 — Re-resolution is USER-INITIATED ONLY.** No automatic re-resolution on inbound packs. We do not
  re-resolve an already-resolved binding unless explicitly asked. **The coordinator hook from earlier drafts
  (`RehydratableRefreshCoordinator` workgroup early-return) is DROPPED** — do not implement it. Surface is an
  **"Auto re-link"** action (unambiguous match only) **plus a manual picker** for everything else.
- **S2 — Persist identity hints** on `RepositoryV1` (survives restart; powers display + provenance).
  **Corollary unlocked by S1:** because re-link is an explicit user action, it **may re-read git fresh** at
  that moment. This is the elegant fix for the stuck population — a shallow clone later un-shallowed now has
  a root SHA and can converge on demand, which no cached-hints design could achieve.
- **S3 — Repair = accept-orphan, but never silently.** Flip `WorkgroupRepoId`; leave old-room content in
  place (on disk, out of the read fan-out). **The re-link confirmation must state what will stop appearing**
  (counts of work units / pathways in the current room). Do **not** build re-key/replay unless a concrete
  need appears.
- **S4 — Item 3 fix = Option C** (`ScopeVersion` + max-version tiebreak). Confirmed.

---

## Items 1 + 2 — User-initiated repo re-link (re-resolution, repair, and UI)

### Problem
Deterministic `repoId = "repo-"+sha256(sorted distinct root-SHAs)[..32]` shipped (commit `8fe6fc3`) and
converges two strong-signal clones with zero replication dependency. Remaining:
- **Degraded-hint repos** (shallow / empty / no-HEAD → empty root-SHA set → guid fallback) and the rare
  simultaneous-mint fork race stay on a per-peer guid **forever**. Nothing ever re-runs `Match`.
- **Already-diverged installs** have no repair path.
- The backend disambiguation endpoints exist but **no client UI consumes them**; and the existing resolve
  endpoint **no-ops on a settled binding**, so the real diverged case is unreachable from any surface.

### Key seams (verified)
- `RepositoryRegistryService` — `Match` runs **only** in `RegisterAsync:132` → `BindToWorkgroupAsync:203`
  (one-shot). `ResolveDisambiguationAsync:262` is the human-override commit point but **no-ops when
  `PendingDisambiguation is null`** (`:267-268`) — this is why a settled/diverged binding can't be re-pointed
  today. `RehydrateAsync:529` / `RefreshAsync:568` never re-Match.
- **Hints not persisted.** `RepositoryV1` (`Repository.cs:54-60`) holds
  `RepositoryId, Path, Label, RegisteredAt, WorkgroupRepoId, PendingDisambiguation` — no hints.
- **`WorkgroupRepoId` is mutable** (record `with` + `WriteNodeAsync`); `RepositoryId` immutable. Flipping it
  re-points room membership (`BoundRepoRooms.cs:35-44`, 5s loop) and write-routing (`:58-66`)
  **automatically and live — no host restart** (`RoomPeerClient.cs:191-231`). Already-written repo-scoped
  nodes in the old room do **not** move (→ S3 accept-orphan).
- **Matcher boundary to reuse:** `RepositoryIdentityMatcher.Match`
  (`WorkgroupRepositoryDirectory.cs:82-139`) — `Matched` (shared root SHA + intersecting-or-empty remotes)
  vs `NeedsDisambiguation` (shared root SHA + disjoint non-empty remotes = genuine fork, must never
  auto-collapse).
- **Schema:** `STUDIO_ROOM_SCHEMA.md` amended first-contact derivation (`:198-220`) but still forbids
  re-derivation at `:190-192`, `:251-252`, `:325-327`.
- **Extension:** host env baked from `workspaceFolders[0]` at spawn (`HostManager.ts:234`); **zero**
  folder/config watchers. `nodalmerge.repositoryPath` is already per-request (`repositoryPath.ts:5-9`).
  **Pattern to copy:** `handleAddReference()` (`ArtifactExplorerPanel.ts:342-416`) — `showQuickPick` + REST,
  no webview. Second precedent: `reconcileBlobOrigin` palette command (`extension.ts:128-157`).
  REST helpers `ArtifactExplorerPanel.ts:1657-1674`.

### Design
A single user-invoked flow, exposed as a command and backed by one new service operation. **Nothing runs on
its own.**

**Backend — `RepositoryRegistryService.RelinkAsync(repositoryId, mode, chosenRepoId?, ct)`**
- Re-reads git hints fresh for the target repo (sanctioned: explicit user action — S2 corollary), updates the
  persisted `Hints`, then re-runs `MatchAsync` against the live workgroup map.
- `mode = Auto`: commit **only** on an unambiguous `Matched` result (and only when it differs from the
  current binding). On `NeedsDisambiguation`, degraded/empty signal, or no match → **do not commit**; return
  the candidate set for the manual picker. Deterministic tiebreak for duplicate entries =
  lexicographically-smallest `repoId` (both peers converge independently, no coordination).
- `mode = Manual`: commit the user's `chosenRepoId`, or `register-new` to mint a fresh room (a deliberate
  split/fork).
- **Generalizes past the pending-only limit** — works on a settled binding, which is the whole point
  (replaces the blocked `ResolveDisambiguationAsync` path for re-pointing; keep that method for the
  first-contact pending case).
- Sets `BindingProvenance = HumanResolved` on commit.
- **Pre-commit impact report:** returns counts of repo-scoped nodes in the current room that will leave the
  read fan-out, so the UI can warn (S3).

**`RepositoryV1` additions (additive, zero migration):**
- `Hints` (persisted identity hints — S2)
- `BindingProvenance` enum: `Deterministic` / `ProvisionalMint` / `HumanResolved` — explicit, not inferred.
  Drives what the UI shows and marks human-resolved bindings so nothing downstream second-guesses them.

**REST** (`StudioRestEndpoints.cs`, alongside the existing `:5325-5377` identity endpoints):
- `GET /studio/repositories/{id}/identity` — extend to also return `bindingProvenance`, current hints, and
  whether an auto-relink would change anything (dry-run).
- `POST /studio/repositories/{id}/identity/relink` — `{ mode: "auto"|"manual", chosenRepoId? }`.

**UI — `nodalmerge.relinkRepository` command** (register `constants.ts:15-25` + `package.json`
`contributes.commands`, wire in `extension.ts`). Command + quickpick, **no webview**, no smoke-harness change,
no restart:
1. `GET /studio/repositories` → pick repo (default = active folder; reuse `handleAddReference`'s
   register-then-continue if unregistered — `/identity` 404s on unregistered, `StudioRestEndpoints.cs:5331`).
2. `GET .../identity` → show current binding + provenance + whether auto-relink would change it.
3. Offer **"Auto re-link"** (enabled only when the dry-run reports an unambiguous match) and
   **"Resolve manually…"** (quickpick of candidates + explicit **"Register as new (splits into its own
   room)"**, labeled as a fork).
4. **Confirmation showing the orphan impact** (S3) before committing.
5. `POST .../identity/relink` → toast + `StudioShellPanel.current?.refresh()`; note room join settles in ~5s.

**Schema amendment (narrowed by S1).** Apply to `STUDIO_ROOM_SCHEMA.md` (`:190-192`, `:251-252`, `:325-327`):
identity may be re-resolved **only on explicit user action**, never automatically, never as a background or
event-driven process. A confirmed binding is never re-identified on its own. This is a materially smaller
contract change than the earlier auto-re-resolution draft — frozen contract, make deliberately.

### Phases
- **R1 — Storage additions.** `Hints` + `BindingProvenance` on `RepositoryV1`; populate at `RegisterAsync`;
  persist. Additive, independently testable.
- **R2 — `RelinkAsync`.** Fresh-hints re-read, re-Match, Auto/Manual modes, smallest-id tiebreak, provenance
  stamp, impact report. Works on settled bindings.
- **R3 — REST.** Extend `/identity` (dry-run + provenance), add `/identity/relink`.
- **R4 — Schema amendment.** Narrowed user-initiated-only text.
- **R5 — Command + quickpick UI.** As above, incl. orphan-impact confirmation.
- **R6 (optional, deferrable) — in-panel surfacing.** Show current `workgroupRepoId` + "Re-link…" button in
  the goal workspace (`ArtifactExplorerPanel.ts:2011`, `goalWorkspace.js:1691`). Costs webview JS +
  `getFragment` smoke wiring.
- **R7 (optional, independent) — lifecycle watchers.** `onDidChangeWorkspaceFolders` /
  `onDidChangeConfiguration` + register-all-open-folders. Orthogonal.

### Tests
- **Auto-relink converges a provisional binding**: peer B bound provisionally (degraded hints → guid);
  A's entry visible; relink(Auto) → B's `WorkgroupRepoId` migrates to canonical.
- **Un-shallowed clone converges** (the S2-corollary case): repo registers with empty root-SHA set → guid;
  git gains root history; relink(Auto) re-reads fresh hints → converges. *This case is unreachable without
  fresh re-read — it is the anchor test for S2.*
- **Fork is never auto-collapsed**: shared root SHA + disjoint remotes → relink(Auto) commits nothing,
  returns candidates.
- **Settled strong-signal binding**: relink(Auto) is a no-op (nothing to change).
- **Already-diverged repair**: two rows, same root SHA, matching remotes, different `WorkgroupRepoId` →
  relink(Auto) re-points loser to winner; old-room content left in place; impact report non-zero.
- **Manual mode**: commits `chosenRepoId`; `register-new` mints a distinct room; provenance = `HumanResolved`.
- **Simultaneous-mint collapse** (the currently-RED anchor test 0.3): both mint against empty maps; after
  relink both independently pick lexicographically-smallest → exactly one survivor.
- **No automatic re-resolution** (S1 guard): apply an inbound workgroup pack; assert **no** binding changes.
  Keep green: `A_cached_binding_is_never_reidentified_after_hints_change`
  (`RepositoryRegistryWorkgroupBindingTests.cs:155-172`).
- Extension: manual/host smoke — two folders, relink, room join within ~5s, pathways appear.

---

## Item 3 — Stale-copy-on-rescope (knowledge) — **real correctness bug**

### Problem (worse than "storage waste")
`SetScopeAsync` (`ArtifactLineageService.cs:147-166`) and `ElevateToWorkgroupAsync` (`:128-142`) **mutate the
ArtifactRef in place and write only to the destination room** — the prior room keeps the old payload under
the **same** engine-map key (`studio/artifact-ref/v1/{artifactId}`, identical across rooms).
`ReadAllNodesAsync` (`NodalMergeStudioNodeStore.cs:837-897`) collapses all rooms' copies of an ArtifactId
into one, choosing by **room precedence (workgroup > repo > studio), not recency**. So every **narrowing**
re-scope (and repo→repo) resolves to the **stale broader copy** → the re-scope **silently reverts** on other
peers and after the writing peer's next `RefreshAsync`/restart. The writing peer looks correct only because
its in-memory `_byId` was mutated in place and isn't re-read until a refresh.

| Re-scope | winner | result |
|---|---|---|
| Private → Workgroup(+repo/+all) | new | ✅ |
| Workgroup → **Private** | stale broader | ❌ reverts |
| Workgroup+all → **Workgroup+repo** | stale (workgroup) | ❌ reverts |
| repoX → repoY | nondeterministic foreach | ❌ flaky |

### Why tombstone / eviction don't work (verified)
- **Eviction infeasible:** `EngineRoomMap` has **no delete/remove/evict primitive** (only
  Set/WriteEntry/promote/persist) — per-artifact eviction is net-new CRDT/FFI surface.
- **Tombstone-via-lifecycle fails twice:** the injection reader
  (`ProjectionManager.BuildAgentWorkspaceAsync:360-377` → `GetGlobalConstraintsAsync:271-276`) does **not**
  filter by `Status` at all; and the EntityId-collapse can't hold a tombstone + live copy under one
  ArtifactId — if the tombstone wins the tiebreak the constraint **vanishes** instead of narrowing.

### Fix — Option C (S4)
Add an **additive, `default 0` `ScopeVersion`** to `ArtifactRef.cs` (idiomatic — `Reach`, `RepositoryId`,
`PromotedFromArtifactId` were all added this way, zero migration). **Bump it in `SetScopeAsync` and
`ElevateToWorkgroupAsync`.** Change `ArtifactRefV1` collision resolution in `ReadAllNodesAsync` (`:837-897`)
to **keep the max `ScopeVersion`** rather than last-room-wins (defensive parse: missing ⇒ 0). Fixes
narrowing, widening, repo→repo, and multi-hop in one move; no delete primitive, no injection status change.
**Must bump on every scope mutator** or the tiebreak silently regresses — cover both in tests.

### Sub-phases
- **S1 — stale-copy correctness (Option C).** The item itself.
- **S2 (optional, orthogonal) — injection status hygiene.** Decide whether injection should skip
  `Invalidated`/`Superseded` constraints (today it does not). Independent.
- **S3 (optional follow-on) — hard enforcement.** `PolicyGateService` **already exists and blocks**
  (`PolicyGateService.cs`; checkpoints `ServiceContracts.cs:2174-2199`; enforced `FanOutService.cs:506`,
  `MergeCommandService.cs:86,404`; ships with zero rules = no-op). Advisory→blocking for constraints =
  **one new `IPolicyRule`** over the (now scope-correct) approved-constraint set at
  `ProposalCreated`/`BeforeMerge`. No gate changes. Cleanly separable; sequence after S1.

### Tests
Existing `Scope_endpoint_moves_a_constraint_between_reach_cells` (`ConstraintEndpointsTests.cs:151-192`)
reads through live `_byId` **without refresh** → never exercises the stale copy. New: record
`Workgroup+repoA` constraint → `SetScopeAsync` → `Private` (**narrowing**) → force `RefreshAsync`/rehydrate →
assert `GetGlobalConstraintsAsync` / `BuildAgentWorkspaceAsync` reflect the **new** scope; use
`TryReadEngineMapValue` to confirm the resolved winner. Add `repoX → repoY`. The "old-room copy no longer
wins" assertion belongs in `RoomPerRepoTests.cs:707-734`.

---

## Item 4 — Grandchild fan-out credential resolution

### Problem (correctness, silent)
`profileCredentials` are registered **only under the goal ROOT work unit**
(`_goalCredentialRegistrations` keyed by `workUnitId` = root at spawn,
`InMemoryAgentRuntimeService.cs:17,79-87,783-799`). `FanOutService.cs:494-496` resolves creds keyed by
**`parentWorkUnitId`**. At **grandchild** depth (recursive fan-out from a sub-plan / reconciliation unit,
`GoalCoordinator.cs:63-68`) the parent is an intermediate unit → `GetCredentialsForProfile` returns **null**.
**Worse:** the base fallback `creds` (`:123-124`) is *also* keyed by `parentWorkUnitId`, so **both** profile
and stage/default creds resolve null — the grandchild enqueues with `Model == null`, silently, no error.
**`GoalCoordinator`'s Plan path has the identical defect** (`:172-190`) — Plan is not always at root.

### Fix — Option A: resolve the root, key all creds lookups by it
No `RootWorkUnitId`/`GoalId` exists on the domain `WorkUnit` (`WorkUnit.cs:38-118`; only nullable
`ParentWorkUnitId:49`). Root = ancestor with `ParentWorkUnitId is null`. **Walk the parent chain** (bounded
32-hop, mirroring `ContinueService.cs:248-255`; stop on missing parent as `ProjectionManager.cs:1456-1458`).
FanOutService **already depends on `IWorkUnitService`** (`:25,52`) → zero new deps. Resolve `credRootId` once
per `ProcessAsync`, then key **all three** calls (`GetCredentialsForStage`, `GetGoalDefaultCredentials`,
`GetCredentialsForProfile`) by it. Fixes profile-creds and base-creds at arbitrary depth.
- **Reject B** (add/backfill `GoalId` on the domain model — larger surface).
- **Reject C** (fan credential registrations incl. real ApiKeys out to every child — secret-fan-out surface).
- **Cover both paths:** same walk in `GoalCoordinator.EnsurePlannerAsync` (`:172-190`; resolves
  `IWorkUnitService` via `serviceProvider`).
- **Noted, out of scope:** siblings using a *one-level* parent fallback, still wrong at depth ≥ 3 —
  `InlineReviewerService`, `DomainAgentTriggerService`, `AutomatedReviewGateService`, `WorkUnitCommandService`,
  `ReconciliationAgentService`, `ReplanService`, `InMemoryDeadLetterService`. Durable follow-up: an
  `IAgentControlService.GetCredentials…InLineage` overload that walks internally.

### Tests
Clone `Matched_profile_with_bound_Model_Profile_enqueues_child_on_that_model`
(`FileScopeProfileRoutingTests.cs:134-192`) → `Matched_profile_bound_model_resolves_for_grandchild_fanout`:
root + `mid` (child of root) → register creds on **root only** → Plan artifact with a `.tsx` slice on
**`mid`** → `TryFanOutFromPlanAsync(mid)` → assert grandchild `Model == "sonnet-frontend"`,
`BaseUrl == "http://frontend"` (asserts-fails today: `Model == null`). Companion GoalCoordinator Plan-side
test. Back-compat guard: depth-2 profile-*without*-binding still inherits the stage default.

### Risks
Bounded walk caps cycles (32 hops); missing parent mid-walk → use last resolved id. Depth-1 semantics
unchanged (`credRootId == parentWorkUnitId`) so existing routing tests stay green. Root resolution keys into
the rehydrated `_goalRouting` twin → survives host restart identically to today.

---

## Status ledger (update as phases land)
- [x] **Item 4 — root-walk creds (FanOutService + GoalCoordinator) + grandchild tests — DONE 2026-07-20.**
      `ResolveCredentialRootAsync` (bounded 32-hop walk to the `ParentWorkUnitId is null` root) added to
      both `FanOutService` and `GoalCoordinator`; all credential lookups (`GetCredentialsForStage`,
      `GetGoalDefaultCredentials`, `GetCredentialsForProfile`) now key by the resolved root.
      `EnqueueChildWorkerAsync` takes `credRootId` (immediate `parentWorkUnitId` still used for policy
      context + decision log, which correctly refer to the parent). Two new tests in
      `FileScopeProfileRoutingTests`: `Matched_profile_bound_model_resolves_for_grandchild_fanout` and
      `Grandchild_fanout_without_a_bound_model_inherits_the_stage_default`. **Verified by neutering the
      walk — exactly the 2 new tests fail, the 6 pre-existing pass**, confirming depth-1 behavior is
      unchanged and the tests genuinely catch the bug. Full solution builds clean; AgentRuntime.Tests
      109/109; Integration.Tests 734/736 with the 2 failures (`MultiUserMilestoneTests
      .Two_peers_one_repo_goal_appears_cold_materializes_and_edits_land`, `FileScopeAmendmentTests
      .SetFileScopeAsync_throws_for_terminal_work_units(Completed)`) **confirmed pre-existing flakes —
      reproduced on a clean stashed baseline, and they pass in isolation.**
- [x] **Item 3 S1 — `ScopeVersion` + max-version tiebreak — DONE 2026-07-20.** Additive `int ScopeVersion = 0`
      on `ArtifactRef`; bumped by both scope mutators (`SetScopeAsync`, `ElevateToWorkgroupAsync`);
      `ReadAllNodesAsync` routes all three room-collection loops through a new `UpsertResolvingScope`,
      which for `ArtifactRefV1` keeps the higher `ScopeVersion` and otherwise preserves the historical
      last-room-wins overwrite. Ties keep the incoming value, so never-re-scoped artifacts (all at 0)
      resolve exactly as before. `ReadScopeVersion` is defensive (missing/non-numeric/malformed ⇒ 0).
      Two new tests in `RoomPerRepoTests`: `Narrowing_a_constraints_scope_is_not_reverted_by_the_stale_room_copy`
      (asserts both room copies genuinely coexist, then that the fan-out *and* a live `RefreshAsync`
      both resolve to the narrowed copy) and `Re_scoping_a_constraint_between_two_repos_resolves_to_the_newer_room`.
      **Verified by neutering the resolution — exactly the 2 new tests fail, the other 13 pass.**
      Solution builds clean; Integration 737/738 (sole failure the pre-existing `MultiUserMilestoneTests`
      flake); AgentRuntime 109/109, Core 42/42, Contracts 24/24, Tasks 14/14, Projections 37/37, Merge 71/71.
      *Note: `ScopeVersion` must be bumped by any future scope mutator or the tiebreak silently regresses.*
- [x] **Item 3 S2 — injection status hygiene — DONE 2026-07-20.** `IsInjectableConstraint` blocklist
      (`Status is not (Invalidated|Rejected|Superseded) && InvalidatedByArtifactId is null`) applied at the
      `ProjectionManager` injection fold, covering **both** the global and ancestor-chain arms. Deliberately
      a blocklist, not an allowlist of {Active, Approved}: globals are created `Approved` and lineage
      constraints `Active`, so an allowlist missing either would zero out the feature, and guidance is
      advisory so failing open is the right default. Filter placed at the fold, **not** in
      `GetGlobalConstraintsAsync` — that query also feeds `/studio/constraints/proposed`'s
      `PromotedFromArtifactId` dedupe, where excluding invalidated globals would wrongly re-offer promotion.
      **The arm that actually bites is the lineage one:** no product path moves a global off `Approved`,
      but lineage constraints are created `Active` and *can* be retired (agent `nm_v1_artifact_invalidate`,
      or the `InvalidateAsync` cascade stamping `InvalidatedByArtifactId`) — and were still being injected.
      This also reconciles two surfaces that disagreed: `.workspace/constraints.json` already filtered by
      `IsCurrent` while native prompt injection did not. Tests
      `Retired_constraints_are_excluded_from_injection` + `Invalidated_ancestor_constraints_are_excluded_from_injection`;
      **verified by neutering the predicate — exactly the 2 new tests fail, the other 3 pass.**
      *Known gap (deliberate):* the *derived* "superseded by a newer artifact" relation is branch-relative
      and only computable by the EngineeringState reverse index, so a constraint retired purely via another
      artifact's `Supersedes` list still injects. Closing it means reusing that fold here — larger change.
- [x] **Item 3 S3 — RESOLVED 2026-07-20 as "constraints stay advisory". Steps 0 + 0.5 DONE; Step 1 DEFERRED.**
      Product decision (user): constraints are **guidance, not gates** — "there seem to be exceptions to every
      rule; get your goal complete and explain" — and **correct work being rejected is the higher risk** than
      a violation slipping through. No blocking rule will be built.
      **What investigation found:** the reviewer already enforces constraints by LLM judgment
      (`AutoReviewRule` blocks on rejection), and already attests what it weighed
      (`MergeProposal.ConsideredArtifactIds`). But (a) that attestation was rendered in **zero** UI —
      `clients/` had no match for `considered`, and the purpose-built `GET /studio/artifacts/{id}/feedback`
      had **no caller**; and (b) the worker and reviewer prompts **contradicted each other**: the worker was
      told constraints are guidance to "apply unless this work unit's goal explicitly says otherwise", while
      the reviewer was told a violation is "grounds for rejection even if the diff otherwise looks reasonable"
      and listed it as a flat defect beside build/test failure. A worker correctly exercising its license got
      rejected by a reviewer under a stricter rule — and the reviewer could not even see the worker's
      reasoning, since no such record exists.
      **Step 0 (DONE) — surface what already existed.** `consideredArtifactIds` now renders in Merge Review's
      Evidence section; the orphaned `/feedback` endpoint is wired to a per-constraint "Usage" affordance on
      the Insights Constraints tab (on-demand, one request per constraint asked about, not N per tab open).
      Both surfaces state plainly that a review records the constraint was **weighed, not followed** — an
      Approved proposal carries an identical list whether the constraint was obeyed or departed from.
      **Step 0.5 (DONE) — resolve the contradiction.** Reviewer prompt now treats a departure the goal
      justifies as correct work to note in `verificationResults`, and rejects only an unjustified departure;
      when it cannot tell why, it prefers approving with the departure called out. Mirrors the wording the
      same prompt already used for `fileScope` ("a rejection here throws away correct, working code the user
      already paid tokens for"). Three sites: `AgentLoopPrompts.Reviewer` step 2 and Rules,
      `ReviewerAgentLoop`'s `ArtifactQuery` tool description.
      **Step 1 (DEFERRED, revisit before any further tuning) — worker-side deviation record.** Genuinely does
      not exist: no field, MCP tool, event, or `.workspace/` slot lets a worker say "I departed from C-7
      because Y", and no worker prompt asks. Precedent to follow when built: `noFileChangesJustification`
      (opt-in gate → agent supplies free text → persisted on the proposal). Open design question recorded
      then: proposal field (simpler, replicates with the merge) vs `Decision` artifact (rides lineage +
      scope rules, so it reaches future work units).
      **Known limit of the whole approach:** self-reporting only ever captures *conscious* deviation. An agent
      that never noticed a constraint applied does not believe it deviated, so no prompting extracts a reason
      — the reviewer stays the only thing catching unaware violations. Surface any future reason as "agent's
      stated reason", since asking an LLM to justify reliably produces a justification.
      **Watch after 0.5:** it loosens the reviewer *before* Step 1 gives it a stated reason to weigh, so it is
      currently inferring justification from the diff alone. If violations start sliding through, that is the
      signal to build Step 1 rather than re-tighten the prompt.
- [ ] ~~Item 3 S3 — hard enforcement~~ — **NOT BUILDING.** Superseded by the entry above. Retained for the
      reasoning: no machine-checkable field exists on a Constraint (Title/Body are free text), the LLM path
      that can evaluate one is already wired and already blocking, and constraints are **per-peer disableable**
      (Phase 3 toggle) — so a blocking policy would gate merges on a set that differs per machine.
      S3 assumed "advisory→blocking = one new `IPolicyRule` over the constraint set." Investigation found
      that is not buildable as written: **no machine-checkable field exists on a Constraint** (Title/Body are
      free text; no predicate, pattern, rule-kind, severity, or tag), so a deterministic rule has nothing to
      evaluate. The LLM path that *can* evaluate it is **already wired and already blocking** — the reviewer
      is handed the constraints, `ReviewerAgentLoop` tells it "a change that violates a recorded Constraint
      is grounds for rejection", and `AutoReviewRule` already fails the merge on rejection. An LLM-judge rule
      would be a second, worse reviewer at the highest-blast-radius checkpoint.
      **The blocking contradiction:** constraints are per-peer disableable (Phase 3 toggle,
      `ProjectionManager` subtracts local disables), so a "blocking" policy would gate merges on a set that
      **differs per machine**. `organizational-knowledge-and-workgroup-scope.md:238-240` wants hard
      enforcement; `:186-190` and the injected prompt text deliberately preserve advisory framing. Resolve
      *"can a constraint block a merge at all, or is per-peer opt-out the intended control surface?"* before
      this has a well-defined target.
      **If something concrete is wanted meanwhile:** a deterministic `ConstraintAttestationRule` at
      `BeforeMerge`, off by default behind a `WorkspaceOptions` flag, asserting the applicable constraint set
      ⊆ `MergeProposal.ConsideredArtifactIds` (already populated by `nm_v1_merge_review`). ~80 lines, zero new
      domain fields. Honest framing: it proves each constraint was *weighed*, not *obeyed* — which is exactly
      what the existing design already promises.
- [x] **Items 1+2 R1–R5 — DONE 2026-07-20. User-initiated repo re-link shipped end to end.**
      **R1** `RepositoryV1` gained `Hints` (persisted, so the identity view and future re-links need no
      git read) and `Provenance` (`Deterministic` / `ProvisionalMint` / `HumanResolved`) — explicit, not
      inferred. Both additive/nullable = zero migration. The enum carries
      `[JsonConverter(typeof(JsonStringEnumConverter))]`: the host registers that converter for HTTP, so a
      numeric-by-default enum round-tripped inconsistently (caught by `CrossRepoFileReferenceTests`), and
      more importantly the value is *persisted* — as a bare ordinal, inserting an enum member later would
      silently change what every stored row means.
      **R2** `IRepositoryRegistryService.RelinkAsync(repositoryId, mode, chosenRepoId, commit)`. Re-reads git
      **fresh** (sanctioned because it is user-initiated — and it is the only thing that can rescue a clone
      that was shallow at register time), re-matches against the live workgroup map, and **works on an
      already-settled binding** — the gap `ResolveDisambiguationAsync` could not close, since it no-ops
      unless a disambiguation is pending. Auto commits only an unambiguous `Matched`, never moves a
      `HumanResolved` binding, and commits nothing on fork/degraded/multi-candidate (returns candidates,
      ordered deterministically). Manual commits a choice or `register-new`. `commit:false` previews with no
      writes.
      **Orphan impact**: new `IStudioNodeStore.CountRepoRoomNodesByKindAsync` (default-impl empty, real
      enumeration only in `NodalMergeStudioNodeStore`) reports what is in the room being left, so the flow
      warns before committing. Content is **not** migrated — accept-orphan per the settled decision.
      **R3** `GET .../identity` now returns `provenance` + `hints` (cheap, never touches git);
      `POST .../identity/relink` takes `{mode, chosenRepoId, commit}` and returns proposal + candidates +
      impact.
      **R4** `STUDIO_ROOM_SCHEMA.md` amended in all three no-re-derivation clauses plus a new
      "User-initiated re-link" section. The rule narrowed to *"never re-derived **on its own**"* — explicit
      human action only, no background/timer/startup/inbound-pack path may ever trigger it. Materially
      smaller than the auto-re-resolution amendment the earlier draft would have needed.
      **R5** `nodalmerge.relinkRepository` command + quickpick (no webview, no smoke-harness change, no host
      restart — the membership loop joins the new room in ~5s). Previews first, then a **modal**
      confirmation naming the room being left and how many records stop appearing.
      **Tests** (`RepositoryRelinkTests`, 7): un-shallowed clone converges; already-settled divergent binding
      re-points; preview writes nothing; fork never auto-collapses; human-resolved binding untouched;
      `register-new` splits; and a guard that a better match appearing changes nothing until asked.
      **Falsified both load-bearing claims**: replacing the fresh git read with cached hints fails exactly
      the 4 tests that depend on it; disabling the `HumanResolved` guard fails exactly that test.
      Solution builds clean; extension tsc clean + webview smoke PASS; **Integration 747/747**; Core 42/42,
      Contracts 24/24, Tasks 14/14, AgentRuntime 109/109, Merge 71/71, Projections 37/37.
- [x] **Items 1+2 R6/R7 — DONE 2026-07-20.** A command-palette-only flow left the binding invisible, which
      is most of the problem: the room id appeared nowhere, so "why can't I see my teammate's work?" had no
      answer short of curling REST.
      **R6** The goal workspace's repo-path row now shows the bound room and a "Re-link…" button.
      Three distinct states, because the difference is exactly what a confused user needs: *not registered*,
      *registered but no room* (nothing replicates), or *room {id}*. `sendRepositoryRoom` matches the
      effective repository path against `/studio/repositories`; sent separately from `explorerSettings` so a
      REST round trip can't block or fail the settings render. The button delegates to the command so the
      preview / candidate-pick / orphan-confirmation flow lives in one place.
      **R7** `onDidChangeWorkspaceFolders` and `onDidChangeConfiguration` watchers. The config one just
      repaints (`repositoryPath` is per-request). The folder one repaints **and offers a host restart**,
      because the host bakes its data dir and workspace root at spawn — silently showing stale state, or
      restarting behind the user's back, would both be worse than asking.
      **Bug found and fixed while wiring this** (surfaced by the user asking whether an already-linked repo
      could be split off): `register-new` mints nothing on a dry run, so `willMove` was false and the
      preview reported **no orphan impact** — splitting a bound repository was the one path that would have
      orphaned its old room's content with no warning. `RelinkAsync` now tracks the split separately, and
      the command previews the *specific* chosen action rather than reusing the initial auto preview (each
      choice leaves a different room behind). Test
      `RoomPerRepoTests.Relink_preview_reports_what_the_repository_would_leave_behind` covers it against the
      real node store (the in-memory double's default-impl count returns empty, so this could not be tested
      in the `RepositoryRelinkTests` harness); **verified load-bearing by neutering the split-is-a-move
      flag — exactly that test fails.**
      Extension tsc clean + webview smoke PASS; Integration 748/748 on a clean run; all other suites green.
- [x] **Register-the-open-folder in the re-link flow — DONE 2026-07-20.** Found by opening a fresh repo:
      the command dead-ended with "no repositories are registered yet". Registration otherwise happens
      only as a side effect of goal creation, so the very first state a new user is in had no path
      forward. The picker now offers **"Register the open folder"** first when the active folder isn't
      registered (`POST /studio/repositories`, which creates the dir if needed, `git init`s a non-repo,
      and registers), then continues into the normal re-link flow with the new id. The in-panel button
      relabels to **"Register…"** when unregistered — the same flow handles both, but "Re-link" does not
      read as the way to register something.
- [x] **Test-hygiene sweep — DONE 2026-07-20 (partial win; read the numbers before assuming it's solved).**
      Categorised all 164 build sites by enclosing member rather than bulk-replacing: **131 are test
      methods** (app is local — safe to dispose) and **33 are helpers that return services or tuples**, where
      the container must outlive the helper and `await using` would dispose it before the caller ran. 95 of
      the test-method sites lacked disposal and now have `await using`; the 33 helpers were deliberately left
      (fixing them means changing what each helper hands back — a separate, larger refactor).
      One compile fallout: `StudioHostSmokeTests.Build_registers_studio_services` was `void`, now `async Task`.
      **Measured honestly.** Before: ~2/5 runs failed, on a *different* test each time
      (`MultiUserMilestoneTests`, `FileScopeAmendmentTests`, `FileLeaseConflictIntegrationTests`,
      `ScopedTreeFetchTests`, `ReadBeforeWriteEnforcementTests` — the signature of cross-test interference).
      After: ~2/6 runs, but concentrated on **two specific tests**. The raw failure rate barely moved; what
      collapsed is the *spread*. Cross-test interference is gone; what remains are intra-test races.
      **The remaining failure is characterised, not mysterious:** `ReadBeforeWriteEnforcementTests` fails
      reading `existing.txt` in **its own** temp root while it is "used by another process" — the test drives
      a real `WorkerAgentLoop`, and the loop is still live (writing) when the test reads. Same family as the
      shutdown-contract bug: work outliving the awaited call. `ScopedTreeFetchTests` builds no host at all,
      so its failure is a third, separate cause. Both are now narrow enough to fix directly.
- [ ] **Follow-ups this sweep exposed (not done):** (a) the 33 service-returning helpers still leak a host
      each; (b) `ReadBeforeWriteEnforcementTests` / `ScopedTreeFetchTests` intra-test races above; (c) the
      detector the flake investigation recommended — an assembly fixture failing the run on
      `TaskScheduler.UnobservedTaskException`, which would surface the remaining `_ = SomethingAsync()` sites
      instead of letting them show up as someone else's flake.
- [ ] ~~Original sizing note~~: 163 test sites build a `StudioWebApplication` without disposing it
      (vs 33 that do), leaking background loops and file handles into whatever runs next — the cause of the
      residual "different test each run" flakiness that survived the shutdown-contract fix. Note 6 of those
      sites are helpers that `return app`, where a blind `await using` would dispose before use, so this
      needs a careful pass rather than a bulk replace.
