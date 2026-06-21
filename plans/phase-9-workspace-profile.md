# Phase 9 — Workspace Profile & Multi-Root Awareness

## Context

Two pain points reported in practice, both traced to the same root cause: nothing in the system
knows that a repo can contain more than one project.

1. **Prompting drifts away from existing code in multi-stack repos.** Asked to "update the
   compliment endpoint," the agent creates a new `.ts` file instead of editing the existing
   handler in the .NET host. `WorkerAgentLoop.cs:34-37` already tells the agent to check for a
   same-named file elsewhere in the tree before writing — that mitigation exists and helps the
   single-stack case. It does nothing for the cross-stack case: there's no signal anywhere that
   the repo *has* a dotnet host and a frontend as distinct roots, so the agent has no reason to
   suspect "endpoint" means a controller, not a route handler.
2. **Build/test/run breaks in multi-project repos**, confirmed by reading
   `WorkspaceExecutionService.cs` directly:
   - `.csproj`/`.slnx` detection is recursive (`SearchOption.AllDirectories`,
     `WorkspaceExecutionService.cs:123-124`) but `package.json` detection is **not**
     (`File.Exists(Path.Combine(workDir, "package.json"))`, line 130) — a repo with
     `frontend/package.json` and no root-level `package.json` never detects npm at all.
   - Even when a build system is detected, every command runs with
     `WorkingDirectory = workDir` — the *branch root* (`RunCommandAsync`, line 200). `dotnet
     build`/`npm run build` only work from the branch root if the project file happens to live
     there. A repo shaped `backend/Host.csproj` + `frontend/package.json` detects both (csproj
     recursively) but **both commands fail** because neither runs from the right directory.
   - "Run" (`WorkspaceExecutionCommandService.RunAsync`, lines 46-68) defaults to the literal
     string `"dotnet run"` and executes it through the same one-shot `RunCommandAsync` path used
     for build — `Process.Start` + read-to-end + `WaitForExitAsync` capped at a 120s timeout. A
     dev server (`dotnet run`, `npm run dev`) never exits on its own, so this just blocks for 120s,
     gets killed, and returns whatever stdout happened to buffer. There's no concept of a
     long-running process, no per-root targeting, and no way to have the frontend dev server and
     the dotnet host running side by side.

Both problems disappear once there's a single source of truth for "what projects live in this
repo, where, and how do you build/test/run each one" — fed into agent context for (1) and into the
execution service for (2).

**Explicitly deferred: AST/symbol indexing.** A full symbol graph would require per-language
parsers, cross-language link resolution, and invalidation on every write — high cost, and not what
either observed bug needs. Agentic grep/glob-style search over a small root manifest is the
established lower-cost pattern (this is what Claude Code itself does) and should be tried first.
Revisit AST only if a later gap is specifically about cross-file rename/type-flow reasoning that
root-scoped search can't resolve.

### Mature-agent patterns surveyed for this phase

Checked this phase's scope against how Claude Code/Cline/Continue structure agent context and
workflow, to make sure nothing load-bearing was missed. Per pattern:

