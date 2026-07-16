# Multi-user smoke test (two laptops + one server)

Manual verification recipe for the Phase 6 slice 6.5 milestone
(`plans/cas-distribution-and-storage.md`): a real Rust room server plus two independent Studio
hosts, each standing in for one laptop. This is **not** part of the automated test suite
(`tests/NodalMerge.Studio.Integration.Tests/MultiUserMilestoneTests.cs` covers the in-CI .NET-only
topology) — it is the thing to actually run by hand before believing the feature works end to end,
and the thing to re-run after any change that touches `RoomPeerClient`, `NodalMergeStudioNodeStore`,
or the Part 1 cache-refresh coordinator.

Everything below runs on one machine (three terminals) — that already exercises real network
sockets, a real separate server process, and two fully independent Studio host processes, which is
the part of the topology that matters. Running host B on an actual second machine only changes the
IP address in `Room:HostUri`.

## Prerequisites

- The `nodalmerge` repo checked out as a sibling of `nodalmerge-studio` (`../nodalmerge` relative to
  this repo), on a branch that includes Phase 6's replicated-room protocol (S2.1b onward). Rust
  toolchain installed.
- `nodalmerge-studio` builds (`dotnet build` at the repo root) — the Studio host by default restores
  `NodalMerge.DotNetHost`/`NodalMerge.Host.Composition` from NuGet (`NodalMergeUseNuGetPackages=true`
  in `Directory.Build.props`), so no local repack of the `nodalmerge` repo is required unless you are
  actively iterating on that side too. If you are, see the `nodalmerge-local-dev-flow` notes for the
  repack scripts and pass `-p:NodalMergeUseNuGetPackages=false` to use your local checkout instead.
- A scratch directory for the shared "repository" both laptops will work against, e.g.
  `C:\temp\smoke-repo`, containing at least one real file agents/humans can edit.

## Step 1 — Start the Rust room server

```powershell
cd C:\Users\bgstr\source\repos\nodalmerge
cargo run -p nodalmerge-server -- --store C:\temp\smoke-server-store
```

- `--store <dir>` enables real SQLite+file persistence (omit it for a purely in-memory server —
  fine for a quick smoke, but a restart then loses everything, including the blob origin).
- Default bind address is `127.0.0.1:7878` (override with the `NODALMERGE_BIND_ADDR` env var, e.g.
  `$env:NODALMERGE_BIND_ADDR = "0.0.0.0:7878"` to accept connections from a second physical machine).
- Watch for `NodalMerge server listening on ws://<addr>/ws/<room> and http://<addr>/blobs/<hash>` —
  that log line confirms both the room WebSocket endpoint and the blob-origin HTTP endpoint are up.
- The S3-backed variant (`nodalmerge-server-s3`, same CLI shape, S3 bucket config via env vars) is
  the other supported blob backend — swap `-p nodalmerge-server` for `-p nodalmerge-server-s3` if
  you specifically need to smoke-test the S3 path; the room/replication behavior below is identical
  either way.

## Step 2 — Start Studio host "A" (laptop 1, owns the real repository)

```powershell
cd C:\Users\bgstr\source\repos\nodalmerge-studio
$env:NodalMerge__Providers__BlobStorage = "File"
$env:NodalMerge__Storage__Sqlite__DbPath = "C:\temp\smoke-hostA\nodes.db"
$env:NodalMerge__Storage__FileBlobs__RootPath = "C:\temp\smoke-hostA\blobs"
$env:Workspace__RootPath = "C:\temp\smoke-hostA\workspace"
$env:Workspace__SeedRepositoryPath = "C:\temp\smoke-repo"
$env:Room__HostUri = "ws://127.0.0.1:7878"
$env:Studio__Urls = "http://127.0.0.1:5080"
dotnet run --project src/NodalMerge.Studio.Host
```

`NodalMerge:Providers:BlobStorage` **must** be set explicitly to `"File"` — the default provider is
`"WsOnly"` (an in-memory, per-process dictionary; `NodalMergeHostProviderOptions.Defaults`), which
silently makes `NodalMerge:Storage:FileBlobs:RootPath` a no-op (the directory is never created).
`/studio/goals` still works and blobs are still servable over `/blobs/{hash}` either way (WsOnly is
a real, working `IBlobStoreProvider` for the process's own lifetime) — but content vanishes on
restart, which defeats the point of a persistence smoke. Caught by this recipe's own dry run: the
env var list above initially omitted this and host A's blob directory was never created (see this
guide's own execution notes in the slice's final report). Also note: `Program.cs`'s server-mode
branch reads its own listen address from **`Studio:Urls`**, not `ASPNETCORE_URLS`/`--urls` — those
are silently ignored (`app.Run(configuredUrl)` is called explicitly with a value read from config
key `Studio:Urls`, defaulting to `http://127.0.0.1:5080` if unset).

