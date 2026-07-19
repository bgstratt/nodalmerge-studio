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

### Phase 0 — Findings replication (ship now, independent of the scope model)

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

### Phase 1 — Scope as a first-class property (this is where "global constraint replication" lands)

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

### Phase 2 — Leave / unmount + eviction (completes "persistent + leaveable")

- **Leave-workgroup operation**: a new command that (a) drops the `workgroup` room connection in
  `RoomPeerClient` (today `_connections` is never shrunk — `RoomPeerClient.cs:41-42,93-95`) and
  (b) evicts the room's persisted local data.
- **Per-room eviction API**: a `DropRoomAsync(roomId)` in the persistence layer
  (`RuntimeDagPersistenceService`) that removes persisted packs/snapshots for a room and invalidates
  its hydration — none exists today (`InvalidateHydration` only drops a cache;
  `DeleteAcceptedNodesAsync` is compaction-only).
- On leave, workgroup-scoped constraints disappear from injection automatically (their room is gone
  from the fan-out).
- **Re-join** re-syncs current workgroup state from peers/server. Safe by construction: the human-apply
  barrier means only deliberately-promoted knowledge is ever there to receive.

### Phase 3 — Provenance & Import-to-Local (audit trail + safe adoption)

- **Provenance edges.** Today the link is one-way (`Finding.PromotedArtifactId`). Add a stored
  provenance backlink on the Constraint (e.g. `DerivedFromFindingId`, and the finding's source run
  ids) so "why do we have this policy?" walks Policy → Finding → runs → logs. This is the audit trail
  the model promises and is currently absent on the artifact itself.
- **Import to Local.** An explicit action that copies a Workgroup/Repository constraint into the
  user's **Local** scope as a new node whose provenance points back to the shared original. Adoption
  is deliberate and reversible; it never happens implicitly on join.

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

- **Insights tab: applied / applicable constraints view + local override.** Let a user see which
  constraints are currently applied/applicable to their agents and **override them locally** — a
  `Reach=Private` constraint (the fourth 2×2 cell, incl. Private + repo-specific) that supersedes a
  shared Workgroup one for this peer. `Reach=Private` + the existing `Supersedes` primitive set this
  up; the UI and the local-override resolution rule are the remaining work. (Raised 2026-07-19; phase
  after the above.)
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
