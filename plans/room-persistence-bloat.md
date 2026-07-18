# Room persistence bloat & peer-sync refocus

**Status:** Layer 1 SHIPPED (host-side snapshot debounce, green). Layer 2 REFRAMED (2026-07-18) — was "pull transcripts local"; now a three-tier reasoning-replication model (share the reasoning, evict only the byte-firehose). Layer 3 folds into it.

**Origin:** 2026-07-17 two-machine hand test of studio-on-NuGet 0.2.3. The laptop peer kept getting disconnected ("stopped", "failed to load part of the timeline for 75") while agents ran on the desktop. Host logs showed the peer-private `studio` room's server-pack snapshot at ~6.8 MB and growing ~100 KB/mutation, re-serialized on every mutation. Related: [[nodalmerge_snapshot_on_mutation_storm]], `plans/repo-identity-convergence.md`.

**2026-07-18 reframe (why Layer 2 changed).** The user challenged the original Layer 2 premise ("get transcripts/events out of the replicated graph, keep local"). Their argument, which is correct: **agent reasoning is the product.** If only file state + one-line decision rationales replicate, multi-user Studio degrades to "git with a nicer graph" — the vision ("trace any approved change back through its lineage… replay a proposal under a different model… roll back to a known-good state *with its decision context*") requires the *reasoning* to be attached and shared, not just the tree hash. And the uncomfortable truth: **reasoning already doesn't replicate today** — `ConversationLogV1` lives in the peer-private `studio` room, so a peer sees the `DecisionV1` node and the file state but not the trajectory that produced them. The original Layer 2 would have cemented that. The fix is not "share everything" (the firehose really does storm the host) and not "hide reasoning" (guts the product) — it is to **separate the reasoning worth sharing from the telemetry byte-firehose, and move the shared reasoning onto the content plane** (small ref node replicates, body pulled on demand) so it is available without bloating the room. Constraint from the user: **no synchronous LLM summarization in the hot path** — store raw text, pull on demand, use cheap heuristics (first line) for node labels.

---

## The three distinct problems (do not conflate)

The single symptom ("host thrash → peer disconnects") has three independent causes. Fixing one does not fix the others.

1. **Snapshot cadence (Layer 1 — the storm).** `PersistRoomSnapshotAsync` re-serializes the *entire* room into one blob and persists it on *every* mutation. As the room grows this is O(n²) CPU + disk. This is what actually tips the connection over during an agent burst. **FIXED** by debouncing the checkpoint (below).

2. **Unbounded room growth (Layer 2 — the real leak).** Even with a perfect debounce, the `studio` room grows without bound, so the snapshot keeps getting bigger and eventually storms again — just slower. Two things drive it:
   - The CRDT **sync graph never replaces; it only appends.** Every write — even re-writing the same key — promotes a *new immutable node* into the room's sync graph (`EngineRoomMap.WriteEntryAsync` → `PromoteCheckpointToGraph`, `EngineRoomMap.cs:220-300`). `TryGetServerPackSnapshotAsync` exports the *entire graph history* (`RequestServerPack` with empty `known_ids`, `RuntimeDagPersistenceService.cs:701-778`). So the pack = every version of every key ever written, not current state.
   - **No GC of the sync graph.** Compaction pruning is disabled by default (`RuntimeDagCompactionOptions.Default` `EnablePruning=false`) and even when enabled only trims the outer `pack:` records, never the accumulated history *inside* them.
   - The volume comes from two append-with-new-GUID kinds routed to `studio`: **`ConversationLogV1`** (one node *per agent turn*, embedding uncapped `AssistantText` + uncapped tool-call input JSON + tool results capped 20 KB *each*, many per turn) and **`ExecutionEventV1`** (~10+ writes *per tool call* across the four agent loops). Secondary: `OrchestrationEventV1` (per decision), `ProjectionSnapshotV1` (full projection payload per capture).

3. **Reasoning replication (Layer 2/3 — "what should cross").** The question the user pressed: *are we sharing enough reasoning between peers to meet the vision, or just file state?* Original answer ("repo-scoped set is correct, bloat kinds are correctly peer-local") was **wrong on the second half** — the peer-local set includes the actual reasoning narrative (`ConversationLogV1`), so today peers share file state + one-line rationales, not reasoning. This is no longer a separate "mostly-confirmation" layer; it is the point of Layer 2. See the three-tier model below.