Host A is the embedded host the VS Code extension on "laptop 1" would connect to
(`nodalmerge.runtimeUri` empty/default = `http://127.0.0.1:5080`). `Room:HostUri` pointed at the
Rust server (not at host B directly) is what makes this the real multi-user topology — both Studio
hosts below are plain room *clients* of a neutral third server, unlike the in-CI test's
two-hosts-only simplification (see that test's own class comment for why that distinction matters:
a client pushing to a peer that is *also* the room's server hits a known gap this topology avoids).

Confirm it's up: `curl http://127.0.0.1:5080/studio/workunits` should return `[]`.

## Step 3 — Bootstrap the shared repository on host A

```powershell
curl -X POST http://127.0.0.1:5080/studio/goals `
  -H "Content-Type: application/json" `
  -d '{"goal":"seed repository","repositoryPath":"C:/temp/smoke-repo"}'
```

Use forward slashes in every JSON body below, even on Windows — `Path.GetFullPath` normalizes them
identically, and a literal `C:\temp\...` inside a curl `-d` argument reliably gets mangled by
argument re-quoting between PowerShell and `curl.exe` (`\U`/`\s`/etc. read as invalid JSON escapes)
long before it reaches the server. Caught by this recipe's own dry run.

This registers `C:\temp\smoke-repo` (auto-minting a workgroup repo id — it's the first registration
in a fresh workspace) and bootstraps its CAS (walks the directory, writes every file to host A's
blob store, records generation-0 `RepositorySnapshot`). Note the `repositoryId` in the response —
you'll need it for the next goal.

```powershell
curl http://127.0.0.1:5080/studio/repositories
```

Note the `workgroupRepoId` for the repo you just registered — host B needs it in Step 4.

## Step 4 — Start Studio host "B" (laptop 2, cold — no local clone, no local blobs)

```powershell
cd C:\Users\bgstr\source\repos\nodalmerge-studio
$env:NodalMerge__Storage__Sqlite__DbPath = "C:\temp\smoke-hostB\nodes.db"
$env:NodalMerge__Providers__BlobStorage = "ChainedRemote"
$env:NodalMerge__Storage__FileBlobs__RootPath = "C:\temp\smoke-hostB\blobs"
$env:NodalMerge__Storage__RemoteOrigin__BaseUrl = "http://127.0.0.1:5080"
$env:Workspace__RootPath = "C:\temp\smoke-hostB\workspace"
$env:Room__HostUri = "ws://127.0.0.1:7878"
$env:Studio__Urls = "http://127.0.0.1:5090"
dotnet run --project src/NodalMerge.Studio.Host
```

Notes on this configuration, since it differs from host A's in several deliberate ways:

- `NodalMerge:Storage:RemoteOrigin:BaseUrl` points at **host A's** HTTP address, not the Rust
  server's — the Rust server is the *room* (metadata/CRDT) relay; host A is still the blob origin
  for content it originally bootstrapped, because that's where the bytes actually live. A real
  deployment would instead point this at whichever origin the team actually designates (often the
  server itself, if blobs are pushed there too — Phase 4's delegated-S3 path is exactly that option).
- `Workspace:SeedRepositoryPath` is deliberately **left unset** on host B — it has no local clone of
  the shared repository at all, and (as of slice 7.2) doesn't need one for the flow below to work.
  Before 7.2, this had to be forced to the *same literal path* host A used purely so
  `RepositorySnapshot`/materialize lookups (keyed by physical repo path, which has no portable
  identity across peers) would happen to match on this single-machine recipe — see "things that bit
  us" below for the fixed shape of that gap. `Workspace:CasRootPath` is likewise left unset here:
  it only ever mattered as a defense against `ApplyCasRootPath`'s `SeedRepositoryPath`-triggered
  redirect (see the notes section below), which no longer applies once `SeedRepositoryPath` is unset.

Confirm host B is up and standalone-cold: `curl http://127.0.0.1:5090/studio/workunits` should
return `[]`, and `curl http://127.0.0.1:5090/studio/repositories` should return `[]` too (host B
hasn't bound to anything yet).

## Step 5 — Bind host B to the shared repository, and watch the goal appear

```powershell
curl -X POST http://127.0.0.1:5090/studio/repositories `
  -H "Content-Type: application/json" `
  -d '{"path":"C:/temp/smoke-repo-b-placeholder","label":"shared repo (peer B)"}'
```

The path here does **not** need to exist on host B's disk (host B has no clone) — it's only a local
placeholder identity. Note the `repositoryId` this returns (host B's own local id — different from
host A's). Check its binding state:

```powershell
curl http://127.0.0.1:5090/studio/repositories/<repositoryId from above>/identity
```

This comes back with `pendingDisambiguation.candidates` listing host A's already-registered repo
(host B is connected to the room and sees it as a known candidate) rather than an already-bound
`workgroupRepoId` — confirming the workgroup repositories map itself replicated correctly before you
even touch the shared repo room. Resolve it onto host A's `workgroupRepoId` from Step 3:

```powershell
curl -X POST http://127.0.0.1:5090/studio/repositories/<repositoryId from above>/identity/resolve `
  -H "Content-Type: application/json" `
  -d '{"chosenRepoId":"<workgroupRepoId from Step 3>"}'
```

Now, back on **host A**, create the actual goal (after host B is bound — this is the "work appears
live" case, not startup catch-up):

```powershell
curl -X POST http://127.0.0.1:5080/studio/goals `
  -H "Content-Type: application/json" `
  -d '{"goal":"Update the README","repositoryId":"<repositoryId from Step 3>","fileScope":["README.md"]}'
```

**Observe:** within a few seconds (host B's room-membership reconcile loop runs every 5s —
in practice this was near-instant when this recipe was last executed),
`curl http://127.0.0.1:5090/studio/workunits` on host B lists the new work unit — through host B's
own REST surface (its in-memory `IWorkUnitService`, refreshed by Part 1's
`RehydratableRefreshCoordinator`), not by querying host A. Open the VS Code extension pointed at
host B (`nodalmerge.runtimeUri` = `http://127.0.0.1:5090`) and confirm the goal shows up in the
Activity Center without reloading the window.

**Cross-check the reverse direction too** (this is the one the in-CI automated test *can't* fully
exercise, since there host A plays both peer and room-server roles — see that test's own class
comment): create a goal directly on host B, scoped to its own local repository id from Step 5, and
confirm it appears on host A:

```powershell
curl -X POST http://127.0.0.1:5090/studio/goals `
  -H "Content-Type: application/json" `
  -d '{"goal":"Edit from peer B","repositoryId":"<host B''s own repositoryId from Step 5>","fileScope":["src/Foo.cs"]}'
curl http://127.0.0.1:5080/studio/workunits
```

When this recipe was last executed, this direction replicated correctly on the very first poll —
confirming the gap found in the two-Studio-hosts-only automated test is specific to a peer pushing
to a host that is *also* the room's server, and does not affect this recipe's real (separate-server)
topology.

## Step 6 — Cold-materialize a file on host B

```powershell
curl -X POST "http://127.0.0.1:5090/studio/branches/<branchId from Step 5's goal>/materialize-file?path=README.md"
```

**Observe:** `README.md` appears under
`C:\temp\smoke-hostB\workspace\branches\<branchId>\README.md` with host A's original content, and
`C:\temp\smoke-hostB\blobs` gains a cached blob file it didn't have a moment ago (the cold fetch
through the `ChainedRemote` provider against host A's `/blobs/{hash}` endpoint) — this now works with
**no** `Workspace:SeedRepositoryPath` configured on host B at all (slice 7.2): `FileSystemWorkspaceService`
resolves the branch's owning repository via its `WorkUnit.RepositoryId` and
`IRepositoryRegistryService.ResolveCasIdentityAsync`'s workgroup-portable identity chain instead of
requiring a local default clone. If the identity genuinely can't be bound anywhere on this peer
(e.g. host B never completed Step 5's disambiguation), this call now 404s with a message naming the
unresolved repository id and pointing at registration/disambiguation, not the old unhelpful "Path
does not exist in the latest repository snapshot" (see "things that bit us" below for the fixed
shape of this gap). In the real product, this materialize call happens automatically the moment an
agent or the extension opens the work unit's files (`WorkspaceCacheManager.MaterializeAsync` /
`IFileWorkspaceService.InitBranchAsync`) — spawning a real agent via `POST /studio/agents/spawn`
against the work unit exercises the same path with real LLM-driven edits if you want to go further.

## Step 7 — Edit, propose, and check replication back to host A

Edit the materialized file directly, then propose:

```powershell
curl -X POST http://127.0.0.1:5090/studio/workspace/write `
  -H "Content-Type: application/json" `
  -d '{"branchId":"<branchId>","path":"README.md","content":"# Smoke Repo\nedited on peer B\n"}'
curl -X POST http://127.0.0.1:5090/studio/merge `
  -H "Content-Type: application/json" `
  -d '{"sourceBranch":"<branchId>","targetBranch":"main","summary":"Update README from peer B","workUnitId":"<work unit id>"}'
curl http://127.0.0.1:5080/studio/merge/<proposalId from the response above>
```

**Observe:** because this smoke topology routes through the real Rust room server (both Studio
hosts are plain room *clients*), this should succeed — matching the direct goal-replication check in
Step 5. If it doesn't, re-run Step 5's reverse-direction check first to isolate whether the gap is
generic (affects every repo-scoped kind) or specific to `MergeProposal`'s denormalized `RepositoryId`
routing (`NodalMergeStudioNodeStore.TryResolveRepoRoomIdCoreAsync`) — and if it reproduces here, that
means the gap is broader than this slice's own testing found (which only ever saw it in the
embedded-host-as-server topology) — stop and report that precisely; do not patch around it in this
repo. (Verify the exact REST body shapes above against `StudioRestEndpoints.cs` — the workspace
write/merge-propose routes weren't re-derived as carefully as the goal/repository ones above while
writing this guide.)

## Notes / things that bit us building this

- **`Peer:RoomId`** (this peer's own default room, distinct from the workgroup/repo rooms) stays at
  its default (`"studio"`) throughout this recipe — nothing here needed it configured, but it is
  explicitly *not* wired through `Room:*`/the extension settings the way `HostUri`/`Workgroup` are
  (see Phase 6 slice 6.4's own scope note). If your smoke run does something that needs two
  *peer-local* "studio" rooms to stay distinct from each other, you will need to set this per host —
  it isn't discoverable from the extension UI today.
- `ApplyCasRootPath` (`StudioWebApplication.cs`) silently redirects `FileBlobs:RootPath` into
  `<SeedRepositoryPath>/.nodalmerge/cas` whenever `Workspace:SeedRepositoryPath` is set and
  `Workspace:CasRootPath` isn't — bit the automated test (a blob-cache-population assertion silently
  passed against the wrong directory) before `Workspace:CasRootPath` was pinned explicitly. Harmless
  here since host B doesn't set `SeedRepositoryPath`, but worth knowing if you extend this recipe.
- The extension's `nodalmerge.room.hostUri` / `nodalmerge.room.workgroup` settings are the UI-facing
  equivalent of `Room:HostUri` / `Room:Workgroup` used above — set them in each VS Code window's
  workspace settings (not user/global settings) so two windows on the same machine can point at
  different hosts/ports simultaneously.
- **[Fixed by slice 7.2]** Earlier revisions of this guide forced host B's `Workspace:SeedRepositoryPath`
  to the *same literal path* host A registered, purely so `RepositorySnapshot`/materialize lookups
  (keyed by `Path.GetFullPath(physical repo path)`, with no portable identity across peers) would
  happen to match — real second laptops obviously don't share a filesystem, so that was a
  single-machine-only accommodation, not something a genuine two-laptop deployment could rely on.
  Running the recipe with `SeedRepositoryPath` genuinely unset on host B (as it is above) used to
  reproduce the gap directly: Step 6's materialize-file call 404'd with the unhelpful "Path does not
  exist in the latest repository snapshot" even though the snapshot *was* correctly replicated into
  host B's bound repo room. Slice 7.2 (`plans/cas-distribution-and-storage.md` Phase 7) fixed the
  resolution layer: a repository's CAS/snapshot identity is now resolved via
  `IRepositoryRegistryService.ResolveCasIdentityAsync` — the workgroup-portable `WorkgroupRepoId`
  (this peer's own registry entry, or a foreign `RepositoryV1` row replicated via the shared
  "studio" room) for a brand-new repository, sticky to whatever key an already-bootstrapped
  repository's chain already uses otherwise (so no pre-7.2 workspace's history was rewritten or
  orphaned). `FileSystemWorkspaceService.MaterializeFileAsync`/`InitBranchAsync`'s scoped path now
  resolve a cold peer's owning repository via the branch's `WorkUnit.RepositoryId` instead of
  requiring a local default clone at all. An identity that genuinely can't be bound anywhere on a
  peer now surfaces `RepositoryIdentityUnresolvedException` (an identity-aware 404 naming the
  unresolved repository id and pointing at registration/disambiguation), not the old unhelpful
  path-not-found message.
