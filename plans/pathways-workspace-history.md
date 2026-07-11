# Pathways as workspace history — "git for agent reasoning," rendered as a projection

## Status

**Slice 1 shipped (2026-07-08), on branch `pathways-workspace-history`.**
`ProjectionType.WorkspacePathways` is implemented in `ProjectionManager` and
served through the existing generic `/studio/projections/{projectionType}`
REST route (no new endpoint needed) and MCP surface — no code changes to
either transport were required. The dag-replay webview and
`TrajectoryReplayPanel` now consume it instead of `/studio/replay/timeline`;
orchestration decision-log chatter no longer reaches Pathways. 6 new unit
tests in `NodalMerge.Studio.Projections.Tests` cover node/edge shape,
root-anchoring for child-work-unit proposals, dead-branch fallback, and
branch-scoping of external-update nodes. Full Projections test suite (25
tests) passes; extension `tsc --noEmit`, `npm run compile`, and
`npm run webview-smoke` all pass clean.

**What slice 1 deliberately simplified vs. the original design below** (read
before starting slice 2/3):
- **Branch topology is flat per goal, not nested.** Every proposal (including
  ones on fanned-out child work units) edges directly to its top-level goal's
  `GoalStarted` node, not to its immediate parent work unit's own node. True
  nested topology is unstarted follow-up work, not a slice-2/3 item covered
  below.
- **No new `ProjectionRequest.RepositoryId` field was needed** (verification
  item 3, resolved): Studio is single-repo per instance, so `BranchId` was
  reused as-is. Confirmed extending `ProjectionType` doesn't require touching
  `docs/contracts/projection-v1-contract.md` — that doc is stale (documents
  only 5 of the now-16 projection types) and hasn't gated any of the prior
  additions (GoalGraph, ReasoningCommitGraph, etc.) either.
- **Node ids are prefixed** (`goal:{workUnitId}`, `proposal:{proposalId}`,
  `failed:{workUnitId}`, `external:{artifactId}`) for cross-kind uniqueness —
  the webview strips this back to the raw underlying id before calling
  `/studio/replay/inspect`, since that endpoint matches on raw artifact ids.
- **`GoalStarted`, `DeadBranch`, and `ExternalUpdate` nodes render client-side
  from their own projection fields**, not via `/studio/replay/inspect` — that
  endpoint can only resolve nodes reachable through
  `IArtifactLineageService.GetChainAsync(workUnitId)`, which by construction
  never finds bare work units or `OwnedByWorkUnitId: null` artifacts
  (external changesets). Slice 2 (below) is exactly "give these kinds real
  backend detail" — this was expected, not a bug found late.
- **Webview visuals reuse the existing per-branch-lane SVG renderer** (lane
  key = node's `BranchId`, or a synthetic `external-updates` lane for
  external-update nodes). `WorkspacePathwaysEdge`s are returned by the
  projection but not yet consumed by the renderer, which only understands
  sequential per-lane lists. A bespoke tree/DAG visual with distinct node-kind
  icons is fast-follow polish, not blocking.
- Verification items 1 (snapshot-at-merge coverage) and 2 (approver
  attribution on `MergeProposal`) are **still open** — `SnapshotId` is `null`
  on every node for now (never fabricated), and `ActorId`/`ActorModel` on
  Integration/Rejection nodes reflect the *proposer*, not a distinct
  *approver* (that field doesn't exist in `MergeProposal` yet). Both feed
  slice 3.

**Slice 2 shipped (2026-07-08), same branch.** Node detail for
Integration/Rejection/Superseded nodes now shows file diffs and agent
context, both via REST endpoints that already existed
(`GET /studio/merges/{proposalId}/file-changes`,
`GET /studio/workunits/{workUnitId}/conversation-log`) — no new backend
surface was needed, just wiring `DagReplayPanel.ts`'s `inspectNode` handler
to fetch both (best-effort, `Promise.allSettled`) alongside the existing
`/studio/replay/inspect` call, and rendering them in the webview.

**Important correction to the original design** (caught by verifying before
building, not found late): `ReasoningCommitNode.ProjectionSnapshotJson` —
which the original design named as the "what the agent saw" source — is
**dead code**. `ReasoningCommitNode`/`StudioNodeKind.ReasoningCommitV1` are
never written anywhere in the codebase; nothing populates
`ProjectionSnapshotJson`. (`IProjectionSnapshotService.CaptureAsync`, a
*different* type — `ProjectionSnapshot`, not `ReasoningCommitNode` — is real
but only agent-triggered via an opt-in MCP tool, not automatic per decision,
so it can't be relied on either.) Slice 2 uses `ConversationLogEntry`
instead — confirmed real and written by every agent loop
(`ConversationLogRecorder.cs`, called from `WorkerAgentLoop`/
`ReviewerAgentLoop`/`PlannerAgentLoop`/etc.) — filtered to the proposal's
owning work unit, capped to the last 10 cycles client-side. This is a better
fit for "context that goes along with the artifact" than a snapshot anyway:
it's the actual exchange, not a point-in-time projection dump.

**What slice 2 deliberately left out:**
- ~~"Open Diff in Editor" is not wired.~~ **Done as a same-day fast-follow.**
  `NM_DIFF_SCHEME`/`getDiffProvider()`/the diff-open flow moved from
  `MergeReviewPanel.ts` (where they were private) to
  `sharedWebviewChrome.ts` as `openReadOnlyDiff()`, and both
  `MergeReviewPanel.ts` and `DagReplayPanel.ts` now call the shared version
  — VS Code only allows one `registerTextDocumentContentProvider` per scheme
  per extension, so this had to be shared, not duplicated. Each file change
  in the Pathways drawer now has a "View Diff in Editor" button identical to
  Merge Review's. Split-view mode was not ported (Pathways' drawer is a
  compact strip, not a full review panel — inline is the only mode there).
- **GoalStarted/DeadBranch/ExternalUpdate nodes still render client-side
  only** (as in slice 1) — no backend detail was added for these kinds.
  There's no real data gap for GoalStarted/DeadBranch (a work unit's own
  fields are already everything there is), but ExternalUpdate could show its
  pre/post `KnownGoodState` diff (`RepositorySyncService` already records
  `preSyncSnapshotStateId`/`postSyncSnapshotStateId` in the artifact body) —
  left for slice 4, which already owns external-update polish.