---

## Layer 1 — snapshot debounce (SHIPPED)

**Insight that makes it safe:** the full-room snapshot is a hydrate *checkpoint*, not the durability log. On the `pack` path (Studio's path — Studio writes arrive as CRDT packs), the incremental delta is already persisted independently one step earlier (`PersistInboundPackAsync`, `source=inbound-pack`, `RuntimeWebSocketLoopRunner.cs:269`) *before* the redundant full snapshot. Hydration = latest checkpoint + replay of incremental packs after it. So snapshotting less often only lengthens the replay chain; **no data is lost.**

**Change (in the `nodalmerge` host repo):**
- `RuntimeDagPersistenceService.PersistRoomSnapshotDebouncedAsync(roomId)` — records a mutation and snapshots at most once per window: `MaxPendingMutations` (default **200**) OR `MinInterval` (default **30 s**), then resets. Per-room state, thread-safe. When disabled, snapshots every time (legacy).
- `RuntimeDagPersistenceService.FlushRoomSnapshotAsync(roomId)` — forces a checkpoint of the pending window on peer disconnect / host shutdown; no-op when nothing pending.
- `RuntimeWebSocketLoopRunner`: the `pack`-path snapshot (`:308`) now calls the debounced variant; the finally/disconnect block flushes (`:471`).
- Config: `NodalMerge:Runtime:Dag:Snapshot` → `Enabled` / `MaxPendingMutations` / `IntervalSeconds`. `RuntimeSnapshotDebounceOptions.Default` = on, 200 ops, 30 s.
- Tests: `RuntimeDagPersistenceServiceTests` — op-threshold coalescing, interval trip, disabled=legacy, flush-then-noop (4 new, green).

**Deliberately NOT touched:** the *non-pack* mutation path (`RuntimeWebSocketLoopRunner.cs:317`, gated by `ShouldPersistSnapshotForMutation` = map-set/list-*/text-*/blob-set). That path has **no** incremental persist — its snapshot is the *sole* durability for direct-op apps (the JS SDK soundboard/speechslate hosts). Studio never uses it (all Studio writes are packs). Debouncing it would risk losing those apps' direct-op mutations on a crash. If it ever needs debouncing, add incremental persistence to that path *first*, then debounce.

**Deployment:** host change → local NuGet repack (`pack-local-nuget`) → VSIX rebuild (`build-vsix.ps1 -NodalMergePackageVersion 0.2.x`). The room *server* (7878) does not need rebuild — it relays packs opaquely.

---

## Layer 2 — the three-tier reasoning model (REFRAMED 2026-07-18)

**Goal (revised):** make another peer's *agentic reasoning* visible on the same repo — the "why" behind a change, session-filtered — **while** keeping the replicated room bounded regardless of how long agents run. The original Layer 2 optimized only the second half and sacrificed the first. The key realization: the data we lumped together as "high-volume, keep local" is actually **three different things** with three different homes.

### The three tiers

| Tier | Contents | Size | Reasoning value | Home |
|---|---|---|---|---|
| **1. Reasoning DAG** (structural) | goals, plans, tasks/slices, proposals, decisions (+rationale), conflicts, constraints, known-good states, lineage edges | Bounded in *count* — dozens of nodes/goal — but **three kinds carry uncapped free-text bodies** (see audit flag) | High — *is* the provenance graph | **Repo room, replicate eagerly.** Mostly already `RepoScoped`; move any that aren't; **cap the uncapped bodies** (L2.2b). |
| **2. Reasoning narrative** | assistant text per cycle, tool-call *intent* (name + brief input), the trajectory a peer reads to understand "why" | Moderate, grows with turns | High — what makes a peer's dev legible | **Content plane.** Small `ConversationRef`-style node replicates in the repo room (session id, work-unit id, cycle count, body blob hash, first-line label); **body is a CAS blob pulled on demand.** Session-filtered by construction. |
| **3. Execution telemetry** (firehose) | `ExecutionEventV1` (~N per tool call), raw tool-**result** bytes (file contents, command output), full projection payloads | Unbounded, dominates volume | Low — derivable / re-runnable / unread | **Peer-local append store** (SQLite) with retention window. Never promoted to the sync graph. |

Plus: **file diffs are derived, not stored** — the snapshot DAG already replicates the tree pair; render the diff on read. (Confirms the user's intuition; resolves L2.4 by *deletion* rather than a CAS ref for the diff bytes.)

The bloat/storm is almost entirely **tier 3** (per-tool-call firehose) plus the **uncapped tails of tier 2** (full tool-result payloads stapled onto each transcript entry). Tier 1 + the assistant-text of tier 2 — the reasoning a human actually wants — is small. So we can share the reasoning *and* shrink the room; they were never in tension, the original framing just conflated the layers.

### Why the content plane for tier 2 (not eager replication, not local-only)
- **Local-only** (original Layer 2) → peers can't see each other's reasoning → "just file state." Rejected.
- **Eager replicate the transcript into the repo room** → reasoning is shared but the room grows with turn count again → re-storms, slower. Rejected as the default.
- **Content plane (chosen):** apply the doctrine we already hold — *"rooms hold references; the CAS holds bytes"* — to reasoning, not just files. The repo room carries a **small ref node** (replicates, bounded); the transcript **body is a CAS blob pulled on demand** when a peer opens that session. Room stays bounded; reasoning is fully available; session-filtering falls out for free (you pull the sessions you look at; "my session" is a UI filter over shared refs). **No synchronous summarization** — store raw text, heuristic first-line label for the node.

### Offline-first is preserved (and is the argument *for* this)
The room server (7878) is a **relay, not the source of truth** — each peer runs its own runtime + node store and hydrates the full replicated DAG locally. That is what makes it offline-first. But that value is real *only if the reasoning DAG is in the replicated set* — otherwise offline-first buys nothing over git. So the user's offline-first concern is the argument *for* tier 1 replicating and tier 2 being pullable-from-any-origin, not against.

### Slices (RED-first)

- **L2.0 — Reasoning inventory audit (COMPLETE 2026-07-18; findings below).** Authoritative routing = `StudioNodeStore.RepoScopedKinds` (`StudioNodeStore.cs:107-129`); a kind replicates only if in that set *and* its writer passes a resolvable `repositoryId` to the 4-arg `WriteNodeAsync` (`:149`) — the 3-arg overload (`:134`) always lands peer-local.
  - **Real vs. stub:** `ReasoningCommitV1` (`StudioNodeStore.cs:37`) and `TrajectoryV1` (`:35`) are **defined-but-never-written stubs** — do not build on them. `ReasoningTools.RecordAsync` (`ReasoningTools.cs:29-37`) actually writes an `OrchestrationEventV1` and *discards* model/provider; the "reasoning commit graph" (`ProjectionManager.cs:647-767`) is a *projection* over ExecutionEvents/Decisions/Evidence, not persisted nodes. Real-but-peer-local reasoning kinds: `HypothesisV1`, `EvidenceV1`, `FindingV1`, `SteeringDecisionV1`, `ExperimentV1`, `ChangeIntentV1`. Real-and-replicating: `DecisionV1`, `ArtifactRefV1`, `GoalV1`, `TaskV1`, `MergeProposalV1`.
  - **Where the "why" lives:** *only* in peer-local `ConversationLogEntry.AssistantText` (`ConversationLogEntry.cs:15`, uncapped). `DecisionV1.Rationale` (`DecisionNode.cs:16`, uncapped, repo-scoped) is caller-supplied and usually `null`/boilerplate (`ExperimentService.cs:201` writes "Superseded by winning fork…", with `ReviewerModel/Provider: null`). **No node links a Decision/Proposal → the reasoning transcript.** So a peer sees outcome + file state, not trajectory. Reframe confirmed.
  - **Volume:** 1 `ConversationLogV1`/cycle; result fields capped 20 KB *each but many per entry*; `AssistantText` + each tool-call `InputJson` **uncapped** (`ConversationLogEntry.cs:15,34`). `ExecutionEventV1` from ~30 emit sites, fresh GUID each (`ExecutionEventStreamService.cs:27`) so nothing collapses; `PayloadJson` uncapped. `OrchestrationEventV1` **double-writes** (also mirrors an ExecutionEvent, `OrchestrationDecisionLogService.cs:53-68`). `ProjectionSnapshotV1` embeds a full workspace projection per capture.
  - **CAS-ref plumbing EXISTS and is reusable** (answers the big L2.3 risk): `IBlobStoreProvider.PutBlobAsync(hashHex, bytes, contentType)` / `TryGetBlobAsync` (`IBlobStoreProvider.cs:5-39`) is arbitrary-bytes, not file-specific; `BlobHasher.ComputeHash` (`BlobHasher.cs:16`) = Blake3 hex; worked "hash→put→reference by hash" example at `RepositoryBlobTools.BlobWriteAsync` (`RepositoryBlobTools.cs:70-89`); pull-on-demand already wired (`WorkUnitPrefetchService`, `CasReconcileService`, `IBlobPrefetchService`). **L2.3 greenfield = just a new ref-node kind + publish/repoint; the store/hash/pull path already exists.**
  - *Read-path note (folded from the old read-path audit):* still must confirm every reader of the tier-3 kinds goes through a service method, not raw room enumeration, before the L2.1 store swap — the audit mapped the writers, not yet every reader. Do this as the first step of L2.1.

- **L2.1 — Tier-3 local append store. SHIPPED 2026-07-18.** New `IStudioLocalLogStore` (`AppendAsync`/`GetAsync`/`ReadAllAsync`/`PruneOlderThanAsync`) with a dependency-free JSONL-file-per-kind impl (`FileStudioLocalLogStore` — chosen over SQLite to avoid a second `SQLitePCLRaw` provider stack + the prior pool-race flake); `InMemoryStudioNodeStore` implements it too so shared-store tests keep compiling. All four owning services (`ConversationLogService`, `ExecutionEventStreamService`, `OrchestrationDecisionLogService`, `ProjectionSnapshotService`) swapped their `IStudioNodeStore` dependency → `IStudioLocalLogStore` (structurally can no longer write to the room). DI: File in `AddNodalMergeStorage`, InMemory forwarded in `AddInMemoryStorage`; Host binds `NodalMerge:Studio:LocalLog:Directory`, anchored next to the node DB (`Sqlite:DbPath`'s dir) so it tracks the extension's absolute path. Read-path parity was CLEAN (only each service's own `RehydrateAsync` read the room for these kinds — L2.0 recon). Tests: `StudioLocalLogStoreTests` (contract ×2 impls + 4 service-routing round-trips, 12 green); regression Integration 702/702 + AgentRuntime 107/107. **Deferred:** active retention *scheduling* (a background loop calling `PruneOlderThanAsync` for `ExecutionEventV1`) — the capability + test exist; the acceptance win (room no longer grows from these kinds *at all*) is already met, local-log disk growth is the lower-severity remainder. Also still open: the `OrchestrationEventV1` double-write (`OrchestrationDecisionLogService.cs:61-69`) is untouched — both writes now land in the local log, so it no longer bloats the room; cutting the mirror is a separate cleanup.

- **L2.2 — Cap the uncapped tier-2 fields. SHIPPED 2026-07-18.** Shared `NodePayloadLimits.Cap` (20 KB, matching the pre-existing tool-result cap; appends a `...truncated` marker) applied in `ConversationLogService.RecordAsync` to `AssistantText` + each tool-call `InputJson`. 5 green (`NodePayloadCapTests`).

- **L2.2b — Cap the uncapped *replicated* tier-1 bodies. SHIPPED 2026-07-18.** Same `NodePayloadLimits.Cap` applied at the persistence boundary in `ArtifactLineageService.RecordAsync` (`Body`) and `DecisionNodeService.RecordAsync` (`Rationale`) — both repo-scoped, so this bounds the *replication* plane. Covered by `NodePayloadCapTests`; no regressions (21/21 touched services). *Follow-up option:* for genuinely large artifact bodies, push the body to a CAS blob + carry the hash (same pattern as L2.3) instead of truncating.

- **L2.3 — Tier-2 content-plane ref + Decision→reasoning link. SHIPPED 2026-07-18.** Design confirmed by a blob-store recon: **one global** `IBlobStoreProvider` (inject directly; replicates cross-peer only under a remote-origin `ChainedBlobStoreProvider`); peer-B pull is **explicit** (`TryGetBlobAsync`, nothing auto-fetches node-referenced hashes); the blob is **not** GC-protected by default (`BlobIndexEntryV1` is inert). Delivered:
  - **Contracts:** `ConversationRef` (repo-scoped ref: work-unit id, session id, cycle count, `TranscriptBlobHash`, first-line heuristic `Label`, `DecisionId`/`ProposalId`, `RepositoryId`) + `PublishedReasoningTranscript`/`Cycle`/`ToolCall` DTOs. New kind `StudioNodeKind.ConversationRefV1`, added to `RepoScopedKinds` (replicates). `DecisionNode.ReasoningRefId` added.
  - **Publish (write):** `ReasoningPublisherService.PublishAsync` builds a bounded transcript (assistant text + tool-call **intent**; tool-result bodies dropped; tool inputs previewed to 512 chars), stores it as a CAS blob (`application/json`, BLAKE3), writes the repo-scoped `ConversationRef`. **No LLM summarization** — label = last cycle's first line. Wired as an *optional* collaborator into `DecisionNodeService.RecordAsync` (best-effort; sets `ReasoningRefId`); registered in prod DI only when a blob store exists.
  - **GC protection:** `WorkspaceCacheManager.GetLiveBlobHashesAsync` now unions `ConversationRefV1.TranscriptBlobHash` into the live set (the ref is the blob's only referent).
  - **Resolve (read):** `ReasoningResolverService.GetReasoningAsync` returns local `ConversationLogV1` when present, else falls back to the peer-published transcript (find replicated `ConversationRef` → pull blob → map to `ConversationLogEntry[]`, `AgentId="(remote)"`). The `/studio/workunits/{id}/conversation-log` drawer endpoint repointed to it — **same shape, no client change**; a peer now sees another peer's reasoning, not just file state.
  - Tests: `ReasoningPublisherTests` (4) + `ReasoningResolverTests` (3), incl. a peer-A-publishes / peer-B-resolves round-trip. Regression: Integration **709/709**, AgentRuntime **107/107**, Contracts **24/24**.
  - **Follow-ups:** (1) publish on `MergeProposalV1` too (only `DecisionV1` linked so far); (2) document `ConversationRefV1` in `docs/STUDIO_ROOM_SCHEMA.md` (frozen-schema hygiene; golden vectors still pass); (3) cross-peer blob replication requires the host configured with a remote blob origin (`ChainedBlobStoreProvider`) — deployment config, verify on the two-machine test.

- **L2.4 — MergeProposal diff: CAS-ref, not inline. SHIPPED 2026-07-18.** Derive-on-read was impractical (a remote peer would need both branch trees materialized), so the plan's CAS-ref fallback was chosen (user decision). `MergeProposal.WorkspaceChangesBlobHash` added. **Choke point:** `InMemoryMergeService.ProposeAsync` — when a blob store exists and the diff is non-empty, it's stored as a `text/x-diff` CAS blob and `WorkspaceChanges` is nulled (idempotent; no blob store → stays inline, so existing tests are unaffected). **Resolve:** new `IMergeDiffResolver` (Core) / `MergeDiffResolverService` (Storage) — inline when present, else pull the blob. All **6 consumers** repointed: `AutomatedReviewGateService` (revision context), `HarnessHarvestPipeline` (review request), `InMemoryWorkUnitService.BuildProposalSnapshot` (changed-files summary), and `StudioRestEndpoints` `/constituents` + `/compare`. **GC:** `GetLiveBlobHashesAsync` unions `MergeProposalV1.WorkspaceChangesBlobHash`. Tests: `MergeDiffResolverTests` (3) + `InMemoryMergeServiceTests` CAS-ref (2). Regression: full solution green except one confirmed pre-existing teardown flake (`ReadBeforeWriteEnforcementTests.Dispose` dir-removal race; passes 1/1 isolated). **Gotcha fixed:** `InMemoryWorkUnitService` is DI-registered via an explicit factory (`AddStudioOrchestrator`), so the new optional `diffResolver` had to be threaded there manually — auto-injection didn't apply.

### Acceptance
- A multi-hour agent run leaves the **repo room** server-pack bounded by live DAG state + ref nodes, not linear in turn/tool-call count; tier-3 telemetry lives in a bounded local store.
- A peer on the same repo can open another peer's session and see the reasoning narrative (pulled), goals→plans→tasks→proposal→decision lineage, derived diffs, and conflicts/constraints — **not** the tier-3 firehose.
- Pathways node-detail + Activity Center render identical content locally (read-path parity test) and now also resolve remote-peer reasoning via ref.

---

## Layer 3 — peer-sync doctrine (folded into Layer 2)

**The sharing boundary is the workgroup** (`cas-distribution-and-storage.md:116-119`): repo/workgroup data is shared by design; collaboration isolation comes from **branches + proposals + reconciliation, never from hiding data**. Peers get their own instanced materialized dirs (peer-local disk).

### What crosses vs. stays local (target state after Layer 2; authoritative source: `StudioNodeStore.RepoScopedKinds`)

| Plane | Contents | Why |
|---|---|---|
| **Repo room `repo/{repoId}`** (replicates) | Tier 1: `WorkUnitV1`, `RepositorySnapshotV1`, `RepositoryOpV1`, `RepositoryConflictV1`, `CoModPatternV1`, `MergeProposalV1`, `TaskV1`, `BranchV1`, `KnownGoodStateV1`, `DecisionV1`, `ArtifactRefV1`, `CandidateConflictV1`, `TaskConflictV1`, `GoalV1` — **plus (new) tier-2 `ConversationRef`** (ids + blob hash + label) | The per-repo DAG + reasoning refs that let a same-repo peer trace the "why". Small, provenance-bearing; heavy content by hash. |
| **Workgroup room** (replicates) | `repositories` directory (repoId → {label, repoRoomId, hints}), presence/membership, cross-repo goal/reference nodes | Discovery + identity convergence (hints, never identity — D2). |
| **Content plane (CAS)** (pull-on-demand) | file/tree bytes by hash **+ (new) tier-2 reasoning-transcript blobs by hash** | "Rooms hold references; the CAS holds bytes." Big, self-verifying, pull the sessions you actually open. |
| **Peer-local store** (does NOT replicate) | settings, profiles, scheduler, registry bindings (local paths), gc runs, materialized dirs, **tier-3 telemetry: `ExecutionEventV1`, raw tool-result bytes, `ProjectionSnapshotV1`, full `ConversationLogV1` fidelity** | Slice 7.3 (settings/registry LWW collisions) + the firehose is unread, derivable, per-machine. Retention-windowed. |

**Rule for placing anything new:** structural provenance a same-repo peer needs → tier-1 repo-scoped node. Reasoning narrative a peer wants to *read* → tier-2 CAS blob + repo-scoped ref. Bulk/opaque/unread/per-machine → tier-3 peer-local with retention. Isolation is never achieved by withholding data.

### Resolved / remaining decisions
1. **Cross-peer session visibility — RESOLVED by the three-tier model.** Yes, peers should see each other's reasoning (that was the whole reframe), but via **tier-2 content-plane refs (bounded, pulled, session-filtered)**, NOT by replicating the raw tier-3 `ExecutionEventV1`/`ConversationLogV1` firehose. This is no longer a deferred "decide later" — it is L2.3.
2. **`MergeProposalV1` diff placement — leaning derive-on-read** (L2.4), CAS ref only as fallback. Confirm during L2.4 whether a derive path is practical.
3. **Eager vs. lazy for tier-2** — chosen **lazy (content-plane pull)**. Revisit only if a "live-watch another peer's agent" feature demands eager streaming, which is a different (streaming) problem, not this persistence one.

---

## Cross-references
- Snapshot storm memory: [[nodalmerge_snapshot_on_mutation_storm]]
- Convergence + Part B multi-repo UX: `plans/repo-identity-convergence.md`, [[nodalmerge_repo_identity_convergence_gap]]
- Room topology + two-plane model: [[nodalmerge_room_topology_repo_identity]], `plans/cas-distribution-and-storage.md`
- Pathways as projection: `plans/pathways-workspace-history.md`, [[nodalmerge_pathways_plan]]
- Schema (frozen): `docs/STUDIO_ROOM_SCHEMA.md`
