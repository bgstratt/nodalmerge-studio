# Repository Virtualization

Each work unit branch gets a physically isolated working directory. Agents write files, build, and
test in their own sandbox — there is no shared working directory and no file-lock contention between
concurrent agents.

---

## How it works

The workspace root is configured via `Workspace:RootPath` in `appsettings.json`. For every branch,
the Studio derives a directory path by sanitizing the branch ID — any character that is invalid in
a filesystem path (including `/`, the conventional branch separator) is replaced with `_`:

```
{Workspace:RootPath}/{sanitized-branchId}
```

Examples:

| Branch ID | Directory |
|---|---|
| `main` | `{RootPath}/main` |
| `feature/auth` | `{RootPath}/feature_auth` |
| `work-abc123` | `{RootPath}/work-abc123` |

To get the effective path for any branch programmatically:

- **REST:** `GET /studio/workspace/path?branchId=<branch>`
- **MCP:** `nm_v1_workspace_path` with `{ "branchId": "..." }`

Response: `{ "branchId": "...", "workingDirectory": "C:\\...\\feature_auth", "exists": true }`

---

## Branch seeding

When a new branch directory is initialized, Studio tries three strategies in order:

1. **Scoped CAS materialization** — if the work unit declares `FileScope` glob patterns and a CAS
   snapshot exists, only the matching paths (plus project structure files such as `.csproj` and
   `package.json`) are extracted from the content-addressable store. This keeps branch directories
   small when agents only need a subset of the repository.

2. **Seed from another branch** — if `seedFromBranchId` is specified on work-unit create, the
   source branch directory is copied as-is.

3. **Main-branch reconstruction** — for the `main` branch specifically, Studio reconstructs from
   `Workspace:SeedRepositoryPath` via CAS snapshot or a full directory copy.

---

## Keeping the CAS snapshot current

The CAS snapshot (`RepositorySnapshot` — a flat path→blobId map, plus an `Add`/`Replace`/`Delete`
op-log) is a best-effort audit/reconstruction trail derived from the seed repository's on-disk
content — it is **not** the source of truth during a run; the per-branch working directory is.
Two things advance it:

- **Bootstrap** (`RepositoryImportService.EnsureBootstrappedAsync`) — walks every file once and
  records a Generation-0 snapshot. Fires automatically the first time a goal is created against a
  repository, then intentionally does nothing on any later `GoalCreation`/`StartupRecovery` call
  for that same repository in the same process — a one-time seed is all those need.
- **Forced resync** (`RepositoryImportService.ForceSyncAsync`) — re-diffs the repository's current
  disk content against the last snapshot and records a successor snapshot if anything changed,
  regardless of whether it was already bootstrapped. This is what keeps the snapshot from going
  permanently stale after the first goal. It fires:
  - Automatically, right after `InMemoryMergeService.ApplyAsync` writes a merge's changes back to
    the repository (`SyncTrigger.PostMergeWriteBack`) — scoped specifically to the global default
    repository (`Workspace:SeedRepositoryPath`); a multi-repo work unit's own registered repository
    does not currently trigger an automatic resync this way.
  - On-demand via `POST /studio/workspace/switch` or the `nm_v1_workspace_switch` MCP tool
    (`SyncTrigger.ManualRefresh`), whether or not the path actually changed.

A resync never touches an already-materialized file in any branch directory — `InitBranchAsync`
no-ops the instant a branch directory is non-empty, and the only other snapshot-consuming read path
(on-demand `FileScope` fallback fetch, below) only ever fires for a file that branch has never
touched before. So a live resync cannot disturb a running agent's own in-progress work; the one
observable effect is that a scoped branch's first-ever fetch of a not-yet-materialized file may see
fresher content than it would have before the resync ran.

---

## Scoped materialization (Phase 11)

When a work unit's `FileScope` property contains one or more glob patterns, the materializer
extracts only files matching those patterns from the CAS blob store. Project-structure files
(`.csproj`, `package.json`, `Cargo.toml`, `go.mod`, etc.) are always included regardless of scope
so that build tools can resolve the project graph.