- **Conversation log is unfiltered beyond "last 10 cycles for this work
  unit"** — no attempt to find the specific cycle(s) nearest the proposal's
  own timestamp. For a typical single-proposal work unit this is the same
  thing; for a work unit with multiple revise cycles it shows more than just
  the winning attempt. Acceptable for a first pass; tightening this is a
  candidate follow-up, not a blocker.

**Slice 3 shipped (2026-07-08), same branch — plus the "Open Diff in Editor"
fast-follow from slice 2 (see above).** "Materialize to scratch" and "Branch
from here (new steering)" are both wired in the node-detail drawer. Neither
needed new backend services — both `IProjectionMaterializer` and
`ICounterfactualService` were already fully implemented; they were just
unreachable from any UI (`ICounterfactualService` wasn't even reachable from
REST/MCP before this — confirmed by grep, zero callers anywhere). The actual
slice 3 work was almost entirely webview wiring plus two REST calls added to
`DagReplayPanel.ts`'s `handleMessage`:
- **Materialize**: `materializeNode` → `fs.mkdtempSync` a fresh OS temp dir
  client-side → `POST /studio/projections/{workUnitId}/materialize?targetPath=`
  (existing endpoint) → offer to open the folder in a new VS Code window.
  Always passes an explicit scratch `targetPath` — never omit it, since the
  server defaults to `WorkspaceOptions.SeedRepositoryPath` (the real working
  repo) when it's absent.
- **Branch from here**: `createCounterfactual` → profile quick-pick (fetched
  from the existing `/studio/agent-profiles`) → optional goal-override/
  constraint input boxes → `POST /studio/counterfactuals` (existing endpoint)
  → resolve the new work unit's branch and register it in the DAG the same
  way `branchFromCursor` already does. Offered only on Integration/Rejection/
  Superseded nodes — `PathwaysNode.proposalId` is exactly the `ProposalId`
  `ICounterfactualService` already keys its `base/{proposalId}` snapshot
  seeding on, so no id translation was needed either.

**Verification items 1 and 2 are now resolved — as confirmed gaps, not
fixed.** Both turned out worse than "maybe missing," and both are now
precisely characterized instead of guessed at:

1. **Snapshot-at-merge coverage: confirmed absent, not just incomplete.**
   Grepped every caller of `IRepositorySnapshotService.CreateAsync` in the
   codebase — there is exactly one, `GitAdapter.ImportAsync`, which only
   runs on an explicit git-import action. Neither `InMemoryMergeService.
   ApplyAsync` nor `RepositorySyncService`/`RepositoryImportService`
   (goal-creation drift sync) ever create a `RepositorySnapshot`, despite
   `RepositorySnapshot`'s own doc comments describing "one snapshot per goal
   cycle, created by between-run sync" as the design — that description was
   never actually wired up. Concretely: `WorkspacePathwaysNode.SnapshotId`
   will be `null` on every node in every normal (non-git-import) workflow,
   not just intermittently. This also means true point-in-time
   materialization (reconstructing exactly the state a specific pathway node
   was recorded against, via `RepositorySnapshot`+`IMaterializationEngine`)
   is **not achievable yet** — slice 3's "materialize to scratch" above
   deliberately uses `IProjectionMaterializer` instead, which reconstructs a
   work unit's branch's *current* live content via `IFileWorkspaceService`,
   not a historical snapshot.

   Fixing this is a real, standalone piece of work: `InMemoryMergeService.
   BestEffortResyncAsync` (the only merge-apply path that touches the
   sync/audit-trail machinery at all) calls `RepositorySyncService`, which
   itself never creates a `RepositorySnapshot` either — so closing this gap
   needs a snapshot write *added*, not just an existing path unblocked. And
   `BestEffortResyncAsync` only fires when the apply's `writeBackPath` equals
   `WorkspaceOptions.SeedRepositoryPath` — i.e. **only for the single global
   default repo**, explicitly skipped for multi-repo work units whose
   write-back points elsewhere. That's exactly the reconciliation-across-
   goals scenario you flagged in the original discussion (candidate branch
   merging into a candidate branch from multiple top-level goals) — so even
   the non-snapshot audit-trail refresh that *does* exist misses that case
   today. Not attempted this session; a future slice.
