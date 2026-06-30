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
