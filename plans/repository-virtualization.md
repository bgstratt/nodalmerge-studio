# Repository Virtualization Architecture

## The Core Shift

Today NodalMerge has two worlds:

- **Knowledge** is immutable, event-sourced, replayable, and projection-driven.
- **Code** lives in mutable directories that are treated as special.

This plan closes that gap. The repository itself becomes a room in the DAG — another event stream with projections, materializers, and a cache layer. Code becomes just another kind of immutable artifact.

The filesystem is then merely one possible projection of repository state. It is not the source of truth.

---

## The Architectural End State

```
Repository Room (DAG)
  │
  ├── GitImportSnapshot (base: abc1234...)
  │         │
  │         ├── RepositoryOp (Add src/Auth/UserService.cs → blob:aa0f6...)
  │         ├── RepositoryOp (Replace src/Auth/Token.cs   → blob:bb182...)
  │         ├── RepositoryOp (Delete src/Legacy/Old.cs)
  │         │
  │         └── RepositorySnapshot (after WU-14's ops)
  │                   │
  │                   ├── RepositoryOp (Replace src/UI/Dashboard.tsx → blob:cc77b...)
  │                   │
  │                   └── RepositorySnapshot (after WU-22's ops)
  │
CAS (.nodalmerge/cas/)  ← FileBlobStoreProvider, Blake3 hashes
  │
  ├── aa/ aa0f6be3....blob
  ├── bb/ bb182a47....blob
  └── cc/ cc77b8f2....blob

Materialization Cache (temp/studio-workspace/)
  │
  └── branch-WU-14/   ← disposable, rebuildable
```

Git becomes an import/export adapter. Filesystem becomes a build/test cache.
Agents become producers of RepositoryOps, not editors of mutable directories.

---

## What This Enables

- **No more 26 copies of the repo.** Files unchanged across work units exist once in CAS.
- **Eviction is safe.** Workspace directories can be deleted at any time; they're reconstructable.
- **Conflicts are structural.** Two ops forking from the same blob are a DAG conflict, detected at emit time, not at merge time.
- **Partial materialization.** An agent declared `src/Auth/**` only materializes those paths.
- **Provenance is complete.** Every file change has an agent, a reason, a parent blob, and a work unit.
- **Human edits are ops.** The user becomes another producer of RepositoryOps via the workspace watcher.
- **Op history as agent context.** An agent reading `UserService.cs` can receive the last N operations on that file — who changed it, which work unit, and why — without the agent having to read git history or ask. This is context no filesystem-only system can provide.
- **Co-modification intelligence.** The op log accumulates which files are consistently changed together. This feeds planner hints ("you usually also touch `AuthMiddleware.cs` when modifying `UserService.cs`"), scope recommendations, and pattern-based Findings.

---

## Model

### CAS Blob

```
Blake3(content) → bytes
```

No metadata. No knowledge of what references it. Content-addressed. Deduplicated by construction.
Blake3 matches the hash algorithm used throughout the NodalMerge host engine (`nodalmerge_core::Hash`).

### RepositoryOperation

```
OperationId      string
RepositoryId     string
ParentSnapshotId string
WorkUnitId       string?
OperationType    Add | Replace | Delete | Rename | Move | Import
Path             string
OldBlobId        string?    // null for Add/Import
NewBlobId        string?    // null for Delete
Timestamp        DateTimeOffset
AgentId          string?
Reason           string?    // the primary field surfaced in agent projections
```

No line-based diff. Blob transitions are the canonical representation. Diffs are derived on
demand from `OldBlobId → NewBlobId` and are display-only, not storage.

`Reason` is the human-readable explanation the agent or user provided at write time. It is the
field that makes op history meaningful as context: "replaced because the sliding expiration
requirement changed" is information an agent reading the file today cannot recover from the
bytes alone.

### RepositorySnapshot

```
SnapshotId       string
RepositoryId     string
BaseSnapshotId   string?   // null = root (GitImportSnapshot)
TreeHash         string    // SHA256 of sorted (path, blobId) pairs
GitCommit        string?   // non-null only for import/export snapshots
Generation       long      // monotonically increasing; cheap ordering without timestamp comparison
CreatedAt        DateTimeOffset
WorkUnitId       string?   // which work unit produced this snapshot, if any
```

`TreeHash` gives O(1) repository equality. Two snapshots with the same TreeHash are identical
regardless of which op chains produced them.

### BlobIndex — DROPPED

Reference counting was planned but not built. Phase 8 implements scan-based GC instead: collect
all blob hashes across every `RepositorySnapshotV1` node's `TreeEntries` plus every
`RepositoryOpV1` node's `NewBlobId`/`OldBlobId`, then delete anything not in that set.

This is provably correct: if a blob has no referrer in any snapshot or op, it is truly orphaned
regardless of how it got there. The crash case (blob written, process dies before op is recorded)
is also correct — the op that would have pointed to it doesn't exist, so the data is unrecoverable
anyway; GC'ing the orphaned blob is the right outcome.

Reference counting buys O(1) GC lookup instead of O(snapshots × entries). At the scale this
system operates, the scan is fast enough. The feature is formally dropped; the scan-based approach
in `WorkspaceCacheManager.GetLiveBlobHashesAsync` is the permanent implementation.

### Repository Room

Each repository is its own CRDT room: `repo:{repositoryId}`. Repository op volume is high and
would pollute the studio room's snapshot load if co-located. The studio room retains entity state
(WorkUnits, decisions, artifacts). Cross-room references use stable IDs (`RepositoryId`,
`SnapshotId`).

---

## Services That Need Explicit Evolution

| Service | Current Role | Evolution |
|---|---|---|
| `FileSystemWorkspaceService` | Source of truth for file content | Compatibility layer; delegates to CAS on write |
| `AgentWorkspaceService` | Wraps a branch directory | `Path` becomes a nullable cache hint, not identity; workspace = snapshot + op chain |
| `KnownGoodStateService` | Captures branch state | Captures `SnapshotId` instead of directory state |
| `FileLeaseService` | File-level write locking | Deprecated once conflict detection is structural (DAG ancestry) |
| `RepositorySyncService` | Diffs filesystem against repo | Becomes the user-edit import path in Phase 5 |
| `WorkUnit.BranchId` | Branch directory name | Augmented with `BaseSnapshotId`; eventually `BranchId` becomes a projection |

---