2. **Approver attribution: confirmed absent, not just unconfirmed.**
   `IMergeService.ReviewAsync` — the human-approval path — has no reviewer-
   identity parameter at all: not on the method signature, not stored on
   `MergeProposal`, and the `ReviewCompletedEvent` it publishes hardcodes
   `ReviewerAgentId: null` for every human review (`AutomatedReviewAsync` is
   the only path that records a reviewer id, and only for automated
   reviews). `WorkspacePathwaysNode.ActorId`/`ActorModel` on Integration/
   Rejection/Superseded nodes therefore reflect the *proposer* only — there
   is currently no data source anywhere in the system for "who approved
   this," human or otherwise. Adding it means extending `ReviewAsync`'s
   signature, `MergeProposal`'s schema, and the REST/MCP review call sites
   that invoke it — a real API/contract change, correctly out of scope for
   this session; not attempted.

**Slice 4 shipped (2026-07-08, partial), same branch.** ExternalUpdate nodes
now carry `FilesTouched` (parsed from the artifact body) and a "View file
changes" button wired to the existing `KnownGoodState` diff endpoint via two
new `WorkspacePathwaysNode` fields (`ExternalSyncStateIdBefore`/`After`). A
"Sync now" toolbar button was added, reusing `/studio/workspace/switch`
rather than a new endpoint. Approver-attribution polish is **not done** —
correctly blocked on item 2's schema gap, not attempted. One new backend
test (26th in the suite) added specifically to catch a case-sensitivity bug
this parsing work hit and fixed before it shipped: `JsonSerializer.
Deserialize` without `JsonSerializerOptions.Web` silently drops every field
when matching the artifact body's camelCase JSON against PascalCase record
properties — caught by reasoning through the exact serialization path before
trusting the parse, not by a failing test after the fact.

