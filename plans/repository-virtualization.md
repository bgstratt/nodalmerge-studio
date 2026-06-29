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

### BlobIndex

```
BlobHash         string
SizeBytes        long
ReferenceCount   int
RepositoryIds    string[]
SnapshotIds      string[]
WorkUnitIds      string[]
LastAccessedAt   DateTimeOffset
Pinned           bool      // never evict (approved/merged proposals set this)
```

The CAS intentionally knows nothing beyond hash → bytes. The BlobIndex is the reference-tracking
layer that makes safe garbage collection possible. It lives in the studio node store alongside
other entity metadata.

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

### Milestone 1 — Record (non-breaking)

Introduce the CAS and RepositoryOp infrastructure. All existing file writes continue to work.
Nothing breaks. The system begins building an immutable history as a side effect of normal operation.

**Phases:**
- [Phase 0](#phase-0--model-and-bounded-context) — Define the Repository bounded context
- [Phase 1](#phase-1--content-addressable-storage) — CAS abstraction + local implementation
- [Phase 3](#phase-3--repository-operations) — RepositoryOperation nodes
- [Phase 4](#phase-4--filesystem-compatibility-layer) — Dual-write (file + op)
- [Phase 4.5](#phase-45--op-history-in-agent-projections) — Op history surfaced in `AgentWorkspaceProjectionPayload`
- [Phase 5](#phase-5--user-workspace-import) — User edits become imported RepositoryOps

**Exit criteria:** Every file write produces a RepositoryOp node. The full op log is replayable.
An agent assigned to a file scope receives the recent op history for those paths — who changed
each file, in which work unit, and why — as part of its standard projection payload.
Workspace directories can still be deleted manually without consequence to correctness.

---

### Milestone 2 — Reconstruct

Introduce snapshots and the materializer. Workspace directories become cache entries that can be
safely evicted and rebuilt on demand. The BlobIndex enables safe garbage collection.

**Phases:**
- [Phase 2](#phase-2--repository-snapshot-model) — Snapshot nodes + TreeHash
- [Phase 6](#phase-6--snapshot-compaction) — Compaction (event-sourcing pattern)
- [Phase 7](#phase-7--materialization-engine) — Materializer (snapshots + CAS → filesystem)
- [Phase 8](#phase-8--workspace-cache-manager) — Eviction policies + BlobIndex GC

**Exit criteria:** `rm -rf` on any workspace directory is safe at any time. Materializer
reconstructs it from snapshot + CAS. Disk accumulation problem is solved.

---

### Milestone 3 — Virtualize

Workspace directories stop being the source of truth. Conflicts are structural. Materialization
is scoped to the agent's owned paths.

**Phases:**
- [Phase 9](#phase-9--conflict-engine) — Structural conflict detection via blob ancestry
- [Phase 10](#phase-10--intelligent-merge) — Merge strategies as RepositoryOps
- [Phase 11](#phase-11--projection-aware-materialization) — Partial materialization by file scope
- [Phase 11.5](#phase-115--co-modification-intelligence) — Co-modification patterns as planner hints and Findings

**Exit criteria:** Two agents writing to overlapping paths produce a detectable DAG fork at
emit time, not at merge time. `FileLeaseService` is deprecated. Agents never materialize more
than their declared scope. The planner receives co-modification hints derived from the
accumulated op log, and the Finding system surfaces anomalous patterns.

---

### Milestone 4 — Decouple

CAS is pluggable. Agents can operate without a materialized filesystem. Git is an adapter.

**Phases:**
- [Phase 12](#phase-12--tool-virtualization) — Repository-aware agent tools without filesystem dependency
- [Phase 13](#phase-13--remote-repository-rooms) — CAS backends (S3, Azure Blob, cloud cache)
- [Phase 14](#phase-14--git-as-importexport-adapter) — Git becomes boundary adapter only

**Exit criteria:** A worker agent can read and write files entirely through CAS-backed tools
without a materialized directory. Git is used only on import and export.

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

This phase handles two distinct cases: **initial bootstrap** (first ever goal run for this
repository) and **between-run sync** (user edits made directly to the repo between goal runs).
Both paths run at the same trigger point — goal start — before any agent is assigned a branch.

**Case 1 — Bootstrap (no prior `RepositorySnapshot` exists for this repository):**

1. Walk every non-ignored file in `SeedRepositoryPath`
2. Write each file's content to CAS via `IBlobStoreProvider.PutBlobAsync(Blake3(content), ...)`
3. Emit a `RepositoryOp` of type `Import` for each file (`OldBlobId = null`, `NewBlobId = hash`)
4. Record a `GitImportSnapshot` node with `BaseSnapshotId = null`, `Generation = 0`, and
   `GitCommit` set to the current HEAD (if available). This is the root of the entire op lineage.

This runs once per repository, the first time `InitBranchAsync` is called. Subsequent goal runs
skip to Case 2.

**Case 2 — Between-run sync (snapshot exists, user may have edited files directly):**

`RepositorySyncService` (currently diffs filesystem against repo for sync-state tracking) evolves
to compare each file in `SeedRepositoryPath` against its expected `blobId` in the latest
`RepositorySnapshot`:

1. Files whose content hash matches the snapshot: skip
2. Files added since the snapshot: emit `Add` op, write blob to CAS
3. Files modified since the snapshot: emit `Replace` op (`OldBlobId = snapshot's hash`), write new blob
4. Files deleted since the snapshot: emit `Delete` op (`NewBlobId = null`)
5. After all ops: emit a new `RepositorySnapshot` tagged `source: ImportedFromFilesystem`

Agents only start after this sync completes. The diff their projection shows is relative to this
freshly-created snapshot, not the stale one.

**Watcher integration (longer term):** A filesystem watcher on `SeedRepositoryPath` emits ops
as the user saves files, rather than only at goal start. The snapshot advances incrementally.
This is the point at which the user's filesystem is truly just another client of the DAG.

---

### Phase 6 — Snapshot Compaction

Replaying 50,000 ops from the git import baseline is impractical. Snapshots are the compaction
boundary — the same pattern as event-sourcing's checkpoint.

**Policy:** Emit a snapshot after every N ops (configurable; default 500) or after every work
unit that produces at least one op. Neither is exact — the trigger is "ops since last snapshot
exceeds threshold at work unit completion."

**Replay from snapshot:** Load the snapshot's TreeHash (the `path → blobId` map is stored in
the snapshot node), then apply only the ops since that snapshot. Fast startup; bounded replay.

**Retention:** Snapshots corresponding to approved or merged proposals are pinned
(`BlobIndex.Pinned = true`). Intermediate snapshots are eligible for compaction once a newer
snapshot in the same lineage exists and the intermediate one is not pinned.

---

### Phase 7 — Materialization Engine

```
IMaterializationEngine

MaterializeAsync(snapshotId, targetPath, fileScope?)
RematerializeAsync(snapshotId, previousSnapshotId, targetPath, fileScope?)
```

**Algorithm:**
1. Load snapshot's `path → blobId` map
2. If `fileScope` provided, filter to matching paths only
3. For each path: check if file at `targetPath` already matches `blobId` (skip if so)
4. For mismatched/missing files: fetch blob via `IBlobStoreProvider.TryGetBlobAsync`, write to `targetPath`
5. Delete files in `targetPath` not present in the snapshot (and in scope)

**Optimizations (in order of impact):**
- Skip files already matching the expected blob hash (cheap hash check vs. full read)
- Parallel blob reads (bounded concurrency; CAS reads are independent)
- Hardlinks or reflinks where the OS supports them (Windows: reflinks on ReFS, symlinks as fallback)
- `RematerializeAsync` diffs two snapshots and only touches changed paths

---

### Phase 8 — Workspace Cache Manager

Workspace directories become cache entries. The `WorkspaceOptions.RootPath` directory is the
cache root. Nothing in it is authoritative.

**`IWorkspaceCacheManager`:**

```csharp
MaterializeAsync(workUnitId, snapshotId)
EvictAsync(workUnitId)
EvictAllIdleAsync(idleThreshold)
EvictOrphanedAsync()   // branch dirs with no matching node-store entry
```

**Eviction triggers:**
- Build/test complete → evict after configurable delay (default: evict immediately on success)
- Agent idle for N minutes (default: 15)
- LRU under disk pressure (BlobIndex.LastAccessedAt drives ordering)
- Manual: "Clean Up Workspaces" panel action
- Startup: scan for orphaned directories whose work unit is in terminal state

**Blob GC:** When a workspace is evicted, the BlobIndex `ReferenceCount` for every blob in its
snapshot decrements. When `ReferenceCount` reaches zero and `Pinned = false`, the blob becomes
a GC candidate. The host's `FileBlobGcCoordinator` (already implemented, with tombstone-based
grace window and `DryRun`/`LiveRun` modes) performs the actual deletion. Studio calls it with
the set of live hashes derived from the BlobIndex.

**Safe eviction invariant:** A workspace can be evicted if and only if its snapshot exists in
the node store. The materializer can always reconstruct it.

---

### Phase 9 — Conflict Engine

Conflicts are now detectable at op-emit time, not merge time.

**Definition:** Two RepositoryOps targeting the same `(repositoryId, path)` that share the same
`OldBlobId` but produce different `NewBlobId` values are a conflict. They fork from the same
parent blob.

**Detection in `RepositoryOpService.EmitAsync`:**
1. Resolve the current blob at `path` in the parent snapshot
2. If `op.OldBlobId != currentBlobId` → conflict; block or flag based on policy

**`NonOverlappingFileScopeRule` is deprecated** in this phase. Structural ancestry detection
is strictly more precise: it catches conflicts that scope declaration misses (two agents declare
non-overlapping scopes but happen to modify a shared utility file) and eliminates false positives
(two agents declare overlapping scope for a file neither actually touches).

**Conflict resolution as a DAG node:**

```
ConflictResolutionOp
  ConflictId
  RepositoryId
  Path
  OpIdA            // the two divergent ops
  OpIdB
  ResolutionBlobId // the merged result
  Strategy         // ThreeWay | Ast | LlmAssisted | Human
  ResolvedBy       // agentId or "human"
```

Resolution is just another RepositoryOp. Nothing special.

---

### Phase 10 — Intelligent Merge

Merge strategies are pluggable and all produce a `ConflictResolutionOp`:

```csharp
public interface IMergeStrategy
{
    string Name { get; }
    Task<MergeResult> MergeAsync(string blobIdA, string blobIdB, string blobIdBase,
        string path, CancellationToken ct);
}
```

**Implementations (in order of precedence):**

1. `ThreeWayMergeStrategy` — standard three-way merge using base blob. No LLM.
2. `AstMergeStrategy` — structure-aware merge for supported file types (C#, TypeScript).
   Uses Roslyn/TypeScript AST to merge at the declaration level, not line level.
3. `LlmAssistedMergeStrategy` — LLM receives both versions + base + surrounding context;
   produces merged content. Runs only when ThreeWay/AST fail.
4. `HumanReviewStrategy` — presents the conflict in the merge review panel and waits.

---

### Phase 11 — Projection-Aware Materialization

Instead of materializing the entire repository for each work unit, materialize only the paths
the work unit's file scope covers, plus their dependency closure.

**File scope → materialization scope:**

```
WorkUnit.FileScope: ["src/Auth/**"]
Dependency closure: ["src/Auth/**", "src/Shared/Extensions.cs"] // resolved by Roslyn
Materialized paths: only those two sets
```

The `WorkspaceProfileService` already computes sub-project roots and dependency information.
The materializer reads this to determine the minimum set of blobs required.

**Impact:** A work unit touching 8 files in `src/Auth/` materializes 8–20 files instead of
the entire 4,000-file repository. Disk impact per work unit drops to kilobytes for typical tasks.

---

### Phase 11.5 — Co-Modification Intelligence

By Milestone 3, the op log has accumulated across many work units and captures which files are
changed together. This phase mines that signal and routes it into two existing systems without
modifying their contracts.

**Co-modification pattern record:**

```csharp
// Stored in the node store as studio/comod-pattern/v1
public sealed record CoModificationPattern(
    string PatternId,
    string RepositoryId,
    string PathA,
    string PathB,
    int CoModificationCount,   // number of work units that touched both
    int TotalWorkUnitsScanned,
    double Confidence,         // CoModificationCount / TotalWorkUnitsScanned
    DateTimeOffset ComputedAt);
```

Patterns are recomputed periodically (not on every op write — batched, e.g. nightly or on
demand from the Insights tab). The computation is a simple pairwise frequency analysis over
`RepositoryOp` nodes grouped by `WorkUnitId`.

**Integration 1 — Planner projection hints:**

Add `IReadOnlyList<CoModPattern>? CoModHints` to `AgentWorkspaceProjectionPayload` (same
additive pattern as Phase 4.5's `RecentFileOps`). When a planner assigns file scope to a child
work unit, the projection includes pairs that historically co-appear with scope files above a
confidence threshold (default 0.6).

Prompt surface:
```
## Likely related files (based on past work patterns)
When modifying src/Auth/UserService.cs, these files were co-modified in 73% of past work units:
- src/Auth/TokenService.cs
- src/Auth/AuthMiddleware.cs
Consider whether your changes require updates there too.
```

**Integration 2 — Finding detectors:**

Two new `IFindingDetector` implementations registered in the existing `FindingService` pipeline:

- **`CoModificationMissDetector`** — after a work unit completes, checks if any high-confidence
  co-modification partner was not touched. Emits a `Finding` of type `PromptImprovement` with
  the suggestion text. Human can Promote (adds to planner context) or Dismiss.

- **`BoundaryViolationDetector`** — emits a `Finding` when files in logically separate layers
  (e.g., UI and Domain) appear in the same co-modification cluster more than N times. Suggests
  that an architectural boundary may be eroding.

Both detectors implement the existing `IFindingDetector` interface; no changes to `FindingService`
or its review pipeline.

**No rework risk:** All changes are additive — new node kind, new optional projection fields, new
detector registrations. Nothing in Phase 4.5 or earlier phases needs modification.

---

### Phase 12 — Tool Virtualization

Agent tools evolve to operate against CAS directly, without requiring a materialized filesystem:

```
nm_v1_repository_read_blob(path)   → resolves blobId from current snapshot, returns content
nm_v1_repository_apply_op(...)     → emits RepositoryOp, updates in-memory projection
nm_v1_repository_search(query)     → full-text search over current snapshot's blobs
nm_v1_repository_list(scope)       → lists paths in scope from snapshot's path→blob map
```

The existing `nm_v1_workspace_write` etc. remain as the compatibility path for agents that
still expect a filesystem. New profiles can opt into repository-native tools.

Materialization becomes optional, not mandatory. The "level 2 agent" described in the vision
(works in-memory, no filesystem) is achievable at this milestone.

---

### Phase 13 — Remote Repository Rooms

The CAS backend switches from local filesystem to cloud object store. The CRDT room for the
repository runs on a remote node. Distributed workers read from and write to the same
repository state.

**The S3 backend is already built in the host.** The `nodalmerge-s3-blobs` crate implements
`BlobPersistence` against any S3-compatible store (AWS, Cloudflare R2, MinIO, GCS). On the C#
side, `S3DelegatedBlobUrlResolverProvider` already exists in `NodalMerge.Host.Composition`.
Phase 13 is primarily a **configuration and Studio wiring** task — not a build task.

What Phase 13 adds on the Studio side:
- Surface the CAS backend choice in `WorkspaceOptions` / extension settings
- Pass the configured backend through to `IBlobStoreProvider` at startup
- Update `WorkspaceCacheManager` to pre-fetch only scope-relevant blobs when materializing from a remote store

**Content distribution:** Workers pre-fetch only the blobs they need (their materialization scope).
Blobs are immutable, so CDN caching works without invalidation.

---

### Phase 14 — Git as Import/Export Adapter

Git stops being a storage mechanism.

**`IGitAdapter`:**

```csharp
Task<GitImportSnapshot> ImportAsync(string gitRepoPath, string? commitSha, CancellationToken ct);
Task<string> ExportAsync(string snapshotId, string targetGitRepoPath, string branchName, CancellationToken ct);
```

**Import:** Walk the git tree at the given commit, write each blob to CAS, record RepositoryOps
of type `Import`, produce a `GitImportSnapshot` node.

**Export:** Reconstruct the workspace from a snapshot (via materializer), then `git add` + `git commit`.
The commit message includes the `SnapshotId` and `WorkUnitId` for traceability.

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