## Milestones

### Milestone 1 — Record (non-breaking) ✅ COMPLETE

Introduce the CAS and RepositoryOp infrastructure. All existing file writes continue to work.
Nothing breaks. The system begins building an immutable history as a side effect of normal operation.

**Phases:**
- ✅ [Phase 0](#phase-0--model-and-bounded-context) — Define the Repository bounded context
- ✅ [Phase 1](#phase-1--content-addressable-storage) — CAS abstraction + local implementation
- ✅ [Phase 3](#phase-3--repository-operations) — RepositoryOperation nodes
- ✅ [Phase 4](#phase-4--filesystem-compatibility-layer) — Dual-write (file + op)
- ✅ [Phase 4.5](#phase-45--op-history-in-agent-projections) — Op history surfaced in `AgentWorkspaceProjectionPayload`
- ✅ [Phase 5](#phase-5--user-workspace-import) — User edits become imported RepositoryOps

**Implementation notes:**
- SHA256 used throughout C# layer instead of Blake3 (Blake3 is Rust-only; not exposed to C#)
- Phase 5 Case 2 (between-run sync) moved to Milestone 2 — requires Phase 2 snapshot infrastructure
- `RepositoryId` is `Path.GetFullPath(SeedRepositoryPath)` — single active repo per Studio instance
  until real multi-repo support is built (see `Repository.cs` doc comment)
- `WorkspacePathFilter` extracted as internal shared utility; `.nodalmerge` added to `IgnoredDirNames`

**Exit criteria:** Every file write produces a RepositoryOp node. The full op log is replayable.
An agent assigned to a file scope receives the recent op history for those paths — who changed
each file, in which work unit, and why — as part of its standard projection payload.
Workspace directories can still be deleted manually without consequence to correctness.

---

### Milestone 2 — Reconstruct

Introduce snapshots and the materializer. Workspace directories become cache entries that can be
safely evicted and rebuilt on demand. The BlobIndex enables safe garbage collection.

**Phases:**
- ✅ [Phase 2](#phase-2--repository-snapshot-model) — Snapshot nodes + TreeHash + Phase 5 Case 2 (between-run sync)
- ✅ [Phase 6](#phase-6--snapshot-compaction) — Compaction (event-sourcing pattern)
- ✅ [Phase 7](#phase-7--materialization-engine) — Materializer (snapshots + CAS → filesystem)
- ✅ [Phase 8](#phase-8--workspace-cache-manager) — Eviction policies + BlobIndex GC

**Exit criteria:** `rm -rf` on any workspace directory is safe at any time. Materializer
reconstructs it from snapshot + CAS. Disk accumulation problem is solved. Between-run user edits
are detected and emitted as RepositoryOps before each goal run (Phase 5 Case 2).

---

### Milestone 3 — Virtualize

Workspace directories stop being the source of truth. Conflicts are structural. Materialization
is scoped to the agent's owned paths.

**Phases (implementation order):**
- ✅ [Phase 9](#phase-9--conflict-engine) — Structural conflict detection via blob ancestry
- ✅ [Phase 11](#phase-11--projection-aware-materialization) — Partial materialization by file scope
- ✅ [Phase 11.5](#phase-115--co-modification-intelligence) — Co-modification patterns as planner hints and Findings
- ✅ [Phase 10](#phase-10--intelligent-merge) — Merge strategies as RepositoryOps (last: most complex, depends on 9 being stable)

**Implementation order rationale:** Phase 11 (partial materialization) and 11.5 (co-mod intel)
have zero dependency on conflict detection and deliver independent value. Phase 10's merge
strategies — especially AST merge via Roslyn — are Milestone 3's most complex work and benefit
from everything else being stable. `NonOverlappingFileScopeRule` is kept alive through Phase 9
and deprecated only once Phase 10's resolution path is end-to-end.

**Exit criteria:** Two agents writing to overlapping paths produce a detectable DAG fork at
emit time, not at merge time. `FileLeaseService` is deprecated. Agents never materialize more
than their declared scope. The planner receives co-modification hints derived from the
accumulated op log, and the Finding system surfaces anomalous patterns.

---

### Milestone 4 — Decouple

CAS is pluggable. Agents can operate without a materialized filesystem. Git is an adapter.

**Phases:**
- ✅ [Phase 12](#phase-12--tool-virtualization) — Repository-aware agent tools without filesystem dependency
- ✅ [Phase 13](#phase-13--remote-repository-rooms) — CAS backends (S3, Azure Blob, cloud cache)
- ✅ [Phase 14](#phase-14--git-as-importexport-adapter) — Git becomes boundary adapter only

**Exit criteria:** A worker agent can read and write files entirely through CAS-backed tools
without a materialized directory. Git is used only on import and export.

**Post-M4 additions (same session):**
- MCP HTTP server tool parity: `McpServer/Tools/RepositoryBlobTools.cs` exposes all four
  Phase 12 blob tools (`nm_v1_repository_blob_*`) to external harnesses (Copilot, Cline, etc.)
  with the same semantics as the internal dispatcher.
- `GET /studio/startup-config` — returns settings file path, effective CAS/blob backend values,
  and copy-paste JSON snippets for all startup-only config (CasRootPath, BlobStorageProvider).
- `GET /POST /studio/options` expanded: now includes `blockConflictingOps`,
  `materializerConcurrency`, `snapshots.{snapshotOnSync, opsPerSnapshot}`,
  `docFetchAllowedDomains`, `docFetchDeniedDomains`, `allowAgentGitCommits`, `allowAgentGitPush`.
- Git commit/push opt-in flags added to `WorkspaceOptions` and persisted via `RuntimeSettingsService`.

---

## Phase Details

### Phase 0 — Model and Bounded Context

Define the types before writing any infrastructure. Add to `NodalMerge.Studio.Contracts`:

```
Domain/
  RepositoryOp.cs          RepositoryOperation record + OperationType enum
  RepositorySnapshot.cs    RepositorySnapshot record
  BlobIndex.cs             BlobIndex record + BlobIndexEntry record
```

Add to `StudioNodeKind`:

```csharp
public const string RepositoryOpV1       = "studio/repository-op/v1";
public const string RepositorySnapshotV1 = "studio/repository-snapshot/v1";
public const string BlobIndexEntryV1     = "studio/blob-index-entry/v1";
```

No implementation. Only the model. The CAS interface (`IBlobStoreProvider`) already exists in
`NodalMerge.Host.Abstractions` — no new abstraction needed here.

---

### Phase 1 — Content Addressable Storage

**This is mostly already built.** The NodalMerge host already provides:

- **`IBlobStoreProvider`** (`NodalMerge.Host.Abstractions.Providers`) — `TryGetBlobAsync(hashHex)` /
  `PutBlobAsync(hashHex, bytes, contentType)`. This is the CAS interface Studio will use.
- **`FileBlobStoreProvider`** — local filesystem backend with 2-char shard dirs and `.blob` extension.
  Stores blobs at `{RootPath}/{shard}/{hash}.blob`.
- **`FileBlobGcCoordinator`** — tombstone-based GC with configurable grace window and dry-run support.
- **`S3DelegatedBlobUrlResolverProvider`** — S3 backend with presigned URL support (Direct and Delegate modes).
- **Blake3** — the hash algorithm used throughout (not SHA256). All blob IDs are Blake3 hex strings.

**What Phase 1 actually does:** configure and wire the host's existing blob store for repository use.

**Storage layout** (via `FileBlobStoreProvider` with configured root):

```
.nodalmerge/cas/
  aa/
    aa0f6be3...48d1.blob
  bb/
    bb182a47...9c3f.blob
```

**Configuration:** In `WorkspaceOptions`, add `CasRootPath` (defaults to
`.nodalmerge/cas` relative to `SeedRepositoryPath`). The extension passes this to
`FileBlobStorageOptions.RootPath` when constructing the host.

**Test isolation:** The in-memory host used by `AddInMemoryStorage` already has an in-memory blob store.
No `MemoryCasStore` needs to be built — it already exists at the host layer.

**S3/cloud backends** are already implemented in the host (Phase 13 just configures Studio to use them).

---

### Phase 2 — Repository Snapshot Model

A snapshot is a complete, named repository state. It is the replay checkpoint.

**Creation triggers:**
- Git import (produces `GitImportSnapshot` with `GitCommit` set)
- Work unit completion with at least one RepositoryOp since the last snapshot
- Manual checkpoint (user-initiated or policy-driven)

**TreeHash computation:**

```
sorted list of (path, blobId) pairs for all non-ignored files
→ Blake3 of that list serialized as "path:blobId\n" lines
```

TreeHash can be computed incrementally: when applying a single RepositoryOp to an existing
snapshot, only the changed path needs updating. No full re-hash.

**`Generation`** is a monotonically incrementing counter per repository (stored in the node store
alongside the snapshot). It enables cheap ordering without timestamp comparison and is the
correct cursor for "give me all ops after snapshot N."

---

### Phase 3 — Repository Operations

RepositoryOps are immutable DAG nodes. They record blob transitions, not diffs.

**Node store kind:** `studio/repository-op/v1`

**Key constraint:** `OldBlobId` must match the blob currently at `Path` in the parent snapshot.
If it doesn't, the op is a conflict (two agents diverged from the same parent blob). Validation
happens at emit time in `RepositoryOpService.EmitAsync`, not at merge time.

**Import operations** (`OperationType.Import`) have no `OldBlobId`. They are produced once per
file during a `GitImportSnapshot`. The import op is the root of every file's lineage.

---

### Phase 4 — Filesystem Compatibility Layer

`FileSystemWorkspaceService.WriteAsync` and `ReplaceAsync` become dual-write:

```
WriteAsync(branchId, relativePath, content)
  1. Compute blobId = Blake3(content)           // matches host's Hash.of(bytes)
  2. await _blobStore.PutBlobAsync(blobId, ...)  // IBlobStoreProvider; idempotent
  3. await _repoOpService.EmitAsync(op)          // RepositoryOp to node store
  4. await _fileSystem.WriteAllTextAsync(...)    // existing behavior, unchanged
```

`DeleteAsync` emits a `Delete` op with `NewBlobId = null`. No `PutBlobAsync` call since there is
no new blob to store.

The agent tools (`nm_v1_workspace_write`, `nm_v1_workspace_replace`) call through
`IFileWorkspaceService` unchanged. No MCP contract changes in this phase.

**Test isolation:** The in-memory host backing used by `AddInMemoryStorage` already carries an
in-memory blob store. No additional test infrastructure needed.

---

### Phase 4.5 — Op History in Agent Projections

Once Phase 4's dual-write is in place, the op log exists. This phase surfaces it as first-class
context in `AgentWorkspaceProjectionPayload`.

**New types (additive — zero breaking changes):**

```csharp
// Added to NodalMerge.Studio.Contracts/Projections/
public sealed record FileOpHistory(
    string Path,
    IReadOnlyList<FileOpSummary> RecentOps);

public sealed record FileOpSummary(
    string OperationId,
    string? WorkUnitId,
    string? WorkUnitGoal,   // denormalized for prompt readability
    string? AgentId,
    string OperationType,
    string? Reason,
    DateTimeOffset OccurredAt);
```

**`AgentWorkspaceProjectionPayload` change:**

```csharp
// New optional field; null until Phase 4.5 is deployed (backward compatible)
IReadOnlyList<FileOpHistory>? RecentFileOps = null
```

**`ProjectionManager` update:**

When building an `AgentWorkspaceProjectionPayload`, if a `RepositoryOpService` is registered:
1. Take the work unit's `FileScope` paths
2. Query `RepositoryOpService.GetRecentOpsForPathsAsync(paths, limit: 5)`
3. Denormalize `WorkUnitGoal` from `IWorkUnitService` (one lookup per distinct `WorkUnitId`)
4. Set `RecentFileOps` on the payload

The query is bounded (last 5 ops per path, configurable) and filtered to the work unit's scope —
an agent only sees history for the files it is authorized to touch.

**Prompt surface:**

Add to the agent system prompt template (after the file scope block):

```
## Recent file history
{{#each RecentFileOps}}
### {{Path}}
{{#each RecentOps}}
- {{OccurredAt | short-date}} — {{OperationType}} by {{AgentId ?? "user"}}
  Work unit: {{WorkUnitGoal ?? WorkUnitId ?? "unknown"}}
  Reason: {{Reason ?? "(no reason recorded)"}}
{{/each}}
{{/each}}
```

When `RecentFileOps` is null or empty, this section is omitted entirely.

**Why this ordering matters:** Phase 4.5 comes before Phase 5 (user import) so that the first
human edits imported in Phase 5 also appear in the projection history, not just agent edits.

---

### Phase 5 — User Workspace Import

This phase handles the **initial bootstrap** — the first ever goal run for this repository, when
no `RepositorySnapshot` exists. It runs at goal start (inside `RepositorySyncService`), before
any agent is assigned a branch.

**Case 1 — Bootstrap (no prior `RepositorySnapshot` exists for this repository):**

1. Walk every non-ignored file in `SeedRepositoryPath` (skip `.git/`, `node_modules/`, `bin/`,
   `obj/`, `.nodalmerge/`, and Studio-internal paths)
2. For each file: write content to CAS via `IBlobStoreProvider.PutBlobAsync(SHA256(content), ...)`
   (SHA256 — Blake3 is Rust-only; C# layer uses SHA256 throughout)
3. Emit a `RepositoryOp` of type `Import` for each file (`OldBlobId = null`, `NewBlobId = hash`)
4. Compute a `TreeHash` from a sorted list of `"{path}:{blobId}\n"` pairs (SHA256 of concatenated
   entries), then record a `RepositorySnapshotV1` node with `BaseSnapshotId = null`,
   `Generation = 0`, and `GitCommit` set to the current HEAD (if available). This is the root of
   the entire op lineage.

Bootstrap runs exactly once per repository lifetime — checked via "does any `RepositorySnapshotV1`
node exist for this `repositoryId`?" in the node store. Once the snapshot node is written,
subsequent goal runs see the snapshot and skip to Case 2.

**Case 2 — Between-run sync (snapshot exists, user may have edited files directly):**

*Moved to Milestone 2.* This case requires the Phase 2 snapshot infrastructure (stored path→blobId
map) to efficiently diff the filesystem against the snapshot. It is implemented as part of Phase 2
once that infrastructure exists.

**Out of scope for this plan — Continuous watcher:** A filesystem watcher on `SeedRepositoryPath`
could emit ops as the user saves files, rather than only at goal start, advancing the snapshot
incrementally between goals. This is a natural follow-on but is not part of any phase here.

---

### Phase 6 — Snapshot Compaction ✅ COMPLETE

Replaying 50,000 ops from the git import baseline is impractical. Snapshots are the compaction
boundary — the same pattern as event-sourcing's checkpoint.

**Policy:** `OpsPerSnapshot` threshold in `WorkspaceOptions.Snapshots` (null by default — opt-in).
When set, `IRepositorySnapshotService.ConsiderCompactionAsync` checks op count since the last
snapshot and creates a `"Compaction"` snapshot if the threshold is met. Called at the start of
`RepositoryImportService.EnsureBootstrappedAsync` so mid-goal agent writes are compacted before
the between-run filesystem sync runs. Per-work-unit-completion trigger deferred to Phase 7.

**Replay via `ApplyOps`:** `InMemoryRepositorySnapshotService.ApplyOps` replays a chronologically
ordered op list onto a base `TreeEntries` dict. Add/Replace/Import → set path. Delete → remove.
Rename/Move reserved for Phase 10.

**`GetOpsSinceAsync`:** `InMemoryRepositoryOpService` maintains a second `_byRepository` index
alongside the path-keyed index. O(n) filter by Timestamp — fast in practice (n ≤ OpsPerSnapshot).

**Retention/pinning:** Deferred to Phase 8 (BlobIndex GC). Intermediate snapshots are not yet
pruned; all snapshot nodes accumulate in the node store until Phase 8 implements eviction.

---

### Phase 7 — Materialization Engine ✅ COMPLETE

**`IMaterializationEngine`** — `MaterializeAsync(snapshot, targetPath, fileScope?)` and
`RematerializeAsync(snapshot, previousSnapshot, targetPath, fileScope?)`.

**Algorithm:** Filter entries by fileScope → delete files absent from snapshot (within scope) →
fetch missing/changed blobs from CAS in parallel (bounded by `WorkspaceOptions.MaterializerConcurrency`,
default 4) → skip files already on disk whose SHA256 matches the expected blobId.
`RematerializeAsync` diffs two snapshots and only touches changed/added/removed paths.

**Integration:** `FileSystemWorkspaceService.InitBranchAsync` uses the materializer for "main"
when a snapshot with `TreeEntries` exists, falling back to `CopyDirectory` from `SeedRepositoryPath`
when CAS is unavailable. Child branches continue to copy from their seed branch directory, which
is itself now reconstructable. `NullMaterializationEngine` (null-object) used in test environments
where `IBlobStoreProvider` is absent — no null checks in `FileSystemWorkspaceService`.

**Hardlinks/reflinks:** Skipped — premature optimization. File copy via `WriteAllBytesAsync` is
sufficient for Milestone 2. Phase 11 (partial materialization) can revisit if needed.

---

### Phase 8 — Workspace Cache Manager ✅ COMPLETE

**`IWorkspaceCacheManager`:** `MaterializeAsync(workUnitId)`, `EvictAsync(workUnitId)`,
`EvictOrphanedAsync()`, `GetLiveBlobHashesAsync()`.

**Safe eviction invariant:**
- `Cancelled` work units: always evict (their changes were never merged; no snapshot needed).
- `Completed`/`Merged` work units: only evict when `snapshot.CreatedAt > wu.UpdatedAt`, ensuring
  the between-run sync captured their changes before the branch directory is deleted.
- `Failed`/`DeadLettered` work units: never auto-evicted (files stay inspectable).

**Blob GC:** `GetLiveBlobHashesAsync` collects all blob hashes from every `RepositorySnapshotV1`
node's `TreeEntries` plus all `RepositoryOpV1` node `NewBlobId`/`OldBlobId` values. The host-layer
REST endpoint `POST /studio/cache/gc` calls `FileBlobGcCoordinator` (already implemented in
`NodalMerge.Host.Composition` with tombstone grace window + `DryRun`/`LiveRun` modes) with this
live hash set. Blob GC stays in the host layer to avoid adding a `Host.Composition` reference to
`Studio.Storage`.

**BlobIndex reference counting** (original plan): deferred. Scanning all snapshot nodes for live
hashes is safe and sufficient for Milestone 2; reference counting is a Phase 11+ optimization.

**Idle/LRU eviction triggers**: deferred to Phase 11+ (requires idle-time tracking infrastructure).

**Startup:** `WorkspaceCacheManager` implements `IHostedService`; `StartAsync` fires-and-forgets
`EvictOrphanedAsync` as a best-effort background sweep without blocking the startup chain.

**REST endpoints:** `POST /studio/cache/materialize`, `POST /studio/cache/evict`,
`POST /studio/cache/evict/orphaned`, `POST /studio/cache/gc?dryRun=true|false`.

---

### Phase 9 — Conflict Engine ✅ COMPLETE

**Detection:** `InMemoryRepositoryOpService.EmitAsync` calls `FindForkingOpsAsync` before writing
each `Add`/`Replace`/`Delete` op. A fork is any existing op for the same `(repositoryId, path)`
(scoped to ops since the latest snapshot) that shares `OldBlobId` but has a different `NewBlobId`.
Two concurrent `Add` ops both have `OldBlobId = null` — the null == null comparison catches that
case naturally. `Import` ops are excluded from conflict detection (system-level bootstraps).

**Conflict record:** `RepositoryConflict` in Contracts captures `OpIdA`/`OpIdB`, shared
`OldBlobId`, divergent `BlobIdA`/`BlobIdB`, `WorkUnitIdA`/`WorkUnitIdB`, `DetectedAt`, and
`Status` (`Open` | `Resolved` | `Dismissed`).

**Policy:** `WorkspaceOptions.BlockConflictingOps` (default `false`). When false, both ops land
and the conflict is recorded for Phase 10 resolution. When true, `ConflictingOpException` is
thrown and the second op does not land.

**`IConflictService`:** `RecordAsync`, `GetActiveAsync(repositoryId)`, `GetAsync(conflictId)`,
`DismissAsync`, `MarkResolvedAsync` (Phase 10 hook). `InMemoryConflictService` is rehydratable.

**`NonOverlappingFileScopeRule`:** kept active — structural conflict detection is strictly more
precise but `FileLeaseService` and scope rules remain as belt-and-suspenders until Phase 10's
resolution path is end-to-end and proven in production.

**REST:** `GET /studio/conflicts`, `GET /studio/conflicts/{id}`, `POST /studio/conflicts/{id}/dismiss`.
`POST /studio/conflicts/{id}/resolve` ships in Phase 10.

**ConflictResolutionOp** (original plan's named node): deferred to Phase 10 — modeled as
`OperationType.Resolve` on `RepositoryOperation` so replay stays uniform.

---

### Phase 10 — Intelligent Merge ✅ COMPLETE

**New contracts (`Contracts/Domain/MergeStrategyContracts.cs`):**
- `LlmMergeCredentials(Provider, Model, BaseUrl, ApiKey)` — optional, passed per-request
- `MergeContext(ConflictId, RepositoryId, Path, BaseContent?, ContentA?, ContentB?, LlmCredentials?)` — inputs
- `MergeStrategyResult(Success, MergedContent?, StrategyName, FailureReason?)` — per-strategy output
- `ConflictResolutionResult(Success, ConflictId, StrategyUsed, ResolutionOpId?, FailureReason?)` — service output

**New interfaces (`Core/Services/ServiceContracts.cs`):**
- `IMergeStrategy` — pluggable strategy: `Name` + `MergeAsync(MergeContext, ct)` → `MergeStrategyResult`
- `ILlmMergeProvider` — LLM bridge (defined in Core, implemented in AgentRuntime)
- `ISourceValidator` — Roslyn syntax check (defined in Core, implemented in Storage)
- `IConflictResolutionService.ResolveAsync(conflictId, strategy?, llmCredentials?, ct)` — orchestrator

**New `LineDiffer.DiffRaw(before, after)` in Merge:**
Returns raw `IReadOnlyList<DiffLine>` (no hunk grouping) — used by ThreeWayMergeStrategy.

**Implementations (in strategy chain order):**

1. `ThreeWayMergeStrategy` (Merge project):
   - Converts `DiffRaw(base→A)` and `DiffRaw(base→B)` into `Edit(BaseStart, BaseEnd, NewLines[])` lists
   - Walks base applying both edit scripts; succeeds when edit regions don't overlap or overlap identically
   - Returns null (falls through) when same base region is changed differently by A and B

2. `AstMergeStrategy` (Merge project):
   - C# files only — ThreeWay + `ISourceValidator` Roslyn parse validation
   - Falls through to LLM if ThreeWay fails OR merged output doesn't parse
   - **Full AST-level declaration merging is permanently dropped.** The case it solves (two agents
     each adding a distinct method to the same class) is already handled by `LlmAssistedMergeStrategy`,
     which understands semantic intent rather than line positions. Building a hand-coded AST merger
     for every C# member kind buys determinism at high complexity cost; the LLM fallback is already
     correct for these cases.

3. `LlmAssistedMergeStrategy` (Merge project):
   - Calls `ILlmMergeProvider.MergeAsync(context, ct)` — needs `LlmCredentials` in `MergeContext`
   - Falls through when provider is null or model returns "CONFLICT"

4. `HumanReviewStrategy` (Merge project):
   - Always returns `Success=false` — terminal, surfaces in UI merge review panel

**`ConflictResolutionService` (Merge project):**
- Reads blobs via `IBlobStoreProvider.TryGetBlobAsync` → `Encoding.UTF8.GetString`
- Tries strategies in order (or specific strategy if named)
- On success: SHA256 new blob, `PutBlobAsync`, emit `RepositoryOperation(Kind=Replace, OldBlobId=conflict.OldBlobId, NewBlobId=mergedHash)`, `MarkResolvedAsync`

**`RoslynSourceValidator` (Storage project):** Uses `CSharpSyntaxTree.ParseText` to check for error-level diagnostics.

**`LlmMergeProvider` (AgentRuntime project):** Calls `LlmClient.SendAsync` with a concise system prompt. Returns null if the LLM responds with "CONFLICT".

**DI registration:**
- `AddStudioMerge()` in Merge: registers all 4 strategies as `IEnumerable<IMergeStrategy>` + `IConflictResolutionService`
- `AddStudioStorage()`: adds `ISourceValidator → RoslynSourceValidator`
- `AddStudioAgentRuntime()`: adds `ILlmMergeProvider → LlmMergeProvider`

**REST endpoint:**
- `POST /studio/conflicts/{conflictId}/resolve?strategy=` with optional `ConflictResolveBody(Provider?, Model?, BaseUrl?, ApiKey?)`
- Returns `ConflictResolutionResult` (200 = resolved, 422 = all strategies failed)

**`NonOverlappingFileScopeRule` status:** Kept active permanently (not deprecated by Phase 10).
Proactive blocking is better UX than reactive conflict resolution for same-file concurrent edits:
the agent whose work unit is blocked gets a clear planning-time signal rather than discovering
the conflict only after completing its work. The scope rule and Phase 10's resolution path are
complementary — the rule prevents new conflicts, the resolution path handles conflicts that do
slip through (e.g. under-declared scopes).

**`FileLeaseService` status:** Kept as belt-and-suspenders. `NonOverlappingFileScopeRule`
(`BlockOverlappingFileScope = true`) is the preferred proactive mechanism. When that option is
`false` (the current default), `FileLeaseService` remains the only active runtime protection.
Deprecation path: flip `BlockOverlappingFileScope` default to `true`, validate in production,
then remove `FileLeaseService`. Not done yet — behavioral default change warrants its own
decision.

**"Force Rebase" as 5th conflict outcome — ✅ IMPLEMENTED** (see post-Phase-10 additions below):
When all four merge strategies fail, `ConflictResolutionResult.RequeueLosingWorkUnit = true` is
set. The REST resolve endpoint reads this flag: when `WorkspaceOptions.AllowAutoRequeue = true`,
it looks up `conflict.WorkUnitIdB` (the losing work unit — the second op that detected the
conflict), creates a new work unit with the original goal + `parentWorkUnitId = losingWuId` +
original file scope, and returns the new `WorkUnitId`. The losing agent's prior attempt is
visible in the projection as prior-attempt history. Convention: `WorkUnitIdB` is the loser
(its op arrived second). `AllowAutoRequeue` defaults to `false`; opt-in for automated pipelines.

---

### Phase 11 — Projection-Aware Materialization ✅ COMPLETE

Instead of materializing the entire repository for each work unit, materialize only the paths
the work unit's file scope covers, plus project structure files needed by `WorkspaceProfileService`.

**Implementation (actual):**

`IFileWorkspaceService.InitBranchAsync` gained an optional `fileScope` parameter:
```csharp
Task InitBranchAsync(string branchId, string? seedFromBranchId = null,
    IReadOnlyList<string>? fileScope = null, CancellationToken ct = default);
```

`IBranchService.CreateBranchAsync` gained the same `fileScope` parameter and threads it
through to `InitBranchAsync`.

`InMemoryWorkUnitService.CreateWorkUnitAsync` now passes `fileScope` into `CreateBranchAsync`
so the work unit's declared file scope reaches `FileSystemWorkspaceService`.

**Scoped materialization path (in `FileSystemWorkspaceService.InitBranchAsync`):**
When `fileScope` is non-null AND snapshot + materializer are available:
1. Strips trailing glob segments (`"src/Auth/**"` → `"src/Auth"`) via `ExpandScopeForMaterializer`
2. Calls `materializer.MaterializeAsync(snapshot, branchDir, materializationScope, ct)` — only matching tree entries are written
3. Falls back to `CopyDirectory` (seed copy) if CAS/snapshot unavailable

**Callers that use null fileScope (full materialization — unchanged behavior):**
- Merge branches (`InMemoryMergeService`, `MergeReconciliationService`) — always full copy
- Known-good snapshot branches (`InMemoryKnownGoodStateService`) — always full copy
- Composite branches (`WorkspaceExecutionService`) — always full copy
- Candidate branch, "main" branch — unchanged

**Design decision — Roslyn dependency closure deferred:**
Roslyn closure requires materialized files before you know what to materialize (chicken-and-egg).
Practical approach: glob-prefix scope from `WorkUnit.FileScope` is sufficient for Phase 11.
Full dependency analysis is a Phase 11+ enhancement. Project structure files (`.csproj`, etc.)
are always injected so `WorkspaceProfileService` can still detect project roots.

**Files changed:**
- `NodalMerge.Studio.Core/Services/ServiceContracts.cs` — `IFileWorkspaceService.InitBranchAsync` + `IBranchService.CreateBranchAsync` signatures
- `NodalMerge.Studio.Storage/FileSystemWorkspaceService.cs` — scoped materialization path + `ExpandScopeForMaterializer`
- `NodalMerge.Studio.Storage/InMemoryBranchService.cs` + `NodalMergeBranchService.cs` — pass through
- `NodalMerge.Studio.Orchestrator/InMemoryWorkUnitService.cs` — pass `fileScope` to `CreateBranchAsync`
- All other callers — named `ct:` / `cancellationToken:` argument to preserve default behavior

**Phase 11 addendum — On-Demand File Materialization:**

`IFileWorkspaceService.MaterializeFileAsync(branchId, path, ct)` → `Task<bool>` fetches a single
path from the latest snapshot into the branch dir. Returns `false` when the path doesn't exist in
the snapshot (file is genuinely absent). Implemented in `FileSystemWorkspaceService` by checking
`snapshot.TreeEntries[path]` and calling `materializer.MaterializeAsync(snapshot, branchDir, [path])`.

`McpToolDispatcher.WorkspaceReadAsync` transparently tries materialization when `ReadAsync` returns
null — if it materializes, the agent gets the content; if not, the error is explicit:
*"File does not exist in this branch or in the repository snapshot. If this is a new file, use
nm_v1_workspace_write to create it."* This prevents agents from hallucinating files that don't exist.
`WorkspaceReadManyAsync` applies the same batch fetch for any `Found=false` slots.

REST endpoint: `POST /studio/branches/{branchId}/materialize-file?path=` for external/debug use.

---

### Phase 11.5 — Co-Modification Intelligence ✅ COMPLETE

Mines the RepositoryOp log for pairwise co-modification frequency and routes the signal into
the projection payload (agent hints) and the finding detector pipeline.

**Implementation (actual):**

`CoModificationPattern` record in `Contracts/Domain/` (exact as planned). Stored as `CoModPatternV1`
nodes. `InMemoryCoModService` computes pairwise frequency by grouping ops by `WorkUnitId`,
collecting unique paths per WU, then counting co-occurrences. Confidence = count / totalWUsScanned.
Deterministic PatternId so recomputes overwrite rather than duplicate. Rehydratable from node store.

**`ICoModService`** (3 methods):
- `ComputeAsync(repositoryId)` — recomputes + persists all patterns for the repo
- `GetAsync(repositoryId)` — returns last-computed patterns without recomputing
- `GetForPathsAsync(repositoryId, prefixes, minConfidence)` — prefix-match against PathA/PathB

**Integration 1 — Projection hints:**
`CoModHint(PathA, PathB, Confidence)` added to `ProjectionContracts.cs`. `AgentWorkspaceProjectionPayload`
gains `IReadOnlyList<CoModHint>? CoModHints = null`. `ProjectionManager` injects `ICoModService?`
and populates `CoModHints` for WUs with non-empty FileScope at confidence ≥ 0.6. FileScope glob
patterns are expanded to directory prefixes via `ExpandGlobsToPathPrefixes` before the lookup.
Agents receive the hints as JSON fields in their AgentWorkspace projection response.

**Integration 2 — Finding detectors (both in `FindingDetectorService.DetectCoModPatternsAsync`):**
- **Boundary violation** (`confidence ≥ 0.4`, `count ≥ 3`): flags pairs where PathA and PathB
  map to different architectural layers (prefix heuristic: ui/web/frontend/domain/core/data/infra/api).
  Emits `KnowledgeGuideline` finding.
- **Co-mod miss** (`confidence ≥ 0.6`): for each completed WU with FileScope, finds high-confidence
  partners NOT in scope. Emits `PromptImprovement` targeting `PipelineStage.Plan`.

Both are included in `POST /studio/insights/detect-findings` response automatically.

**New REST endpoints:**
- `POST /studio/comod/compute` — triggers full recompute for the configured repository
- `GET /studio/comod?minConfidence=` — returns current pattern set

**Files changed:**
- `Contracts/Domain/CoModificationPattern.cs` — new record
- `Contracts/Projections/ProjectionContracts.cs` — `CoModHint` record + `CoModHints` field
- `Core/Services/ServiceContracts.cs` — `ICoModService` interface
- `Storage/StudioNodeStore.cs` — `CoModPatternV1` constant
- `Storage/InMemoryCoModService.cs` — new implementation
- `Storage/ServiceCollectionExtensions.cs` — registration
- `Projections/ProjectionManager.cs` — `ICoModService?` param + hint population
- `Host/FindingDetectorService.cs` — `DetectCoModPatternsAsync` + ctor params
- `Host/StudioRestEndpoints.cs` — comod endpoints + detect-findings includes comod

---

### Phase 11.75 — Blake3 CAS Hash Alignment ✅ COMPLETE

**Problem:** nodalmerge-studio was computing blob IDs with SHA256 while the host engine
(`nodalmerge_core`) computes them with Blake3 (32 bytes, 64-char lowercase hex). The host's
`IBlobStoreProvider` uses the caller-supplied hash string as the file key — it does not
re-verify the content. Any blob written by the studio under a SHA256 ID would be invisible to
the engine looking for the Blake3 ID of the same content, and vice versa.

**Investigation confirmed:**
- `core/src/hash.rs`: `blake3::hash(data).to_hex()` — 64-char lowercase hex
- `FileBlobStoreProvider.cs`: stores as `{root}/{first2}/{fullHash}.blob` — opaque key
- `is_hex64()` validator enforces exactly 64 hex chars — compatible with Blake3 output
- SHA256 in `RuntimeDagPersistenceService.cs` is node-pack deduplication only — not blob IDs

**NuGet package added:** `Blake3` 2.2.1 (xoofx) → `NodalMerge.Studio.Storage.csproj`

**New helper** `Storage/BlobHasher.cs`:
```csharp
public static string ComputeHash(byte[] bytes) => Hasher.Hash(bytes).ToString();
public static string ComputeHash(ReadOnlySpan<byte> bytes) => Hasher.Hash(bytes).ToString();
```

**Call sites switched to Blake3 (cross-boundary blob IDs):**
- `FileSystemWorkspaceService.BlobId(bytes)` — blob written when agent edits a workspace file
- `MaterializationEngine.FileMatchesBlob(path, blobId)` — skip-if-matches check during materialization
- `RepositoryImportService.BlobId(bytes)` — blob IDs assigned during git import
- `ConflictResolutionService.CommitMergeAsync` — blob ID for merged content (uses `BlobHasher` via Storage project reference)

**SHA256 intentionally kept for:**
- `FileSystemWorkspaceService.ComputeFingerprint()` — internal workspace diff structural fingerprint (16-char prefix, not a blob ID, not cross-boundary)
- `InMemoryRepositorySnapshotService` snapshot IDs — internal studio-only identifiers
- `DocFetchCommandService` content checksum — document protocol header, not blob CAS

---

### Phase 12 — Tool Virtualization ✅ COMPLETE

Four new MCP tools added to `McpToolDispatcher` (additive alongside filesystem-backed tools):

| Tool | Inputs | Behavior |
|------|--------|----------|
| `nm_v1_repository_blob_list` | `repositoryId`, `scope?` | Lists paths from latest snapshot's TreeEntries, filtered by prefix |
| `nm_v1_repository_blob_read` | `repositoryId`, `path` | Resolves blobId from snapshot → `TryGetBlobAsync` → returns UTF-8 content |
| `nm_v1_repository_blob_write` | `repositoryId`, `path`, `content`, `reason?`, `workUnitId?` | Blake3-hashes content → `PutBlobAsync` → emits `RepositoryOperation` (Add or Replace) |
| `nm_v1_repository_blob_search` | `repositoryId`, `query`, `scope?`, `maxResults?` | O(n blobs) full-text scan across snapshot entries; returns `{path, lineNumber, snippet}` matches |

**Services added to `McpToolDispatcher` constructor (all optional/nullable):**
- `IRepositorySnapshotService? repoSnapshots` — for latest snapshot lookup
- `IBlobStoreProvider? repoBlobStore` — for blob read/write
- `IRepositoryOpService? repoOpEmitter` — for emitting RepositoryOperation on blob_write

All tools return a helpful error if the required service isn't configured (graceful degradation
when CAS is not wired up). Tools are added to `McpToolNames.All` for profile allowlist use.

**Existing tools unchanged:** `nm_v1_workspace_*` and `nm_v1_repository_read_file` /
`nm_v1_repository_list_files` (filesystem-backed via `IRepositoryRegistryService`) remain as
the compatibility path. Agents on older profiles are unaffected.

**`FileLeaseService` + blob tools:** No lease is acquired for `nm_v1_repository_blob_write`.
`NonOverlappingFileScopeRule` handles planning-time scope protection. Agents using
repository-native tools operate entirely through the op log, which is already the authoritative
write path; the lease system (designed for filesystem-level lock coordination) is orthogonal.

**Future enhancement:** `nm_v1_repository_blob_search` is O(n) over all blobs. A future
phase can add an inverted index over snapshot content for sub-millisecond search.

---

### Phase 13 — Remote Repository Rooms ✅ COMPLETE (host-configuration-only)

No Studio code changes required. The full S3 backend already exists:

- **Rust:** `nodalmerge-s3-blobs` crate implements `BlobPersistence` against any S3-compatible
  store (AWS, Cloudflare R2, MinIO, GCS).
- **C#:** `S3DelegatedBlobUrlResolverProvider` in `NodalMerge.Host.Composition` handles
  presigned URL generation. Host `ServiceCollectionExtensions` switches backend based on
  `BlobStorageProvider` config string: `"File"` / `"WsOnly"` / `"S3Delegated"`.
- **Studio:** `WorkspaceOptions.CasRootPath` already exists for local path override. Studio
  consumes whatever `IBlobStoreProvider` the host registers — no Studio-side switch needed.

**Cross-repository deduplication** falls out of content-addressing automatically: two
repositories containing the same file share a single blob in CAS regardless of which
repository wrote it first. No additional work required.

**To enable S3:** configure `NodalMerge:BlobStorageProvider = "S3Delegated"` and the
corresponding S3 settings in the host's `appsettings.json`. Studio picks it up at next startup.

---

### Phase 14 — Git as Import/Export Adapter ✅ COMPLETE

**NuGet added:** `LibGit2Sharp` 0.31.0 → `NodalMerge.Studio.Storage.csproj`

**`IGitAdapter` interface** (in `Core/Services/ServiceContracts.cs`):
```csharp
Task<string> ImportAsync(string gitRepoPath, string? commitSha, string repositoryId, ct);
Task<GitExportResult> ExportAsync(string repositoryId, string? snapshotId, string targetGitRepoPath, string branchName, ct);
```

**`GitExportResult` record:** `(RepositoryId, SnapshotId, TargetPath, BranchName, Committed, CommitSha?, Message?)`

**`GitAdapter` implementation** (in `Storage/GitAdapter.cs`):
- **Import:** Opens the git repo with `LibGit2Sharp.Repository`, resolves the commit (or HEAD),
  recursively walks `FlattenTree(commit.Tree)` → `(path, blob)` pairs, writes each blob to CAS
  via `BlobHasher.ComputeHash` + `PutBlobAsync`, emits `RepositoryOperation(Kind=Import)` for
  each file, then calls `IRepositorySnapshotService.CreateAsync` with the `path→blobId` map.
  Returns the new `SnapshotId`. Import is always read-only relative to the git object store.
- **Export (gated):** Materializes the snapshot directly into `targetGitRepoPath` via
  `IMaterializationEngine`. Whether a git commit is created depends on
  `WorkspaceOptions.AllowAgentGitCommits` (default: `false`). When false, `Committed = false`
  and `CommitSha = null` — files are on disk for the user/CI to commit. When true, stages all
  and commits via LibGit2Sharp, returning the new SHA.

**Git commit/push gating** (`WorkspaceOptions`):
- `AllowAgentGitCommits` (default `false`) — opt-in for agent-created git commits. Dangerous;
  only enable for headless CI pipelines with deliberate oversight.
- `AllowAgentGitPush` (default `false`) — stub for future push support; shape is stable now.
- Both are surfaced in `GET`/`POST /studio/options`.

**`IRepositorySnapshotService.GetAsync(snapshotId, ct)`** added. Backed by a `_byId` dictionary in
`InMemoryRepositorySnapshotService` populated at `RehydrateAsync` and `CreateAsync`.

**DI:** `GitAdapter` registered in `AddStudioStorage()` with `WorkspaceOptions` and optional
`IBlobStoreProvider` / `IMaterializationEngine`.

**REST endpoints:**
- `POST /studio/repositories/{repositoryId}/git/import` — body: `{ gitRepoPath, commitSha? }`
- `POST /studio/repositories/{repositoryId}/git/export` — body: `{ targetGitRepoPath, branchName, snapshotId? }`

**MCP HTTP server tools** (in `McpServer/Tools/GitAdapterTools.cs`):
- `nm_v1_repository_git_import` — calls `ImportAsync`, available to external harnesses
- `nm_v1_repository_git_export` — calls `ExportAsync`, reflects committed/not-committed result

Git is now equivalent to JSON or CSV — an interchange format, not the authoritative store.

---

## Open Questions

1. **Git object store as CAS?** Git already has a content-addressed object store at `.git/objects/`.
   Using it directly would eliminate the `.nodalmerge/cas/` directory, but it uses SHA-1/SHA-256
   (not Blake3), couples the CAS to git internals, and makes the "git as adapter only" end state
   harder to reach. The host's `FileBlobStoreProvider` already implements the right layout.
   Recommendation: `.nodalmerge/cas/` via `FileBlobStoreProvider`; git stays adapter-only.

2. **Snapshot generation for branches.** When a work unit forks from another (fan-out), the forked
   work unit's `BaseSnapshotId` is the parent's latest snapshot. Two sibling work units can thus have
   divergent snapshot chains from the same base. The conflict engine handles this; the open question is
   whether sibling snapshot chains need their own `Generation` counters or share the repository-global one.
   Recommendation: share the global counter; generation is just a tiebreaker, not a branch identifier.

3. **Binary files.** CAS stores raw bytes regardless of encoding. Binary files (images, compiled assets,
   PDFs) are stored correctly. The diff display layer should skip diffs for binary blobs (no `OldBlobId →
   NewBlobId` text diff possible). The materializer is unaffected.

---

## What This Is Not

- This is not a Git replacement. Git remains the canonical version control system for the project.
  Repository virtualization operates on the NodalMerge side of the git boundary.
- This is not about replacing the CRDT engine. The repository room uses the same CRDT infrastructure
  as the studio room. The CRDT handles replication, conflict ordering, and consensus.
- This is not a build system. The materializer produces a working directory; what you do with it
  (build, test, run) is unchanged.