**Review + hardening pass (2026-07-08, same branch, after a fresh critical
review of all four slices).** Fixed, with the review findings that drove
each: (1) the dag-replay webview's `escHtml` was a **no-op** (each character
"escaped" to itself — a drifted local copy of the exact partial-copy failure
`views/lib/esc.js`'s own comment warns about), so slice 2–4's LLM
conversation text/diff content/file paths reached `innerHTML` raw — now a
full-fidelity five-char escape; (2) "View Diff" double-fired across Studio
Shell views — `StudioShellPanel` broadcasts every message to every panel, so
DagReplayPanel's bare `openDiff` type was also handled by
DecisionConvergencePanel (and both views' document-level listeners matched
the same `[data-open-diff]` attribute) — now `pathways.openDiff` +
`data-pw-open-diff` + one delegated listener scoped to the Pathways pane;
(3) "Sync now" submitted `resolveRepositoryPath()` (the VS Code folder) to
`workspace/switch`, which *sets* `SeedRepositoryPath` — a silent repository
switch whenever the host was configured against a different repo — now it
reads the host's own path from `workspace-summary` first; (4) in-lane node
order was **alphabetical by prefixed node id** (no lamport was passed, and
`compareNodeIds` falls back to `nodeId.localeCompare` — `failed:` sorted
before `goal:`) — now `lamport = Date.parse(occurredAt)`, chronological;
(5) materialize now targets
`{extension storage}/pathways-scratch/{branch}/{timestamp}` (named,
grouped, survivable) instead of a random OS temp dir; (6) branch-scoped
projection requests no longer emit dangling edges to `GoalStarted` nodes
that were outside the scope (guard + regression test); (7) a host without
`WorkspacePathways` no longer kills the whole panel init (pathways fetch is
best-effort); (8) conversation log orders by `occurredAt` (cycle numbers
interleave incorrectly across agents) and node-detail responses carry a
`requestNodeId` echo so rapid clicks can't render a stale response; (9) the
session picker now does what the design said: selecting a session **dims**
out-of-session lanes (`renderDag` gained a `dimmedBranchIds` param) instead
of doing nothing; (10) the per-poll full artifact-store rescan is gone —
`IArtifactLineageService.GetByTypeAsync` (new contract method, in-memory
index) serves the ExternalChangeset lookup. Also: CHANGELOG 0.1.10 + version
bump, `docs/reference/{api,ui}-reference.md` de-staled (api-reference
explicitly claimed the panel "only calls replay/timeline" — now false), and
one **pre-existing** stale Core test fixed
(`Merged → Superseded` transition — legal since reconciliation added that
edge; the test still asserted false and failed on `main`'s lineage too).
Verified: Projections 27/27, Merge 68/68, Core 39/39, Contracts 16/16,
Tasks 11/11, AgentRuntime 67/67; extension tsc/esbuild/webview-smoke clean.

**Final pass shipped (2026-07-08, same branch): every remaining item from
the original design is now implemented.**

- **Event-sourced node derivation** (the retroactive-history-rewrite fix).
  `BuildWorkspacePathwaysAsync` now derives Integration/Rejection/Superseded
  nodes from `IExecutionEventStream.GetEventsByKindAsync([MergeApplied,
  ProposalRejected, MergeProposalStatusChanged])` — a proposal that merged
  and was later superseded by reconciliation keeps BOTH nodes
  (`proposal:{id}:integration` and `proposal:{id}:superseded`, chained by an
  edge), with true transition timestamps from the events instead of
  `DiffGeneratedAt ?? wu.CreatedAt`. State-derived fallback covers proposals
  with no events (no SessionId, or pre-event history) — hybrid, never
  empty. Node ids are now kind-suffixed; the webview never parses them (it
  uses the node's own `ProposalId` field), so only tests needed updating.
- **Reviewer-identity capture** (closes verification item 2).
  `MergeProposal.ReviewedBy` (additive field) + `IMergeService.ReviewAsync`
  gained an optional `reviewedBy` param defaulting to "user" (it IS the
  human path); `AutomatedReviewAsync` records `reviewerAgentId ??
  "automated-reviewer"` on terminal decisions only (the
  Approved→ReadyForReview hand-back deliberately leaves it for the human
  sign-off that follows). `ReviewCompletedEvent.ReviewerAgentId` no longer
  hardcodes null; the ProposalApproved/Rejected event payloads carry the
  real reviewer; the MCP review tool accepts an explicit `reviewedBy`.
  Surfaced as `WorkspacePathwaysNode.ReviewedBy` (event `RejectedBy` wins
  over the proposal field) and a "Reviewed by" row in the drawer.
- **RepositorySnapshot capture at merge-apply** (closes verification item 1
  — with a correction to the item's own finding, see below).
  `BestEffortResyncAsync` now (a) covers **multi-repo** write-backs via
  `IRepositoryImportService.ForceSyncAsync` on the work unit's own repo —
  previously skipped entirely for non-default repos, the exact
  candidate-into-candidate reconciliation gap — and (b) returns the
  repository's post-write-back `RepositorySnapshot` id, which
  `MergeAppliedPayload` now carries (`SnapshotId`, additive). The
  event-sourced projection copies it onto Integration nodes.
  **Correction to the hardening-pass finding:** "exactly one caller of
  `IRepositorySnapshotService.CreateAsync` (GitAdapter)" was wrong — a
  malformed grep. `RepositoryImportService` creates snapshots on bootstrap
  AND on every forced sync (`ImportedFromFilesystem` successors), so
  default-repo applies already advanced snapshots; what was genuinely
  missing was the multi-repo path, the proposal↔snapshot *linkage*, and a
  materialize-from-snapshot route — which is what this pass added.
- **True point-in-time materialize.**
  `POST /studio/repository-snapshots/{snapshotId}/materialize?targetPath=`
  → `IMaterializationEngine.MaterializeAsync` (TreeEntries + CAS).
  `targetPath` is REQUIRED — this endpoint refuses to default to the live
  repo. The drawer's materialize button uses it whenever the node carries a
  `SnapshotId` (label: "Materialize this point in time to scratch") and
  falls back to branch-current materialization otherwise, with the label
  saying which. Route deliberately not under `/studio/snapshots/*` — that
  prefix is ExecutionSnapshot (agent reasoning state).
- **Nested topology.** A proposal's first lifecycle node anchors to the
  nearest ancestor work unit with an emitted node — the parent's
  latest-earlier proposal node when one exists, the parent's GoalStarted
  node otherwise, walking up; still no dangling edges on branch-scoped
  requests. Fan-out children now chain to their parent's proposal instead
  of jumping to the root goal.
- **Bespoke DAG visual.** `dagRenderer` renders per-kind shapes/colors
  (goal=blue circle, integration=green diamond, rejection=red circle+X,
  superseded=gray diamond, dead branch=dark-red circle+X, external=amber
  square — `ReplayNode.payloadRef` carries the kind so no per-frame JSON
  parsing), draws the projection's edges as cross-lane connectors colored by
  kind (same-lane lifecycle chains arc above the lane line), and the panel
  has a legend row. The cursor always renders as a circle so "where am I"
  reads consistently.
- **Host-level integration tests** (`WorkspacePathwaysIntegrationTests`,
  TestServer + real service graph): a reviewed+applied proposal reaches the
  real HTTP route as an Integration node with `reviewedBy: "user"` and the
  goal→integration edge; a merged-then-superseded proposal keeps both nodes
  through the *real* event log — the end-to-end proof of the
  history-preservation fix.
- **One existing test adapted, deliberately:**
  `WorkspaceCacheManagerMultiRepoTests` manufactured "repo B merged but
  never resynced" as its setup — a state the multi-repo resync fix makes
  impossible through the apply path *by design*. Reworked to
  approved-but-unapplied for repo B, which preserves the test's actual
  target (eviction safety consults each work unit's OWN repository; the old
  always-global-default bug still fails the evictedA assertion).
- Full verification: Projections 31/31, Integration 425/425 (one
  pre-existing timing flake in `ClarificationWorkflowTests` observed once
  and passing on isolated + full re-runs — not introduced by this work),
  Merge 68/68, Core 39/39, Contracts 16/16, Tasks 11/11, AgentRuntime
  67/67; extension tsc/esbuild/webview-smoke clean.

**Still open / future work:** promotion-branch mode applies get no
`SnapshotId` until the human PromoteAsync writes to disk (documented
behavior — a promote-time event with the snapshot id is a possible
follow-up); peer replication / CAS distribution per the deployment scoping
section.

**Cleanup pass (2026-07-08, same session): `/studio/replay/timeline` removed,
plus a bigger finding it surfaced.** Confirmed no caller anywhere —
extension (already migrated to `WorkspacePathways` this session), in-process
agents, or external MCP clients — and removed `GET /studio/replay/timeline`,
`GET /studio/replay/timeline/{branchId}`, `GET /studio/replay/range/{branchId}`,
and their MCP counterpart `nm_v1_replay_range` outright (not demoted).
`IReplayService.RangeAsync` itself stays — it's still genuinely used by the
unrelated `/studio/trajectory/replay` feature.

Checking "is anyone external actually calling `nm_v1_replay_range`" surfaced
a real architectural gap: the intended `nms_v1_*` (external-caller,
14 tools) vs. `nm_v1_*` (internal agent, ~117 tools) split was a **naming
convention only** — `NodalMerge.Studio.McpServer/ServiceCollectionExtensions.cs`
registered the entire assembly (`WithToolsFromAssembly`) onto the external
HTTP MCP endpoint (`app.MapMcp("/mcp")`), so every internal `nm_v1_*` tool
was technically reachable by any external MCP client, contradicting the
"internal only" framing in `McpServerToolNames.cs`'s own doc comment. Fixed
by switching to explicit `WithTools<T>()` calls for just the 5
`External*Tools` classes — the external surface is now genuinely limited to
the 14 `nms_v1_*` goal/results/repo/workspace/clarification tools, enforced
at registration, not just by convention. Verified in-process agents are
unaffected: `McpToolDispatcher` is a plain DI singleton called directly
in-process (`GetRequiredService<McpToolDispatcher>()`), never through this
MCP HTTP registration at all.

Docs updated: `docs/contracts/mcp-v1-contract.md` and
`docs/reference/api-reference.md` now state the enforcement explicitly.
Verified: Host + McpServer build clean, Integration 425/425 (same
pre-existing flake pattern as above — different test each run, none
touching MCP registration), all six other unit suites green.

**Pathways slices 1–4 are now all shipped in some form** — 1–3 fully, 4
partially (attribution polish is the one deliberately-blocked item left).
What remains unstarted, in priority order: (a) closing verification item 1
for real (wire automatic `RepositorySnapshot` capture into merge-apply,
fix `BestEffortResyncAsync`'s multi-repo gap) — the prerequisite for true
point-in-time materialization and non-null `SnapshotId`s; (b) closing
verification item 2 for real (add reviewer-identity capture to
`ReviewAsync`/`MergeProposal`) — the prerequisite for attribution polish;
(c) nested branch topology (proposals anchoring to their immediate parent
work unit, not always the top-level goal); (d) a bespoke DAG/tree visual
with distinct per-kind icons, replacing the reused per-branch-lane SVG
renderer. None of these were attempted this session — each is a real,
separately-scoped piece of work, not a quick follow-on.

Everything below this line is the original design as agreed on 2026-07-08;
it still describes the target shape, now annotated inline per-slice with
what actually shipped vs. what's still open above.

## Problem

Pathways (the DAG Replay view in the Control Tower) is supposed to be the
differentiator: a git-like, branchable tree of the *workspace's* history — every
artifact merged into the DAG, every dead branch, every external edit — each node
carrying the agent context and file diffs that produced it, and each node
materializable into a scratch workspace or branchable with updated steering.

What it actually renders today is each agent's internal task list. The data
source is the problem, not the rendering:

- `ReplayService.BuildTimelineAsync`
  (`src/NodalMerge.Studio.Storage/ReplayService.cs`) flattens **every artifact**
  plus **every orchestration decision-log event** per work unit into per-branch
  lanes. That's where the `NoOp: Planner enqueued successfully`, `Enqueue: LLM
  profile selection is disabled`, `SpawnPlanner` noise comes from.
- The extension (`clients/vscode-extension/src/panels/DagReplayPanel.ts`) just
  draws whatever `/studio/replay/timeline` returns.
- Nothing in the timeline is anchored to repository state, so "Branch from here"
  only passes `seedFromBranchId` — the branch's *current* tip, not the state at
  the selected node. Nothing is actually branchable from a point in time.
- The same per-goal task-list information is already visible on the goal
  workspace, so Pathways duplicates it while adding nothing workspace-level.

## Vision alignment (why the design below, and not a simpler aggregation endpoint)

NodalMerge's identity: **a local-first distributed state engine built around
immutable operations, replayed deterministically into programmable
projections.** Studio is the agent-native workspace platform on top.

Two consequences for Pathways:

1. **Pathways must be a projection, not a bespoke REST aggregation.** A one-off
   `PathwaysGraphService` endpoint would be a server-side view only the Studio
   host can compute. Defined instead as a projection over the event/artifact
   log, the graph is a pure function of the log — which means when peer
   replication eventually lands, every replica (human Control Tower, headless
   agent, remote server) computes the *identical* Pathways graph locally. The
   bespoke endpoint would be thrown away at that point; the projection wouldn't.
   It also means agents can consume the same view humans see (via the existing
   projection MCP surface) — e.g. a reconciliation agent reading "what merged,
   what died, what changed externally" as context.

2. **Node identity comes from the log, not from repository snapshots.** The
   repo tree at a node is a *derived materialization* — a snapshot is a cached
   checkpoint of deterministic replay, not the identity of the node. Anchoring
   node identity in event/artifact IDs keeps nodes peer-stable and keeps the
   design "git for agent reasoning," not "git for files."

### Deployment scoping — what this plan does and does not assume

Current reality (and the target for this plan): **local Control Tower, local
file system, single user per extension instance.** Each user has their own DAG
and history in their own extension, separate from the repo, containing only
their own changes. Anything that happened off-machine (an edit made elsewhere
and pulled down, a teammate's commit) enters that user's DAG as an **external
input event** — it is *not* modeled as a peer's node, because there are no
peers yet.

Future state (explicitly **out of scope** here, recorded so the design doesn't
foreclose it): a remote standalone Studio server the Control Tower connects to,
holding the CAS and sharing files with users through the control panel — or
S3-delegated CAS where each blob is pulled from object storage on demand. CAS
replication and peer connection are not part of this plan. Nothing below
requires them; the projection framing just means Pathways is already the right
shape when they arrive.

## What already exists (build on these, don't reinvent)

| Capability | Where | State |
|---|---|---|
| Durable cross-session event log, queryable by kind (`MergeApplied`, `ProposalApproved/Rejected/Superseded`, `WorkUnitFailed/Abandoned`, `SessionBranchCreated`, `WorkspaceBranchCreated`, …) | `IExecutionEventStream` (`ServiceContracts.cs`), `ExecutionEventKind` | Real, persisted |
| External-edit detection at goal creation | `RepositorySyncService`, `SyncTrigger.GoalCreation`, `SyncReason.RepositoryDrift`, `PendingExternalSync` (added/modified/deleted), `ArtifactType.ExternalChangeset` | Real — just never surfaced in Pathways |
| Per-proposal file diffs (hunks + before/after) | `GetFileChangesAsync` → `ProposalFileChange`; Merge Review panel renders them, incl. "Open Diff in Editor" | Real |
| Agent conversation audit trail per work unit/cycle | `ConversationLogEntry` (assistant text, tool calls/results, model, tokens) | Real, persisted |
| **What the agent saw when it decided** | `ReasoningCommitNode.ProjectionSnapshotJson` | Real, persisted — stronger "agent context" than chat excerpts |
| Point-in-time materialization from CAS | `RepositorySnapshot.TreeEntries` (path→blobId) + `MaterializationEngine.MaterializeAsync` | Real (used for workspace init / known-good restore) |
| Snapshot lineage | `RepositorySnapshot.BaseSnapshotId` / `Generation`, one per goal cycle via between-run sync | Real |
| Projection machinery (15 types, levels, REST + MCP exposure) | `ProjectionManager` (`src/NodalMerge.Studio.Projections`), `ProjectionType` | Real |
| Re-run with different steering ("counterfactual") | `ICounterfactualService` / `CounterfactualCommand` — branch from a *proposal's* base state, re-run with different profile/model/goal-override/constraint | Real, but proposal-scoped and unreachable from Pathways |
| Known-good checkpoints + rollback | `IKnownGoodStateService`, `/studio/replay/rollback` | Real |

## Design

### New projection: `WorkspacePathways`

New `ProjectionType.WorkspacePathways` built in `ProjectionManager`, scoped to a
**repository/workspace** (not a work-unit tree, unlike `ReasoningCommitGraph`).
Sourced from the execution event stream + artifact store. Served through the
existing projection surface (REST + MCP), consumed by the Pathways webview.

**Node vocabulary** (provenance-bearing moments only — the orchestration
decision log is *excluded entirely*):

| Node type | Source | Notes |
|---|---|---|
| `goal-started` | `SessionStarted` / root `WorkUnitCreated` | Branch-off point from the trunk |
| `integration` | `MergeApplied` + its `MergeProposal` | Carries file-change summary (adds/mods/deletes), approver attribution |
| `rejection` / `dead-branch` | `ProposalRejected`, `ProposalSuperseded`, `WorkUnitFailed`, `WorkUnitAbandoned` | Terminal node on its branch |
| `external-update` | `ExternalChangeset` artifact (drift sync at goal creation) | "Files updated externally since last entry" — added/modified/deleted lists |
| `known-good` | `IKnownGoodStateService` states | Marker on an existing node, not a separate lane |

**Every node carries an `actor`**: `agent` (id + model), `human` (user), or
`external`. A human's off-machine edit and an agent's merge are the same kind of
node with different actors — this is the single-user-now / multi-actor-later
seam.

**Node ids are event/artifact ids from the log** — deterministic, and
peer-convergent later.

**Edges** come from snapshot lineage (`BaseSnapshotId`) + branch seeds: trunk =
accepted integrations into the workspace, side branches = goals in flight, dead
branches terminate at rejection/failure nodes.

**Each node also carries a `snapshotId`** (nullable) — the derived checkpoint
that makes it materializable. See verification item 1.

### Node detail (inspect)

Clicking a node shows, in one drawer:

1. **Agent context** — the `ReasoningCommitNode.ProjectionSnapshotJson` nearest
   the decision ("what the agent saw"), plus the surrounding
   `ConversationLogEntry` cycles (what it said/did).
2. **File diffs** — `ProposalFileChange` list for integration/rejection nodes;
   reuse Merge Review's rendering incl. "Open Diff in Editor". External-update
   nodes show the added/modified/deleted path lists (no hunks — we don't have
   before-content for external edits, and that's fine).

### Branch-from-node = generalized counterfactual

Extend `CounterfactualCommand` from "branch from a *proposal's* base state" to
"branch from *any pathway node*": resolve node → base state (via its snapshot,
or replay when the snapshot is absent) → new work unit with optional
profile/goal/constraint overrides. This is the "replay with updated steering"
differentiator, and it reuses the existing service instead of adding a parallel
mechanism. `seedFromBranchId` remains for "branch from current tip."

### Materialize-to-scratch

`POST /studio/pathways/materialize` (or equivalent MCP tool): node id → resolve
snapshot → `MaterializationEngine.MaterializeAsync` into a scratch directory →
return the path. UI: "Materialize workspace here" button next to "Branch from
here."

### Noise elimination

Pathways stops consuming `IOrchestrationDecisionLogService` output entirely.
The NoOp/Enqueue/Spawn chatter remains available where it belongs: the per-goal
`ReasoningCommitGraph` / `DecisionContext` projections and the Activity Center.
The old `/studio/replay/timeline` endpoint stays for compatibility until the
webview no longer calls it, then gets removed or demoted to a debug surface.

## Slices

### Slice 1 — `WorkspacePathways` projection + webview renders it — ✅ shipped 2026-07-08

- Added `ProjectionType.WorkspacePathways` + payload records
  (`WorkspacePathwaysNode`, `WorkspacePathwaysEdge`) in
  `NodalMerge.Studio.Contracts/Projections`.
- Built in `ProjectionManager` from `IWorkUnitService` + `IMergeService` +
  `IStudioNodeStore` (for `ExternalChangeset` artifacts — see "What slice 1
  deliberately simplified" in Status). Not `IExecutionEventStream` or
  `IKnownGoodStateService` as originally sketched — those didn't have the
  right shape/query surface for this; no new persistence either way.
- Exposed through the existing generic `/studio/projections/{projectionType}`
  REST route and MCP surface, unchanged. No repository-scope field was needed
  (see Status — verification item 3 resolved).
- Webview (`dag-replay`): renders `WorkspacePathways` nodes via the existing
  per-branch-lane SVG renderer (lane = node's `BranchId`, synthetic
  `external-updates` lane for external nodes). Decision-log entries no longer
  reach Pathways at all. Distinct per-kind visuals/icons and edge-aware
  topology are deferred polish, not done in this pass.
- **This slice alone transforms the view** — everything after is enrichment.

### Slice 2 — node detail: context + diffs — ✅ shipped 2026-07-08

- `DagReplayPanel.ts`'s `inspectNode` handler fetches `ProposalFileChange[]`
  (`/studio/merges/{proposalId}/file-changes`) and `ConversationLogEntry[]`
  (`/studio/workunits/{workUnitId}/conversation-log`) — both pre-existing
  endpoints — alongside the existing `/studio/replay/inspect` call, for
  Integration/Rejection/Superseded nodes only.
- Not `ProjectionSnapshotJson` — see Status for why (dead code, nothing ever
  writes it).
- Drawer UI: inline diff rendering (reused `decisionConvergence.js`'s
  hunk-rendering logic/CSS classes) + a capped, chronological agent-context
  list. Not tabs — appended sections in the same scrollable drawer, simpler
  for the amount of content involved. "Open Diff in Editor" not wired (see
  Status).

### Slice 3 — snapshot linkage, materialize-to-scratch, branch-from-node — partially shipped 2026-07-08

- ~~Enforce snapshot-at-integration~~ **Verified, not enforced.** Item 1 is
  now a confirmed, precisely-scoped gap (see Status) — adding the actual
  `RepositorySnapshot` write is unstarted, real follow-up work, not done in
  this pass.
- ~~`POST /studio/pathways/materialize` → `MaterializationEngine`~~ **Shipped,
  different route than sketched.** Used the already-existing
  `POST /studio/projections/{workUnitId}/materialize` (→
  `IProjectionMaterializer`, which uses `IFileWorkspaceService`, not
  `IMaterializationEngine`/CAS) instead of adding a new `/studio/pathways/*`
  endpoint — no new backend surface needed. Reconstructs current live branch
  content, not a point-in-time snapshot (see Status item 1 for why that's
  the honest scope right now).
- **Shipped as sketched.** `CounterfactualCommand` didn't need generalizing
  — `PathwaysNode.proposalId` already *is* the `ProposalId` it expects. Wired
  "Branch from here (new steering)" in the webview to the pre-existing
  `POST /studio/counterfactuals`.

### Slice 4 — external-update surfacing + attribution polish — partially shipped 2026-07-08

- ~~Surface `ExternalChangeset` artifacts as `external-update` nodes~~
  **Already done in slice 1.** What was missing and got added now:
  `WorkspacePathwaysNode.FilesTouched` for ExternalUpdate nodes (parsed from
  the artifact's `added`/`modified`/`deleted` body fields) and a real
  file-level diff, not just counts — `ExternalSyncStateIdBefore`/
  `ExternalSyncStateIdAfter` (the `KnownGoodState` pair
  `RepositorySyncService` already marks immediately before/after applying
  the drift) let the drawer's new "View file changes" button call the
  existing `GET /studio/projections/known-good/{a}/diff/{b}` (→
  `IProjectionMaterializer.DiffKnownGoodStatesAsync`) on demand. Path-list
  diff only (no hunks) — no before-content is captured for an external edit,
  exactly as originally scoped.
- ~~Optional manual "Sync now" action~~ **Shipped.** A toolbar button
  (`resolveRepositoryPath()` → `POST /studio/workspace/switch` with the
  *same* path, which is `SyncTrigger.ManualRefresh` under the hood — no new
  endpoint added; re-setting `SeedRepositoryPath` to its current value is a
  harmless no-op mutation).
- Approver attribution polish: **not done, blocked** — see verification item
  2's finding (Status section): there is no approver-identity data anywhere
  in the system to render distinctly. This needs the schema/API change
  itself, not UI polish, and stays out of scope.

## Open verification items

Verify during slice 1/3 rather than assume:

1. **Does every merge-apply reliably produce a `RepositorySnapshot` in all
   paths?** Between-run sync writes one per goal cycle and
   `SyncTrigger.PostMergeWriteBack` exists, but reconciliation merges
   (candidate-branch-into-candidate-branch from multiple top-level goals — the
   exact case observed in the UI) may bypass it. If there are gaps, slice 3
   closes them.
2. **Is user-vs-agent approval attribution recorded on `MergeProposal`?**
   `ReviewPolicy` exists and agent-approved merges happen, but confirm the
   approver identity is persisted (not just the status transition). If it
   isn't, add it — it's needed for actor attribution on integration nodes.
3. **Does `ProjectionRequest` need a repository-scope field?** Current fields
   are `WorkUnitId`/`BranchId`/`AgentId`. `WorkspacePathways` wants
   `RepositoryId` (or repositoryPath). Adding a nullable field is a frozen-v1
   contract question — check `docs/contracts` versioning rules first.
4. **Are `ReasoningCommitNode` records written for the decisions Pathways
   surfaces** (merge approve/reject), or only for orchestration steps? If the
   latter, node detail falls back to `ConversationLogEntry` only, and
   `ProjectionSnapshotJson` display becomes best-effort.
5. **Event-log retention/rehydration**: `WorkspacePathways` assumes the
   execution event stream is fully rehydratable across host restarts the same
   way node-store-backed services are. Confirm before making it the graph
   spine.

## Out of scope (recorded on purpose)

- CAS replication, peer connection, peer-to-peer file swapping.
- Remote standalone Studio server / S3-delegated CAS (future state; the
  projection framing is what keeps us compatible with it).
- Capturing external edits beyond the existing drift-sync granularity (e.g.
  mining git commits pulled from another machine into individual nodes). An
  external-update node with added/modified/deleted lists is the v1 contract;
  richer sources can upgrade the payload later without changing the node type.
- Streaming/live updates to the Pathways graph (existing poll cadence is fine
  for v1).