| Pattern | Verdict | Where |
|---|---|---|
| Active toolset JSON schemas | Already in place | `WorkerAgentLoop.BuildAllTools()` etc. — no action |
| Sub-project manifests in context | **Adding** | 9e (`Roots` on the projection) |
| Root-level rule files (CLAUDE.md-equivalent) | **Adding** | New 9h |
| Read-before-write mandate | **Adding** | New 9g |
| Discover → Plan → Edit → Verify loop, drop back on failure | **Strengthening** | New 9i — the Discover/Edit halves already exist in `WorkerAgentLoop`'s steps 1-9; Verify-then-loop-back does not |
| Full directory-tree dump in system prompt | **Not adopting** | Conflicts with the existing tool-based-discovery design (`nm_v1_workspace_list` on demand) the codebase already deliberately chose over pre-loading file trees — same staleness argument as the AST decision above, just one notch lighter. A `Roots` summary (9e) is the bounded version of this that's actually needed. |
| Platform/shell context (OS, current shell) | **Not applicable** | Commands run via `Process.Start` with `UseShellExecute = false` (`WorkspaceExecutionService.cs:203`) — there is no shell in the loop to name, and `FileSystemWorkspaceService.ListAsync` already normalizes paths to `/` before they reach the agent. The real platform risk is "is this build tool even installed on the host machine" (e.g. `make` on a bare Windows box) — that's an environment problem, not a missing-prompt-context problem, and already surfaces as a normal command failure (non-zero exit, stderr) for a human or the agent to read. |
| Surgical search-and-replace edits instead of full-file rewrite | **Not adopting now** | `WorkerAgentLoop.cs:32` deliberately mandates full-file replacement today ("Write = full file replacement... not just a diff"), presumably to avoid partial-patch application bugs. Worth revisiting in a later phase once 9g/9i are in and full-file rewrite cost (tokens, accidental drops on large files) is actually felt as a problem — not changing it speculatively here. |
| XML-tag tool-calling protocol (`<read_file><path>...`) | **Not adopting** | The codebase already uses native structured tool-calling (`NmToolUse`/`LlmToolDef` JSON schemas dispatched by `McpToolDispatcher`) — strictly better than parsing freeform XML out of model text. Switching would be a regression, not an upgrade; the *content* these tools wrap into XML (env snapshot, project profile, rule file) is already covered by 9a/9e/9h in our own format. |
| Strict "one tool call per turn" ReAct loop | **Not adopting** | `WorkerAgentLoop`/`OrchestratorAgentLoop` already dispatch every `NmToolUse` block in a single assistant turn (`WorkerAgentLoop.cs:102-113`). Forcing one-at-a-time would mean more LLM round trips for no observed benefit — nothing in either reported bug traces back to multi-tool turns. |
| Dedicated "Executor" agent node in the DAG | **Not adopting** | Phase 6.6 already drew this exact line on purpose ("Two-Plane Model": Reasoning Plane vs Execution Plane) — build/test/run is deterministic `Process.Start`, not an LLM decision, so it doesn't need its own agent/LLM node. 9b/9c/9i already give Worker direct tool access to execution; adding a 4th LLM-driven node would add latency/cost for a step that's correctly non-agentic today. |
| DAG-based rollback / fork-on-failure / "boot a fresh context at a past node" | **Already built — not new** | This is exactly what `BranchedFromProposalId`/`HypothesisForkType`, `KnownGoodState` snapshots, and the historical-scrubbing slice (`plans/slice-7e-historical-scrubbing.md`: cursor, branch-from-cursor, known-good) already do. The Orchestration Decision Log (Phase 3, 10e) already persists every routing decision with its input projection + reason, which is the "why did we fork here" trail this pattern asks for. No phase-9 action — flagging so it's not mistaken for a gap. |
| Auto-feeding failure diagnostics forward as a "lesson learned" on retry | **Partial gap, deferred** | The generic mechanism exists (`nm_v1_artifact_record` with type `Research`/`Decision`/`Constraint`, `WorkerAgentLoop.cs` step 7) but nothing *automatically* triggers it specifically on a build/test failure or a dead-letter/retry transition — an agent has to think to do it. Worth a small follow-up once 9i ships and there's real failure data to look at; not adding speculatively now. |



## 9a — `WorkspaceProfile` domain model + detection service

**New types**, `NodalMerge.Studio.Contracts/Domain/WorkspaceProfile.cs`:

```csharp
public sealed record ProjectRoot(
    string RelativePath,      // "" for repo root, "frontend", "backend/Host", etc.
    string Stack,             // "dotnet", "npm", "cargo", "go", "python", "make", "cmake"
    string? BuildCommand,     // null = stack has no build step (e.g. python)
    string? TestCommand,
    string? RunCommand,       // null = no obvious entry point detected
    bool IsLongRunning);      // true for npm "dev"/"start" scripts and ASP.NET hosts — see 9c

public sealed record WorkspaceProfile(
    string BranchId,
    IReadOnlyList<ProjectRoot> Roots,
    DateTimeOffset DetectedAt);
```

**New service**, `IWorkspaceProfileService` (`NodalMerge.Studio.Core/Services/ServiceContracts.cs`):

```csharp
public interface IWorkspaceProfileService
{
    Task<WorkspaceProfile> GetOrDetectAsync(string branchId, CancellationToken ct = default);
    Task<WorkspaceProfile> RescanAsync(string branchId, CancellationToken ct = default); // force recompute
}
```

Implementation in `NodalMerge.Studio.Storage/WorkspaceProfileService.cs`:

- Walk the branch directory recursively, skipping `node_modules`, `bin`, `obj`, `.git`,
  `.nodalmerge` (reuse `FileSystemWorkspaceService`'s `IsHidden` convention for dot-folders).
- Group marker files by their **containing directory**, not a single repo-wide flag — this is the
  actual fix for the npm non-recursive bug, generalized: `*.csproj`/`*.slnx` →  a `dotnet` root at
  that directory; `package.json` → an `npm` root at that directory; same pattern for
  `Cargo.toml`/`go.mod`/`pyproject.toml`/`Makefile`/`CMakeLists.txt`.
- Nesting guard: if a `.csproj` lives under a directory that's already inside a detected root with
  a `.sln`/`.slnx` at a shallower level, fold it into the shallower root rather than creating one
  root per project file (otherwise a 20-project dotnet solution becomes 20 roots). Use the nearest
  `.sln`/`.slnx` ancestor when one exists; fall back to one root per `.csproj` directory when it
  doesn't.
- For npm roots, read `package.json` `scripts` to populate `BuildCommand` (prefer `scripts.build`,
  else omit), `TestCommand` (prefer `scripts.test`), `RunCommand` (prefer `scripts.dev`, else
  `scripts.start`), and set `IsLongRunning = true` whenever a `dev`/`start` script was found.
- For dotnet roots, `RunCommand = "dotnet run"` scoped to that root's `.csproj`/`.sln`, and
  `IsLongRunning = true` only if the project references `Microsoft.NET.Sdk.Web` (read the
  `.csproj` XML for the `Sdk` attribute) — a console app or test project is one-shot.
- Cache per `branchId` in memory (a `WorkspaceProfile` is cheap to recompute but no reason to do it
  on every call); `RescanAsync` bypasses the cache. No persistence/rehydration needed yet — branch
  directories are recreated identically on InitBranchAsync/seed, so a cold Host just recomputes
  lazily on first access.

## 9b — Per-root build/test in `WorkspaceExecutionService`

- `ExecuteAsync` resolves a `WorkspaceProfile` via `IWorkspaceProfileService` instead of calling
  `ResolveBuildCommands`/`ResolveTestCommands` against the branch root directly.
- For each `ProjectRoot` with a non-null `BuildCommand`/`TestCommand`, run it with
  `WorkingDirectory = Path.Combine(workDir, root.RelativePath)` — this is the actual fix for the
  "detected but runs from the wrong directory" bug.
- `BuildResult`/`TestResult` gain a `ProjectRoot` field (the `RelativePath`) so results can be
  attributed and rendered per sub-project instead of one flat list.
- `request.BuildCommand`/`request.TestCommand` (explicit override) keeps today's behavior
  unchanged: a single command at the branch root, no profile involved. Profile-based resolution
  only kicks in when no explicit command is given — same three-level priority as today, profile
  detection just replaces "Level 2: auto-detection."

## 9c — Long-running "run" support

Today's `RunAsync` (`WorkspaceExecutionCommandService.cs:46-68`) treats every run command as a
one-shot build: blocking `Process.Start` + `WaitForExitAsync(timeout)`. That's correct for a
console app or a script, but wrong for anything serving requests.

- Add `RunningProcessRegistry` (in-memory, per-branch, not durable — restarting the Host kills any
  dev servers it had spawned, which is correct/expected): tracks `{ branchId, rootPath, pid,
  startedAt, recentOutputBuffer }` per launched long-running process.
- `RunAsync(branchId, rootPath?, runCommand?, ct)`:
  - Resolve target root from the profile (`rootPath` param selects which `ProjectRoot`; omitted =
    every root with a non-null `RunCommand`, mirroring the existing "all detected build systems
    run" convention from build/test).
  - If `root.IsLongRunning`: start the process *without* waiting for exit — redirect stdout/stderr
    into a bounded ring buffer read by a background pump, register it, and return immediately with
    `{ pid, running: true, recentOutput }`. Do not apply `TimeoutSeconds` as a kill timer for
    long-running processes — it's meaningless here.
  - If not long-running: keep today's blocking behavior unchanged.
- New `StopAsync(branchId, pid, ct)` — kills the tracked process tree, removes it from the
  registry. Call this before re-running the same root (replace, don't leak orphans) and when a
  work unit's branch is deleted.
- This is what makes "frontend dev server + dotnet host running side by side" possible: two
  `ProjectRoot`s, two registry entries, two independent processes — no terminal/shell
  multiplexing logic needed in the Host; PowerShell-vs-bash is moot since `Process.Start` doesn't
  go through a shell at all (matches today's `UseShellExecute = false`).

## 9d — REST + MCP surface

Following the Phase 6.5 consolidation pattern (MCP/REST/agent-loop dispatcher share one
implementation via `IWorkspaceExecutionCommandService`):

| MCP Tool | REST | Purpose |
|---|---|---|
| `nm_v1_workspace_profile_get` | `GET /studio/workspace/profile?branchId=` | Return detected `WorkspaceProfile` (roots, stacks, commands) |
| `nm_v1_workspace_profile_rescan` | `POST /studio/workspace/profile/rescan?branchId=` | Force recompute (e.g. after a worker adds a new `package.json`) |
| *(existing)* `nm_v1_workspace_run` | `POST /studio/workspace/run?branchId=` | Extend body with optional `rootPath`; response gains `pid`/`running` for long-running roots |
| `nm_v1_workspace_run_stop` | `POST /studio/workspace/run/stop?branchId=` | Stop a tracked long-running process by `pid` (or all, if omitted) |

`branchId` stays a query parameter on every route, per the existing fix noted in
`StudioRestEndpoints.cs:399-402` (branch IDs like `merge/{workUnitId}` contain a literal `/`).

`BuildResult`/`TestResult`/the run response all gain `ProjectRoot` so the Merge Review panel can
render per-root sections instead of one flat build/test list, and so the Workspace tab can show one
Run/Stop control per detected root.

## 9e — Feed the profile into agent context (fixes the prompting gap)

- Add a compact root summary to the `AgentWorkspace` projection
  (`ProjectionManager.BuildAgentWorkspaceAsync`, `ProjectionManager.cs:234-315`), next to the
  existing `Execution` field:

  ```csharp
  public sealed record ProjectRootSummary(string RelativePath, string Stack);

  public sealed record AgentWorkspaceProjectionPayload(
      string? AgentId,
      string? WorkUnitId,
      ArtifactChain Artifacts,
      IReadOnlyList<ArtifactRef> InheritedConstraints,
      WorkspaceExecutionSummary? Execution = null,
      IReadOnlyList<ProjectRootSummary>? Roots = null);   // new
  ```

  Populated via `IWorkspaceProfileService.GetOrDetectAsync(wu.BranchId, ct)`, same best-effort
  try/catch convention as the existing execution-summary block (lines 277-305) — never fail a
  projection over profile detection.

- Strengthen `WorkerAgentLoop.cs`'s existing same-name-file check (lines 34-37) with a stack-aware
  version: *"The workspace may contain more than one project (see the project roots in your
  context). Before creating a new file, identify which root the task actually belongs to —
  'endpoint' usually means a controller/route handler in a backend root, not a new frontend file —
  and use nm_v1_workspace_list scoped to that root's path first."*
- Give `PlannerAgentLoop.cs` the same root list so file-scope slices it writes are root-aware (a
  slice touching `backend/**` shouldn't get handed to a worker that only looked at `frontend/`).
- Add `nm_v1_workspace_profile_get` to both loops' tool schemas so an agent can pull the full
  profile on demand instead of relying solely on the projection snapshot.

## 9f — Extension UI

- Workspace tab / Merge Review tab: replace the single Build/Test/Run buttons with one row per
  detected `ProjectRoot` (label = `RelativePath` or "repo root" + `Stack` badge), each with its own
  Build/Test/Run-Stop controls — mirrors the per-root results from 9b/9d.
- Run controls show live state (`Running (pid 1234)` / `Stopped`) backed by polling
  `/studio/workspace/profile` + the run response's `running` flag — no new websocket channel needed
  for v1.

## 9g — Read-before-write enforcement

The existing "check for a same-named file elsewhere in the tree" rule (`WorkerAgentLoop.cs:34-37`)
is prompt-level — best-effort, not enforced. The mature-agent pattern is a hard runtime rule:
*you cannot overwrite a file you haven't read in this branch.* This is a different bug class than
9a-9f (which fix "ends up in the wrong root"); this one fixes "blindly clobbers a file's real
content because it never looked."

- `McpToolDispatcher` is registered as a singleton (`InMemoryAgentRuntimeService.cs:619`), shared
  across every agent/run, so the read cache lives there as a
  `ConcurrentDictionary<string, byte> _readPaths` keyed by `$"{branchId}:{path}"`.
- `WorkspaceReadAsync`: on a successful read (content found), record the key.
- `WorkspaceWriteAsync`: before writing, call `fileWorkspace.ExistsAsync(branchId, path)`. If the
  file exists and `$"{branchId}:{path}"` isn't in the cache, return an error instead of writing:
  *"File '{path}' already exists with content you haven't read. Call nm_v1_workspace_read first,
  then write the updated content."* New files (don't yet exist) are unaffected — no read is
  required to create something genuinely new, that's the 9a-9f/9h problem, not this one.
- Scope is per-(branch, path), not per-agent-session: once anyone has read a path in a branch, it
  stays "seen" for the rest of that branch's life. Simpler than threading agent/session identity
  through every workspace call, and the goal (don't overwrite unseen content) doesn't actually need
  session granularity.
- No cache eviction — branches are bounded in number and lifetime (cleaned up with their work
  units); unbounded growth here isn't a practical concern at this scale.

## 9h — Root-level rule files (CLAUDE.md-equivalent)

Currently there's no project-specific instruction file support at all — every agent run uses only
the hardcoded default/profile system prompt. Add support for a repo (or per-root) instruction file,
the same role `CLAUDE.md`/`AGENTS.md`/`.cursorrules`/`.clinerules` play elsewhere.

- On `WorkspaceProfile` detection (9a), for each `ProjectRoot` (and the branch root itself) check
  for, in order: `AGENTS.md`, `CLAUDE.md`, `.clinerules`, `.cursorrules`. First match wins per
  root; reading more than one would just be conflicting instructions for the same scope.
- Add `RuleFileContent` (capped, e.g. 4000 chars — same spirit as other tools' "don't let one file
  blow the budget" caps) to `ProjectRoot`, surfaced through the same `Roots` field added to the
  `AgentWorkspace` projection in 9e.
- Append found rule content to the kickoff message (`WorkerAgentLoop.cs:73`,
  `PlannerAgentLoop`'s equivalent), not the static system prompt — it's per-branch/per-root data,
  not a constant, so it belongs with the other dynamic, per-run context the kickoff message
  already carries (resume notices, etc.), not baked into `DefaultSystemPrompt`.
- Format: `"Project root '{path}' ({stack}) has its own instructions — follow them:\n\n{content}"`
  per root that has one.
- This is the single highest-leverage piece for "the prompting seems to be lacking project
  context" generally (not just the multi-root case) — it's the mechanism for the user to hand the
  agent durable, project-specific facts (naming conventions, "the API lives in X", "never touch Y")
  without re-explaining them every task.

## 9i — Self-verify loop before propose (config-gated)

`WorkerAgentLoop`'s workflow (steps 1-11) goes straight from writing files (step 5) to diffing and
proposing (steps 8-9) — verification only happens after the fact, via the opt-in
`WorkspaceExecutionRule` policy gate (Phase 6.6, `RequireBuildBeforeProposal`/
`RequireTestBeforeProposal`, both default `false`). When that policy is off (the common case today)
nothing checks the agent's work compiles before it proposes a merge.

**Gated by the same two existing flags — not a new always-on instruction.** Telling every worker to
always build+test before proposing would add latency/cost even for repos where the user hasn't
asked for build/test verification at all (docs-only changes, stacks with no build step, or simply a
user who hasn't opted in). So this only changes behavior when
`WorkspaceOptions.RequireBuildBeforeProposal` and/or `RequireTestBeforeProposal` are `true` — same
opt-in convention Phase 6.6 already established, zero behavioral change when both are off.

- `WorkerAgentLoop` gains two optional constructor params, same additive pattern as `isResume`:
  `selfVerifyBuild = false`, `selfVerifyTest = false`.
- Wired at the two `new WorkerAgentLoop(...)` call sites in `InMemoryAgentRuntimeService.cs` (lines
  224 and 530) from the resolved `WorkspaceOptions` singleton:
  `selfVerifyBuild: options.RequireBuildBeforeProposal, selfVerifyTest: options.RequireTestBeforeProposal`.
- When either is `true`, the kickoff message (`WorkerAgentLoop.cs:73`, same mechanism as the
  resume-notice append) gets an extra instruction: *"This workspace requires a passing
  {build|test|build and test} before a merge proposal is accepted. Call nm_v1_workspace_build /
  nm_v1_workspace_test scoped to the root(s) you touched after writing files. If it fails, read the
  error output, fix it, and retry before calling nm_v1_merge_propose."* When both flags are `false`,
  nothing is added — today's kickoff message is unchanged byte-for-byte.
- Still a prompt-level loop (the agent decides to retry), not a code-enforced state machine — the
  `WorkspaceExecutionRule` policy gate remains the authoritative, enforced check at proposal time
  regardless of whether the worker self-checked. This just means that when the gate is on, the
  worker is told about it up front and gets a faster, cheaper feedback loop instead of discovering
  the failure only after `nm_v1_merge_propose` rejects it.

## Slices

| Slice | Scope |
|---|---|
| 9a | `WorkspaceProfile`/`ProjectRoot` domain types + `WorkspaceProfileService` detection (per-directory marker scan, nesting guard, npm script parsing) |
| 9b | `WorkspaceExecutionService.ExecuteAsync` resolves per-root commands via the profile; `BuildResult`/`TestResult` gain `ProjectRoot` |
| 9c | `RunningProcessRegistry`; `RunAsync` branches on `IsLongRunning`; `StopAsync` |
| 9d | MCP tools + REST endpoints (`profile_get`, `profile_rescan`, `run_stop`, extended `run`) |
| 9e | `AgentWorkspace` projection gains `Roots`; Worker/Planner prompts updated; `nm_v1_workspace_profile_get` added to tool schemas |
| 9f | Extension: per-root Build/Test/Run-Stop UI in Workspace tab + Merge Review tab |
| 9g | `McpToolDispatcher` read-before-write cache; `WorkspaceWriteAsync` blocks unread overwrites of existing files |
| 9h | Root-level rule file detection (`AGENTS.md`/`CLAUDE.md`/`.clinerules`/`.cursorrules`) + injection into kickoff message |
| 9i | `WorkerAgentLoop` gains `selfVerifyBuild`/`selfVerifyTest` params wired from `WorkspaceOptions`; kickoff message conditionally instructs self-verify (build/test scoped to touched root) before propose — no-op when both flags are off |

## Verification

1. `dotnet build NodalMerge.Studio.slnx` / `dotnet test` — 0 errors, full pass.
2. Unit: profile detection on a fixture tree shaped `backend/Host.csproj` + `frontend/package.json`
   (no root-level markers) finds both roots at their correct paths.
3. Unit: nesting guard — a fixture with a root `.slnx` and 3 nested `.csproj` files produces one
   dotnet root, not four.
4. Integration: `ExecuteAsync` on the two-root fixture runs `dotnet build` in `backend/` and `npm
   run build` in `frontend/` and both succeed (today: at least one fails, run from the wrong cwd).
5. Integration: `RunAsync` against an ASP.NET Web SDK root returns `running: true` with a live pid
   within a couple seconds, not after blocking for the full timeout; `StopAsync` actually kills it.
6. Manual: open the extension against a real frontend+dotnet repo; confirm Workspace tab shows two
   rows; Run both; confirm both processes are live (dotnet host answering HTTP, npm dev server
   answering HTTP) at the same time.
7. Manual prompting check: ask the assistant to "update the compliment endpoint" against the same
   fixture repo; confirm the worker's first move is `nm_v1_workspace_list` scoped to the backend
   root (or reads the profile) rather than creating a new frontend file.
8. Unit: `WorkspaceWriteAsync` against an existing, never-read path returns the "haven't read"
   error and does not write; the same call succeeds after a prior `WorkspaceReadAsync` on that
   exact `branchId:path`; writing a genuinely new (non-existent) path succeeds with no prior read.
9. Unit: profile detection on a fixture root containing `AGENTS.md` populates `RuleFileContent`;
   a root with both `AGENTS.md` and `CLAUDE.md` only picks up `AGENTS.md` (first-match-wins order).
10. Manual: drop an `AGENTS.md` with an explicit, unusual instruction (e.g. "always prefix log
    messages with `[nm]`") into a fixture root; confirm a worker's kickoff message contains it and
    the agent's output honors it.
11. Manual: introduce a deliberate compile error in a task description; confirm the worker calls
    `nm_v1_workspace_build` scoped to the affected root, sees the failure, and fixes it before
    calling `nm_v1_merge_propose` rather than proposing the broken state.