This is the primary mechanism for keeping short-lived worker branches lean. An agent working on
`src/Auth/**` does not need to materialize the entire repository.

---

## Concurrency model

Each work unit has its own branch and therefore its own directory. The CAS blob store (under
`Workspace:CasRootPath`, defaulting to `{SeedRepositoryPath}/.nodalmerge/cas`) is the shared
read-only layer. Per-branch directories are the write-isolated layer — parallel agents never
compete for file locks.

`Workspace:MaterializerConcurrency` controls the number of parallel I/O threads used during
CAS reconstruction (default: 4). Increase this on machines with fast NVMe storage and many
concurrent agents.

---

## Branch directory cleanup (`WorkspaceCacheManager`)

Branch working directories are treated as ephemeral cache entries — any evicted directory can be
reconstructed later from the latest repository snapshot + CAS. `WorkspaceCacheManager` runs a
best-effort orphan sweep automatically at host startup (`Completed`/`Merged`/`Cancelled` work
units only), and exposes REST endpoints for manual control:

| Endpoint | Purpose |
|---|---|
| `POST /studio/cache/evict?workUnitId=...` | Delete one work unit's branch directory |
| `POST /studio/cache/materialize?workUnitId=...` | Rebuild an evicted directory from the latest snapshot + CAS |
| `POST /studio/cache/evict/orphaned` | Run the orphan sweep on demand |
| `POST /studio/cache/gc?dryRun=...` | Report (or perform) CAS blob garbage collection |

A `Cancelled` work unit is always safe to evict — its changes were never merged, so there's nothing
to preserve. A `Completed`/`Merged` work unit is only evicted once the repository's own snapshot
postdates the work unit's last update — i.e., once a resync (see above) has actually captured that
work unit's contribution, so reconstructing later won't lose anything. For a multi-repo work unit,
this check resolves the work unit's own registered repository (via `RepositoryId`) rather than
always the global default, so eviction/rematerialization checks the right repository's snapshot.

This is currently REST-only — no VS Code UI panel or MCP tool surfaces branch-directory count, disk
usage, or manual evict/materialize/gc today; it is intentionally automatic-only for typical use.

---

## Observability

If `Workspace:SeedRepositoryPath`, the CAS blob store, or the repository op-log service aren't all
configured together, the CAS dual-write (blob + op-log write, alongside every file write/delete in
a branch directory) is silently skipped — this is a legitimate, common, intentional deployment
choice, not a misconfiguration (plenty of setups don't need the audit trail at all). The first time
this happens, an `Information`-level log line is written naming which of the three pieces is
missing; it is not repeated on every subsequent file operation, and stops appearing entirely once
configuration is completed at runtime (e.g. via `POST /studio/workspace/switch`).

---

## Git integration

Two `WorkspaceOptions` flags control whether the Studio acts on the filesystem changes agents
produce:

| Flag | Default | Behavior when `true` |
|---|---|---|
| `AllowAgentGitCommits` | `false` | After a proposal is approved and applied, files are materialized to disk and committed via `git commit`. |
| `AllowAgentGitPush` | `false` | After committing, the branch is pushed via `git push origin {branchName}`. Requires `AllowAgentGitCommits=true`. |

Both are intended for headless CI/CD pipelines. See
[docs/guides/headless-peer.md](headless-peer.md) for the typical configuration pattern.

---

## Configuration reference

All keys live under the `Workspace` section in `appsettings.json`:

| Key | Default | Description |
|---|---|---|
| `Workspace:RootPath` | System temp | Parent directory for all branch working directories |
| `Workspace:SeedRepositoryPath` | *(required)* | Source repository used to seed the `main` branch directory |
| `Workspace:CasRootPath` | `{SeedRepositoryPath}/.nodalmerge/cas` | Content-addressable blob store root |
| `Workspace:MaterializerConcurrency` | `4` | Parallel I/O threads for CAS reconstruction |
| `Workspace:AllowAgentGitCommits` | `false` | Commit materialized files after proposal apply |
| `Workspace:AllowAgentGitPush` | `false` | Push the branch after committing |
