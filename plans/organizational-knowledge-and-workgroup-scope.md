# Organizational Knowledge & Workgroup Scope

Status: **proposed** (2026-07-19)
Owner: Brad
Supersedes the ad-hoc "should constraints sync?" question with a scoped knowledge model.

---

## 1. Motivation

Two concrete defects surfaced while auditing the Insights tab and constraint flow:

1. **Findings never leave the peer that detected them.** `FindingV1` is written to the peer-private
   `"studio"` room (`FindingService.Persist`, 3-arg `WriteNodeAsync`) and is not in
   `StudioNodeKind.RepoScopedKinds`. The Insights **Findings queue** shows only what this peer
   detected/imported. The manual **Export/Import Findings** buttons exist precisely because findings
   don't replicate.

2. **"Global" constraints are inverted from intent.** A constraint promoted from a finding is created
   with `OwnedByWorkUnitId = null` (`FindingService.PromoteKnowledgeGuidelineAsync`). That null owner
   means `RepositoryId` is never denormalized (`ArtifactLineageService.RecordAsync:49`), so the node
   routes to the peer-private `"studio"` room — **local-only**. A constraint deliberately elevated to
   "applies everywhere" becomes the *least* shared thing in the system: visible only to the machine
   that promoted it.

The larger frame (from design discussion): the Insights tab is becoming an **organizational learning
engine**. The real question is not "should constraints sync" but *what kinds of knowledge become
shared organizational knowledge, under what governance, and at what scope*.

## 2. What already exists (do not rebuild)

The human-gated learning spine is already end-to-end:

```
FindingDetectorService (deterministic thresholds + LLM scan)
        │  Host/FindingDetectorService.cs; ServiceContracts.cs:1517 (IInsightLlmAnalyzerService)
        ▼
Finding  ── human review ──▶ POST /studio/findings/{id}/review  (Promote / Dismiss / Investigating)
        │                    StudioRestEndpoints.cs:5364; FindingService.ReviewAsync:36
        ▼
Constraint artifact (ArtifactType.Constraint, Status.Approved)
        │  FindingService.PromoteKnowledgeGuidelineAsync:77
        ▼
folded into planner + worker kickoff prompts (soft, advisory)
   ProjectionManager.BuildAgentWorkspaceAsync:312-356 (InheritedConstraints)
   InMemoryAgentRuntimeService.BuildConstraintsContextAsync:1243-1262
```

Reusable primitives already present:

- **Immutable-ish objects**: `ArtifactRef` is a `record`; `ArtifactLineageService.RecordAsync` is
  idempotent by id (a second write is a no-op).
- **Supersede / retire lineage**: `ArtifactRef.Supersedes` forward-links; `Status` includes
  `Superseded`/`Invalidated`; `InvalidatedByArtifactId` cascades across the descendant subtree
  (`ArtifactLineageService.InvalidateAsync:140`). `SupersededBy`/`IsCurrent` are *derived* by the
  EngineeringState projection fold, not stored.
- **Room substrate**: rooms are independent, lazily-hydrated, joinable-without-restart replicated
  namespaces; cross-room read fan-out already aggregates a query across `"studio"` + every bound
  `repo/{repoId}` room (`NodalMergeStudioNodeStore.ReadAllNodesAsync:792-837`). Deterministic repo
  identity converges two clones onto one `repo/{id}` room.

What is **missing** for the model in §3: an explicit scope dimension, the workgroup room as a
queryable knowledge namespace, a leave/evict operation, provenance edges from a policy back to its
evidence, and an "import to local" action.

## 3. The model (decisions locked 2026-07-19)

### 3.1 Scope = two orthogonal dimensions (Reach × Application)

Scope is **not** one linear axis. It is two independent questions, authored separately by the human:

- **Reach** — who gets a replicated copy? → `Private` | `Workgroup`
- **Application** — what work does it apply to? → this repo | all repos
  (stored as `ArtifactRef.RepositoryId`; `null` = all repos)

These already map onto existing rooms/fields — **Reach selects the room; `RepositoryId` is the
application filter**:

| Reach ↓ / Applies → | **This repo** | **All repos** |
|---|---|---|
| **Workgroup** | `repo/{repoId}` room — shared with peers bound to that repo | `workgroup` room — shared with every member |
| **Private** | `studio` room + `RepositoryId=X` filter *(local override — deferred, §5)* | `studio` room, no filter |

Routing is a pure function of the two authored fields:

- `Reach=Private` → `studio` room (peer-private, severed both ways by Slice 7.3). `RepositoryId` is
  only an application filter here, never a routing key.
- `Reach=Workgroup` + `RepositoryId=X` → `repo/{X}` room (only peers on that repo receive it — the
  efficient path; this is today's implicit "repository" behavior).
- `Reach=Workgroup` + `RepositoryId=null` → `workgroup` room (all members, all repos = the old
  "global").

**Injection application rule** (independent of reach): a constraint fires for the current work iff
`RepositoryId is null || RepositoryId == currentRepoId`. Reach governs *who has it in their store*
(replication); application governs *when it fires*.

**Authoring UX**: a **radio** (Private / Workgroup) for reach + a **checkbox** (repo-specific) for
application. Three of the four cells are natural authoring cases (Private/all, Workgroup/repo,
Workgroup/all); the fourth (Private + repo-specific) is an *override* case that falls out of the same
two fields at no extra cost and ships with the local-override follow-up (§5).

Why reach tops out at Workgroup: **the workgroup is the widest set of peers that share any room at
all.** Two peers not in a common workgroup have no shared room, so there is no "Organization" tier to
share into — "global" and "workgroup" are the same set.

### 3.2 Persistent + leaveable (not ephemeral)

Workgroup knowledge **replicates and persists locally** (works offline), kept in a separate, clearly
**labeled** namespace so it is never confused with the user's own knowledge. An explicit **Leave
workgroup** action evicts the room's local data. This reuses the durable room substrate as-is and
adds only a leave/evict operation — far cheaper than an ephemeral-room mode, and it matches the
requirement "a user can leave a workgroup if they don't want the constraints persisted."

### 3.3 Governance — three gates

1. **Promote** (already built): nothing enters a *shared* scope without a human promoting a finding.
   An AI never silently changes shared behavior.
2. **Join / Leave**: whether this peer *receives* workgroup knowledge at all. Leave evicts it.
3. **Import to Local**: whether a shared (workgroup/repo) policy becomes *this user's own* — an
   explicit action that creates a new Local-scoped node with a provenance backlink to the shared one.

The Company-A → Company-B safety concern is handled by all three: workgroup policies live in a
labeled, evictable namespace (never silently "yours"); leaving a workgroup removes them; adopting one
permanently is a deliberate Import. The human-apply barrier means a rejoining peer only ever receives
knowledge a human explicitly promoted at the source.

### 3.4 Progressive promotion

Each broadening is a separate, audited human action — matching the immutable-graph philosophy. In the
2×2, promotion authors a shared repo-specific constraint; elevation widens its **application** axis
(this repo → all repos), both at Workgroup reach:

```
Finding ──promote──▶ Workgroup + this-repo ──elevate──▶ Workgroup + all-repos
        (default: shared, repo-specific)    (explicit: RepositoryId → null)
```

(A human may instead promote to `Private` reach for a personal-only note; default is shared.)

### 3.5 Terminology

Keep `Finding` → `Constraint` as the internal types. Adopt the
**Observations → Insights → Recommendations → Policies** vocabulary in the **Insights tab UI only**
(Observations = detector inputs/runs; Insights = findings; Recommendations = open findings; Policies =
approved constraints). No wholesale type rename. A first-class non-constraint "Insight" object
(correlations, perf facts, "give the model schema first") is explicitly **out of scope for now** and
parked in Follow-ups — revisit only if we want to persist/replicate non-constraint knowledge.

## 4. Implementation phases

### Phase 0 — Findings replication — ✅ SHIPPED

Goal: the Insights **Findings queue** is shared among peers on the same repo; the manual
Export/Import stays (it serves *cross*-workgroup / external sharing, a different job).

- Attribute each `Finding` to its source repository (add/populate a `RepositoryId` on `Finding` if it
  isn't already carried from the `RunRetrospective` it was detected over).
- Add `FindingV1` to `StudioNodeKind.RepoScopedKinds` (`StudioNodeStore.cs:112`).
- `FindingService.Persist` (~:112): switch the 3-arg `WriteNodeAsync` to the 4-arg overload with the
  finding's `RepositoryId`; `RehydrateAsync` (~:104) already benefits from the repo-room fan-out.
- **Open item**: confirm whether detection is per-repo or workspace-wide. If a finding is genuinely
  cross-repo, it takes **Workgroup** scope instead (Phase 1 machinery) rather than being forced into
  one repo room. Decide during implementation; default assumption is repo-attributable.
- Leave the Export/Import JSON path untouched — it is the intentional external-sharing escape hatch.

Risk: low. No schema/scope change beyond a `RepositoryId` on findings. Reversible (remove from the
allowlist).

### Phase 1 — Scope as a first-class property — ✅ SHIPPED (incl. elevate)

*(1a/1b routing, 1c injection filter, 1d promotion, and elevate all landed. Restrict-to-private is
folded into the Constraint Management UI below, as the local toggle.)*

- **Add a `Reach { Private, Workgroup }` field** on `ArtifactRef` (`ArtifactRef.cs`) and **reuse the
  existing `RepositoryId`** as the application filter (`null` = all repos). No third enum — the 2×2 in
  §3.1 is `Reach` × `RepositoryId`.
- **Route by (Reach, RepositoryId)** in `ArtifactLineageService.RecordAsync` /
  `NodalMergeStudioNodeStore.WriteNodeAsync`:
  - `Private` → `"studio"` room (3-arg); `RepositoryId` carried as an application filter only.
  - `Workgroup` + `RepositoryId=X` → `repo/{X}` room (4-arg, as today).
  - `Workgroup` + `RepositoryId=null` → the `workgroup` room (**new** route for graph nodes; today only
    the repo/goals directories live there).
- **Make the workgroup room queryable.** Add the `workgroup` room to the read fan-out in
  `NodalMergeStudioNodeStore.ReadAllNodesAsync:792-837` and `ReadNodeAsync:763-776`. This is the
  missing "queries resolve across local + repo + workgroup" wiring.
- **Scope-aware injection.** Replace `GetGlobalConstraintsAsync` usage in
  `ProjectionManager.BuildAgentWorkspaceAsync:353` with a fold over all rooms (studio + repo +
  workgroup), then the application filter `RepositoryId is null || RepositoryId == currentRepoId`.
  Label each constraint's reach/application so the prompt and UI can show origin. Keep the "apply
  unless the goal says otherwise" advisory framing.
- **Promotion sets (Reach, RepositoryId).** `PromoteKnowledgeGuidelineAsync` stops hardcoding `null`
  owner; default is `Reach=Workgroup` + `RepositoryId = source repo` (shared, repo-specific). A
  separate **elevate** action (new endpoint) widens application by setting `RepositoryId = null`
  (→ `workgroup` room); a **restrict-to-private** action sets `Reach=Private` for a local override.

Outcome: a promoted constraint actually replicates to same-repo peers (Repository) or all workgroup
members (Workgroup), and every peer's agent injection sees the combined, scope-correct set.

### Phase 2 — Leave / unmount + eviction — ❌ DESCOPED (2026-07-19)

Not building this. Decision: a user who wants different workgroup knowledge simply **connects to a
different workgroup, or clears the workgroup and restarts the host** — no in-app leave/evict verb, no
`DropRoomAsync`, no per-room eviction machinery. The "persistent + leaveable" model collapses to
"persistent; switch workgroups at the host level." This removes the single biggest net-new piece from
the plan. (The safety concern it addressed — controlling which shared constraints affect *you* — is
handled far more directly by the local toggle in Phase 3.)

### Phase 3 — Constraint Management UI (view + local toggle) — the priority

This is the headline user-facing feature: on the Insights tab, **see every constraint and turn any of
them off for yourself**, without affecting other peers. Split the Insights tab into two sub-tabs
(mirroring Model & Agent Studio's tabbing): **Analysis** (today's RunRetrospective + Findings) and
**Constraints** (this).

- **View.** A `GET /studio/constraints` (optionally `?repositoryId=`) returning every constraint that
  applies to the peer/repo, each labeled with its **reach** (Private / Workgroup) and **application**
  (this repo / all repos), plus its **enabled** state. Grouped in the UI by reach × application.
- **Local toggle = per-peer suppression.** "Turn off" writes a **local, non-replicated** suppression
  record keyed by the constraint's `ArtifactId` (a `ConstraintToggleV1` node in the peer-private
  `"studio"` room — never in `RepoScopedKinds`/`WorkgroupScopedKinds`, so it stays yours). Re-checking
  removes it. `POST /studio/constraints/{artifactId}/toggle` (or enable/disable).
- **Injection subtracts suppressed ids.** `ProjectionManager.BuildAgentWorkspaceAsync`'s constraint
  fold excludes any constraint whose `ArtifactId` is in the local suppression set — so a disabled
  workgroup/repo constraint stops reaching *this* peer's agents while remaining live for everyone else.
- This is exactly the "Private-reach override" the fourth 2×2 cell reserved — implemented as a simple
  on/off suppression rather than a full superseding artifact, which is all a toggle needs.

Backend first (toggle store + view/toggle endpoints + injection filter, all testable headless), then
the webview sub-tabs.

### Phase 4 — Optional / later

- **Provenance edges** (moved down from the old Phase 3): a stored backlink on the Constraint
  (`DerivedFromFindingId` + the finding's source run ids) so "why do we have this policy?" walks
  Policy → Finding → runs → logs. Nice-to-have audit trail; the one-way `Finding.PromotedArtifactId`
  link exists today.
- Numbered version UX over the existing `Supersedes`/`Superseded` primitives.
- Hard policy enforcement: wire approved Constraints into `PolicyGateService` (today a *separate*
  merge/review gate, not connected to Constraint artifacts) for policies that should *block*, not just
  advise.
- ~~Import-to-Local~~ — **dropped.** With no leave/evict and a local toggle, there's no need to
  permanently copy a shared constraint into a private scope; the toggle already gives per-peer control.

### Phase 4 — Optional / later

- Numbered version UX over the existing `Supersedes`/`Superseded` primitives.
- Hard policy enforcement: wire approved Constraints into `PolicyGateService` (today a *separate*
  merge/review gate, not connected to Constraint artifacts) for policies that should *block*, not just
  advise.

## 4.2 Migration

- **Legacy null-owner "global" constraints** (currently local-only in `"studio"` due to the bug): on
  introducing `Reach`, default them to **`Reach=Private`, `RepositoryId=null`**. This preserves their
  *current observable behavior* (local-only, applies to all my work) and avoids a surprise
  mass-replication. Humans re-promote to Workgroup explicitly. (Auto-assigning Workgroup is rejected as
  surprising.)
- **Existing work-unit-owned constraints**: `Reach=Workgroup` with their already-denormalized
  `RepositoryId` — they *already* replicate via the repo room today, so behavior is unchanged.

## 5. Follow-ups (deferred, not in the phases above)

- *(The "applied/applicable constraints view + local override" that was here is now **Phase 3** above —
  promoted to the priority feature per Brad, 2026-07-19.)*
- **First-class non-constraint Insight objects** (correlations, perf facts, model-behavior tips) —
  only if we decide to persist/replicate knowledge that isn't a policy.
- **Export/Import JSON** stays as the external / cross-workgroup sharing path — no change.
- **User-scope preferences** beyond overrides (e.g. "Brad prefers concise commits") — a Local-scope
  application, revisit when there's a consumer.

## 6. Risks & open questions

- **Finding scope granularity** (Phase 0 open item): repo-attributable vs workspace-wide detection.
- **Workgroup room as a graph namespace**: it currently carries only directory namespaces
  (`WorkgroupRepositoryDirectory`, the `WorkgroupGoalDirectory` stub). Adding general graph nodes must
  not collide with those namespaces — use a distinct map namespace for artifacts.
- **Eviction correctness** (Phase 2): dropping a room's persisted data must not corrupt compaction
  checkpoints of *other* rooms; scope `DropRoomAsync` strictly per-room.
- **Injection volume**: Workgroup-scoped policies go to every member's every agent; keep the existing
  `Body` cap and consider a per-scope count/relevance limit before this grows.

## 7. Change inventory (anchors)

- `src/NodalMerge.Studio.Contracts/Domain/ArtifactRef.cs` — `Reach { Private, Workgroup }` field; `RepositoryId` reused as the application filter (2×2 = Reach × RepositoryId).
- `src/NodalMerge.Studio.Contracts/Domain/Finding.cs` — `RepositoryId` (Phase 0), provenance ids (Phase 3).
- `src/NodalMerge.Studio.Storage/StudioNodeStore.cs:112` — `RepoScopedKinds` (+ `FindingV1`); workgroup-room kind set.
- `src/NodalMerge.Studio.Storage/NodalMergeStudioNodeStore.cs:570-592,763-837` — scope→room routing; workgroup room in read fan-out.
- `src/NodalMerge.Studio.Storage/ArtifactLineageService.cs:39-73,208-213` — scope-aware record/query; replace `GetGlobalConstraintsAsync`.
- `src/NodalMerge.Studio.Storage/FindingService.cs:77-92,112` — scoped promotion; repo-scoped persist.
- `src/NodalMerge.Studio.Projections/ProjectionManager.cs:312-356` — scope-aware constraint fold + origin labels.
- `src/NodalMerge.Studio.Host/RoomPeerClient.cs:41-42,214-244` — leave/unmount (Phase 2).
- `src/NodalMerge.Studio.Storage/RuntimeDagPersistenceService.cs` — `DropRoomAsync` per-room eviction (Phase 2).
- `src/NodalMerge.Studio.Host/StudioRestEndpoints.cs:5364` — elevate-scope + import-to-local endpoints.
- `clients/vscode-extension/src/panels/InsightsPanel.ts`, `src/webviews/views/insights.js` — scope labels, elevate/import actions, applied-constraints view (follow-up).

## 8. Constraint creation, visibility & manual add — ✅ SHIPPED (2026-07-19)

**How each constraint is created, and whether it surfaces on the Insights "Constraints" tab.** The
tab is `GET /studio/constraints` → `GetGlobalConstraintsAsync`, which returns **only null-owner
constraints** (`OwnedByWorkUnitId is null && Type == Constraint`), then applies the repo application
filter + per-peer toggle.

| Creation path | Owner | Reach | On the tab? |
|---|---|---|---|
| **Promote a finding** (`FindingService.PromoteKnowledgeGuidelineAsync`) | null (global) | `Workgroup` | ✅ yes |
| **Manual add** (`POST /studio/constraints`) | null (global) | chosen (Private/Workgroup) | ✅ yes |
| **Elevate** (`POST /studio/artifacts/{id}/elevate`) | unchanged | → Workgroup, all-repos | ✅ (if already global) |
| `nm_v1_artifact_record` / `POST /studio/artifacts` (agents) | **the work unit** | null (legacy) | ❌ no — lineage-scoped |
| **Domain observers** (Security/Architecture/…) | **the work unit** | null (legacy) | ❌ no — same path as agents (but **promotable**, below) |
| `.workspace/decisions/` harvest | **the work unit** | null (legacy) | ❌ no |

**Domain-observer constraints are work-unit-owned by design** — they record via the same
`ArtifactCommandService.RecordAsync` path every agent uses (`OwnedByWorkUnitId = workUnitId`), so they
flow via lineage inheritance into descendant work units, not onto the shared-policy tab. This is
deliberate: the governance model requires a **human** to promote something into shared scope, so an
observer never writes workgroup policy directly.

**Promote a lineage constraint to global (SHIPPED).** The bridge that lets a reusable observer/agent
constraint become shared policy without retyping:
- `GET /studio/constraints/proposed` — lists work-unit-owned Constraint artifacts (excludes
  invalidated/cascade-flagged), each flagged `promoted` if a global already links back to it.
- `POST /studio/constraints/{id}/promote` `{ reach?, repoSpecific? }` — mints a **global copy**
  (defaults: `Workgroup` reach + the source's own `RepositoryId`; mirrors finding-promotion's opinion),
  stamped with the new **`ArtifactRef.PromotedFromArtifactId`** provenance field. The source lineage
  constraint is left untouched (still steers its own goal). **Idempotent** — one promotion per source
  (re-promote returns the existing global; widening to all-repos afterward is Elevate's job).
  `ParentArtifactId` stays null on the copy so no invalidation cascade couples it to the source;
  provenance rides on `PromotedFromArtifactId` instead.
- **UI**: a "Proposed by observers & agents" section on the Insights **Constraints** sub-tab — one-click
  **Promote** per card; promoted sources badge "promoted" instead. Tests:
  `ConstraintEndpointsTests.Proposed_lineage_constraint_promotes_to_a_global_and_is_idempotent`.

**Re-scope a constraint after the fact (SHIPPED).** Promotion is opinionated (Workgroup + repo, or
all-repos if no repo resolves), so the Constraints tab now lets you move a constraint between any 2×2
cell without recreating it. `POST /studio/constraints/{id}/scope { reach?, repoSpecific? }` →
`IArtifactLineageService.SetScopeAsync(reach, repositoryId)` mutates Reach+RepositoryId and re-routes to
the matching room (mirrors RecordAsync routing; generalizes the widen-only `/elevate`). Leaves a stale
copy in the prior room (rooms have no eviction — same caveat as Elevate; local reads via `_byId` stay
correct). **UI**: two inline selects (Reach: Workgroup/Private × Applies-to: All/This repository) on each
constraint card, replacing the static badges; the card re-groups on refresh. Test:
`ConstraintEndpointsTests.Scope_endpoint_moves_a_constraint_between_reach_cells`.

**Finding-kind promotion destinations (UX clarity SHIPPED).** Longstanding confusion: only
`FindingKind.KnowledgeGuideline` promotes to a Constraint (`PromoteKnowledgeGuidelineAsync`);
`FindingKind.PromptImprovement` promotes to **stage-scoped prompt guidance** (`PromotePromptImprovementAsync`
returns null — no artifact; read by the target stage's loop via `ListPromotedPromptGuidanceAsync`), so it
never hits the Constraints tab. The Insights UI now surfaces this: a toast on promote names the
destination, and a Promoted finding shows a `→ Constraint` / `→ prompt guidance for the <stage> stage`
line (renderFindings + insightsReviewFinding pass `kind`/`targetStage`). No backend change — data already
on the Finding.

**Manual add — the process (`POST /studio/constraints`).** The one path that creates a global
constraint at an arbitrary scope (promotion always makes Workgroup; this gives the full 2×2):

```
POST /studio/constraints
{ "title": "...", "body": "...", "reach": "Private" | "Workgroup", "repoSpecific": true | false }
```

- `reach` → `ArtifactReach` (default `Workgroup`). `repoSpecific=true` resolves the workspace's repo id
  server-side (same resolution findings use) → application = this repo; `false` → all repos.
- Creates a null-owner `Constraint` (`Status=Approved`) with `Reach` + `RepositoryId`; routing follows
  Reach × RepositoryId exactly as promotion/elevate do (studio / repo / workgroup room).
- **UI**: the "+ Add a constraint" form on the Insights **Constraints** sub-tab — title, body, a reach
  radio (Workgroup/Private) + an "Only this repository" checkbox = the 2×2.
- No domain-observer contract change was needed; this is a new REST contract + UI only.

## 9. One-shot CLI scan (Route B) & a guardrail for future CLI harnesses

The Insights **Run LLM Scan** works with a `claude-cli`/`codex-cli` profile via a one-shot CLI
completer (`IOneShotCliCompleter`, `Harnesses/OneShot/`) — the only "single structured completion over
a CLI transport" in the system (goals use the agentic `IHarnessExecutor`; HTTP uses `LlmClient`). The
analyzer branches by provider; over CLI it prompts the model to emit the `report_findings` JSON and
parses it defensively (no forced-tool-call channel exists over CLI). Unparseable output is surfaced to
the user via the extension's "Open raw output" action.

**Guardrail for any future CLI harness / one-shot path:** pass the prompt on **stdin**, never as a
command-line argument. `cmd.exe /c` (the Windows launch wrapper) truncates an argument at its first
newline, so a multi-line prompt reaches the CLI as only its first line (found live — the model got the
system prompt's first line and asked "where's the data?"). `CliProcessRunner` writes stdin
concurrently with the stdout read to avoid a pipe-buffer deadlock. The stub-CLI tests capture stdin and
assert a marker arrived, so this can't silently regress.
