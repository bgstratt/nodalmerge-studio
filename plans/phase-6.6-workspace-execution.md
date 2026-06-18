# Phase 6.6 — Workspace Execution Layer (Build, Test, Lint)

## Problem Statement

The current pipeline is pure semantic trust:

```
Agent writes files → LLM reviews files → Human reviews diffs → Apply
```

There is no non-LLM grounding signal anywhere. Build and test never occur. The system *looks* like a CI-backed PR workflow but doesn't *behave* like one. This creates cognitive friction and limits confidence in agent-produced changes.

## What's Already in Place (Why This is a Layer, Not a Rewrite)

| Existing Capability | How It Helps |
|---|---|
| Branches materialized at work unit creation | Every branch is a real filesystem directory — build/test commands run directly inside it |
| `IFileWorkspaceService.GetWorkingDirectoryAsync(branchId)` | Returns the full filesystem path for any branch |
| `IPolicyGateService` + `PolicyCheckpoint` enum | Pluggable checkpoints at `BeforeEnqueue`, `ProposalCreated`, `BeforeMerge` |
| `MergeCommandService.ProposeAsync` | Single shared code path for all transports — a hook here runs execution before proposal finalization |
| `KnownGoodState` snapshots | Already creates `knowngood/{stateId}` branches seeded from a source branch |
| `IFileWorkspaceService.ApplyBranchAsync` | Copies all files from source branch to target — needed for composite/group execution |
| `MergeProposal.VerificationResults` | Free-form string already rendered in the Merge Review UI — can carry structured build/test results |
| `WorkUnit.Metadata` | Dictionary that can carry per-work-unit build/test command overrides |
| `WorkUnit.FileScope` | Glob patterns already used for routing — can determine which build system applies |

---

## Output Size Management

Large builds (`dotnet build` on a 100-project solution) can produce megabytes of output. Reading all stdout/stderr into a single in-memory string and serializing it into a NodalMerge node risks OOM or slow writes.

### Truncation Strategy

`WorkspaceExecutionService` applies configurable truncation before returning results:

| Setting | Default | Description |
|---|---|---|
| `MaxOutputBytes` | `64 KB` | Max stdout or stderr bytes to capture per command |
| `TruncationMode` | `"Tail"` | `"Head"` = keep first N bytes, `"Tail"` = keep last N bytes, `"HeadTail"` = first + last N/2 bytes each |

```csharp
public sealed record WorkspaceOptions
{
    // ... existing ...
    public int MaxOutputBytes { get; set; } = 64 * 1024;
    public string TruncationMode { get; set; } = "Tail";
}
```

Truncated output appends a marker at the cut point:

```
...[truncated 1.2 MB, showing last 64 KB]...

dotnet test --no-build
Passed!  - Failed: 0, Passed: 47, Skipped: 0, Total: 47
```

Full untruncated output is never stored — only the truncated version enters `BranchExecutionResult` and ultimately `MergeProposal.VerificationResults`.

### UI Display of Large Output

Expandable stdout/stderr sections in the Merge Review tab render in a scrollable `<pre>` block capped at 300px height by default, with a "Download full output" link when truncation occurred. The download pulls from the REST endpoint `GET /studio/workspace/{branchId}/exec/{resultId}/output` which re-runs the command with streaming output if not cached, or returns the cached truncated version if already persisted.

---

## Interactive Branch Access

Users need to inspect and manually work with branch directories — not just trigger automated execution.

### REST Endpoint

`GET /studio/workspace/{branchId}/path` returns:

```json
{
  "branchId": "work-abc123",
  "workingDirectory": "C:\\Users\\...\\nodalmerge-studio\\workspaces\\work-abc123",
  "exists": true
}
```

### VS Code Commands (Slice 16j)

Two new commands registered in `extension.ts` and the `package.json` contributes:

| Command | Description |
|---|---|
| `nodalmerge.openBranchInTerminal` | Opens an integrated terminal at the branch's working directory |
| `nodalmerge.openBranchFolder` | Opens the branch directory as a VS Code workspace folder |

### UI Affordances

**Workspace tab** — new buttons on work unit cards (right of the [Spawn] / [Build] / [Test] buttons):
- **[📂 Open Folder]** — calls `vscode.commands.executeCommand('vscode.openFolder', branchUri)` after confirming the path from the REST endpoint
- **[>_ Terminal]** — calls `vscode.window.createTerminal({ cwd: branchPath, name: 'Branch: ' + branchId })`

**Work unit inspector** (Home tab) — adds the branch path as a clickable monospace field that copies to clipboard on click, with an explicit "Open in Terminal" button.

**DAG Replay tab** — when a node is selected and its playback bar is visible, adds an **[>_ Open Terminal]** button.

---

## Execution Result Persistence

### Problem

If a user runs Build from the Workspace tab and the host restarts before a proposal is created, the result is lost. Today results are only persisted when attached to a `MergeProposal` (a NodalMerge node).

### Solution

`BranchExecutionResult` is persisted as a `StudioNodeKind.ExecutionResultV1` NodalMerge node:

```csharp
// WorkspaceExecutionService persists every result after execution
var nodeId = $"exec/{branchId}/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
await nodeStore.WriteNodeAsync(
    StudioNodeKind.ExecutionResultV1,
    nodeId,
    JsonSerializer.Serialize(result),
    cancellationToken);
```

The latest result per branch can be queried:

```csharp
// GET /studio/workspace/{branchId}/exec/latest
// Returns the most recent BranchExecutionResult for the branch, or 404
```

### Expiry

Execution result nodes are rehydrated on startup alongside all other `IRehydratable` services but do **not** have infinite retention. A background cleanup (runs on Host startup and every hour) deletes execution results older than 24 hours unless they are referenced by a `MergeProposal`.

---

## Agent Access to Execution Results

### MCP Tool: `nm_v1_workspace_exec_status`

| Tool | Purpose | Required Params |
|---|---|---|
| `nm_v1_workspace_exec_status` | Query latest execution result for a branch | `branchId` |

Response:

```json
{
  "branchId": "work-abc123",
  "lastExecutedAt": "2026-06-18T12:00:00Z",
  "allSucceeded": true,
  "builds": [
    { "buildSystem": "dotnet", "success": true, "exitCode": 0, "command": "dotnet build" }
  ],
  "tests": [
    { "buildSystem": "dotnet", "success": true, "totalTests": 47, "passed": 47, "failed": 0, "command": "dotnet test" }
  ],
  "truncated": false
}
```

This tool is available to agents (registered in all agent loops' tool schemas) and via REST.

### Projection Integration

The `AgentWorkspace` projection type includes an `execution` field when a result exists:

```json
{
  "workUnit": "...",
  "plan": "...",
  "execution": {
    "allSucceeded": true,
    "buildSystems": ["dotnet"],
    "testSummary": "47 passed / 0 failed"
  }
}
```

This is surfaced at the `Compact` level only; `Emergency` omits it entirely. `Full` includes the complete `BranchExecutionResult`. The `ProjectionManager` reads from `IWorkspaceExecutionService`'s latest persisted result for the branch.

---

## Architecture

### Two-Plane Model

```
┌─── Reasoning Plane (already built) ───┐
│ DAG, projections, agents, proposals   │
│ reviews, artifact lineage, replay     │
└───────────────────────────────────────┘

┌─── Execution Plane (this phase) ──────┐
│ build(), test(), lint(), exec()       │
│ deterministic verification signals    │
│ attached to branches, not agents      │
└───────────────────────────────────────┘
```

### Language-Agnostic Design

The execution service does not know or care what language the repository uses. It receives a command string (e.g., `dotnet build`, `cargo test`, `go build ./...`) and runs it via `Process.Start` in the branch's working directory. Command resolution follows a three-level priority:

**Level 1: Explicit override (highest priority)**
The caller provides an explicit command string. The execution service runs it as-is.

**Level 2: Auto-detection from branch contents**
When no explicit command is given, `WorkspaceExecutionService` inspects the branch's working directory for known build-system files:

| File Found | Default Build | Default Test |
|---|---|---|
| `*.csproj` / `*.slnx` | `dotnet build` | `dotnet test` |
| `Cargo.toml` | `cargo build` | `cargo test` |
| `package.json` | `npm install && npm run build` (or `npm run build` if `node_modules` exists) | `npm test` |
| `go.mod` | `go build ./...` | `go test ./...` |
| `pyproject.toml` | — (Python doesn't "build") | `pytest` |
| `Makefile` | `make` | `make test` |
| `CMakeLists.txt` | `cmake --build build` | `ctest --test-dir build` |

For multi-language repos with multiple build-system files, **all detected systems are run in sequence** and results are reported individually.

**Level 3: Configured global default**
VS Code settings provide a system-wide fallback:

```jsonc
{
  "nodalmerge.buildCommand": "dotnet build",
  "nodalmerge.testCommand": "dotnet test"
}
```

### Per-Work-Unit Commands

The existing `WorkUnit.Metadata` dictionary can carry build/test overrides for a specific work unit:

```json
{
  "metadata": {
    "buildCommand": "cargo build -p api",
    "testCommand": "cargo test -p api"
  }
}
```

The execution service reads metadata before falling back to auto-detection or global defaults.

---

## Domain Types

All new types in `NodalMerge.Studio.Contracts/Domain/`:

```csharp
public sealed record BuildResult(
    bool Success,
    int ExitCode,
    string StdOut,
    string StdErr,
    string? BuildSystem,    // "dotnet", "cargo", "npm", "go", "pytest", "make", null if explicit command
    string Command,          // the actual command that ran
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record TestResult(
    bool Success,
    int ExitCode,
    int TotalTests,
    int Passed,
    int Failed,
    int Skipped,
    string StdOut,
    string? BuildSystem,
    string Command,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt);

public sealed record LintResult(
    string RuleId,
    string Severity,       // "error", "warning"
    string File,
    int? Line,
    string Message);

public sealed record BranchExecutionResult(
    string BranchId,
    IReadOnlyList<BuildResult> Builds,
    IReadOnlyList<TestResult> Tests,
    IReadOnlyList<LintResult> LintResults,
    bool AllSucceeded,
    DateTimeOffset ExecutedAt);

public sealed record WorkspaceExecutionRequest(
    bool Build = false,
    bool Test = false,
    bool Lint = false,
    string? BuildCommand = null,     // null = auto-detect or skip
    string? TestCommand = null,
    string? LintCommand = null,
    bool AllowAutoDetect = true,
    int TimeoutSeconds = 300,
    Dictionary<string, string>? EnvironmentVariables = null);
```

---

## Interfaces

New in `NodalMerge.Studio.Core/Services/ServiceContracts.cs`:

```csharp
/// <summary>
/// Executes build, test, and lint commands inside a branch's working directory.
/// Language-agnostic — runs whatever command string it receives via Process.Start.
/// </summary>
public interface IWorkspaceExecutionService
{
    /// <summary>
    /// Execute build/test/lint on a single branch.
    /// </summary>
    Task<BranchExecutionResult> ExecuteAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Merge files from multiple source branches into a temporary composite branch,
    /// then execute build/test/lint on the composite. Cleans up the temp branch afterward.
    /// </summary>
    Task<BranchExecutionResult> ExecuteCompositeAsync(
        IReadOnlyList<string> sourceBranchIds,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default);
}
```

---

## Implementation (`WorkspaceExecutionService`)

New: `src/NodalMerge.Studio.Storage/WorkspaceExecutionService.cs`

### Single Branch Execution

```csharp
internal sealed class WorkspaceExecutionService(
    IFileWorkspaceService fileWorkspace) : IWorkspaceExecutionService
{
    public async Task<BranchExecutionResult> ExecuteAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default)
    {
        var workDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct)
            .ConfigureAwait(false);
        if (workDir is null)
            throw new InvalidOperationException($"Branch '{branchId}' has no working directory.");

        var builds = new List<BuildResult>();
        var tests  = new List<TestResult>();

        if (request.Build)
        {
            var commands = ResolveBuildCommands(request, workDir);
            foreach (var cmd in commands)
            {
                var result = await RunCommandAsync(workDir, cmd, "build", request.TimeoutSeconds, ct);
                builds.Add(result);
            }
        }

        if (request.Test)
        {
            var commands = ResolveTestCommands(request, workDir);
            foreach (var cmd in commands)
            {
                var result = await RunTestCommandAsync(workDir, cmd, request.TimeoutSeconds, ct);
                tests.Add(result);
            }
        }

        return new BranchExecutionResult(
            branchId, builds, tests, [],
            builds.All(b => b.Success) && tests.All(t => t.Success),
            DateTimeOffset.UtcNow);
    }
    // ...
}
```

### Composite Execution (`ExecuteCompositeAsync`)

1. Create temp branch: `$"exec-group-{Guid.NewGuid():N}"` via `fileWorkspace.InitBranchAsync`
2. For each source branch, apply files via `fileWorkspace.ApplyBranchAsync`:
   - First branch seeds the composite directory
   - Subsequent branches apply on top; file conflicts are detected and reported
3. Run `ExecuteAsync` on the composite branch
4. Delete the composite branch's directory
5. Return the aggregated result
6. Conflict handling: if two source branches wrote different content to the same file path, record a warning in the result and use the last-writer's content

### Command Auto-Detection

```csharp
private static IReadOnlyList<(string Command, string BuildSystem)> ResolveBuildCommands(
    WorkspaceExecutionRequest request, string workDir)
{
    if (request.BuildCommand is { Length: > 0 })
        return [(request.BuildCommand, null!)];

    if (!request.AllowAutoDetect)
        return [];

    var detected = new List<(string, string)>();

    if (Directory.EnumerateFiles(workDir, "*.csproj", SearchOption.AllDirectories).Any() ||
        Directory.EnumerateFiles(workDir, "*.slnx", SearchOption.AllDirectories).Any())
        detected.Add(("dotnet build", "dotnet"));

    if (File.Exists(Path.Combine(workDir, "Cargo.toml")))
        detected.Add(("cargo build", "cargo"));

    if (File.Exists(Path.Combine(workDir, "package.json")))
        detected.Add((
            Directory.Exists(Path.Combine(workDir, "node_modules"))
                ? "npm run build"
                : "npm install && npm run build",
            "npm"));

    if (File.Exists(Path.Combine(workDir, "go.mod")))
        detected.Add(("go build ./...", "go"));

    if (File.Exists(Path.Combine(workDir, "Makefile")))
        detected.Add(("make", "make"));

    if (File.Exists(Path.Combine(workDir, "CMakeLists.txt")))
        detected.Add(("cmake --build build", "cmake"));

    return detected;
}
```

### Command Execution

```csharp
private static async Task<BuildResult> RunCommandAsync(
    string workDir, (string Command, string BuildSystem) cmd, string kind, int timeoutSec, CancellationToken ct)
{
    var startedAt = DateTimeOffset.UtcNow;
    using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
    cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));

    var psi = new ProcessStartInfo
    {
        FileName = cmd.Command.Contains(' ') ? cmd.Command.Split(' ')[0] : cmd.Command,
        Arguments = cmd.Command.Contains(' ')
            ? string.Join(' ', cmd.Command.Split(' ').Skip(1))
            : "",
        WorkingDirectory = workDir,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
        CreateNoWindow = true,
    };

    using var process = Process.Start(psi)!;
    var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token).ConfigureAwait(false);
    var stderr = await process.StandardError.ReadToEndAsync(cts.Token).ConfigureAwait(false);
    await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);

    return new BuildResult(
        process.ExitCode == 0,
        process.ExitCode,
        stdout,
        stderr,
        cmd.BuildSystem,
        cmd.Command,
        startedAt,
        DateTimeOffset.UtcNow);
}
```

### Test Result Parsing

For `dotnet test`, the output is parsed for the standard summary line:

```
Passed!  - Failed: 0, Passed: 47, Skipped: 0, Total: 47
```

For `cargo test`, the output is parsed for:

```
test result: ok. 23 passed; 0 failed; 0 ignored; 0 measured; 0 filtered out
```

For `pytest`, the output is parsed for:

```
======================= 15 passed, 2 failed in 1.23s =======================
```

For unrecognized output, `TotalTests`/`Passed`/`Failed` remain 0 and `Success` is determined by exit code only.

---

## Transport Consolidation (Phase 6.5 Pattern)

Following the Phase 6.5 command-surface-hardening pattern, every new execution tool must converge MCP and REST onto a single shared implementation. This prevents the drift documented in Phase 6.5 where MCP tools and REST endpoints diverged in behavior (e.g., `merge.propose` had diff/lineage/event/status-transition in the dispatcher but not in MCP or REST).

### Shared Command Service

New interface in `NodalMerge.Studio.Core/Services/ServiceContracts.cs`:

```csharp
/// <summary>
/// Shared entry point for workspace execution commands — called by both MCP tools
/// (WorkspaceTools) and REST endpoints (StudioRestEndpoints) so they cannot drift.
/// </summary>
public interface IWorkspaceExecutionCommandService
{
    Task<BranchExecutionResult> BuildAsync(
        string branchId,
        string? buildCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default);

    Task<BranchExecutionResult> TestAsync(
        string branchId,
        string? testCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default);

    Task<BranchExecutionResult> ExecAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default);

    Task<BranchExecutionResult?> GetLatestAsync(
        string branchId,
        CancellationToken ct = default);

    Task<BuildResult> RunAsync(
        string branchId,
        string? runCommand = null,
        int timeoutSeconds = 120,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default);

    Task<string?> GetBranchPathAsync(
        string branchId,
        CancellationToken ct = default);
}
```

Implementation in `src/NodalMerge.Studio.Storage/WorkspaceExecutionCommandService.cs` delegates to `IWorkspaceExecutionService` for actual Process.Start and to the persisted execution result store.

Registration in `ServiceCollectionExtensions`:

```csharp
services.AddSingleton<IWorkspaceExecutionCommandService, WorkspaceExecutionCommandService>();
```

### Tool → Command Service Mapping

| MCP Tool | REST Endpoint | Both Call |
|---|---|---|
| `nm_v1_workspace_build` | `POST /studio/workspace/{branchId}/build` | `commandService.BuildAsync()` |
| `nm_v1_workspace_test` | `POST /studio/workspace/{branchId}/test` | `commandService.TestAsync()` |
| `nm_v1_workspace_exec` | `POST /studio/workspace/{branchId}/exec` | `commandService.ExecAsync()` |
| `nm_v1_workspace_run` | `POST /studio/workspace/{branchId}/run` | `commandService.RunAsync()` |
| `nm_v1_workspace_exec_status` | `GET /studio/workspace/{branchId}/exec/latest` | `commandService.GetLatestAsync()` |
| `nm_v1_workspace_path` | `GET /studio/workspace/{branchId}/path` | `commandService.GetBranchPathAsync()` |

### Agent-Loop In-Process Path

The agent-loop dispatcher (`McpToolDispatcher.cs`) calls the same `IWorkspaceExecutionCommandService` methods directly, just as it already does for `ISchedulerCommandService`, `IArtifactCommandService`, `ITaskCommandService`, and `IMergeCommandService`. No agent-only code path diverges.

---

## MCP Tools

New tools registered in `McpToolNames` (frozen constants in `NodalMerge.Studio.Contracts/Versioning/McpToolNames.cs`), implemented as thin adapters over `IWorkspaceExecutionCommandService`:

| Tool | Purpose | Required Params | Optional Params | Thin Adapter Calls |
|---|---|---|---|---|
| `nm_v1_workspace_build` | Run build on a branch | `branchId` | `buildCommand`, `timeoutSeconds` | `commandService.BuildAsync()` |
| `nm_v1_workspace_test` | Run tests on a branch | `branchId` | `testCommand`, `timeoutSeconds` | `commandService.TestAsync()` |
| `nm_v1_workspace_exec` | Run build + test + lint | `branchId` | `build`, `test`, `lint` flags, commands, `timeoutSeconds` | `commandService.ExecAsync()` |
| `nm_v1_workspace_run` | Run the application in the branch | `branchId` | `runCommand`, `timeoutSeconds`, `environmentVariables` | `commandService.RunAsync()` |
| `nm_v1_workspace_exec_status` | Query latest execution result for a branch | `branchId` | — | `commandService.GetLatestAsync()` |
| `nm_v1_workspace_path` | Get branch working directory path | `branchId` | — | `commandService.GetBranchPathAsync()` |

These are **not** restricted to agent-only tools — humans trigger them from the Workspace tab UI buttons. Tools are registered in the MCP server's tool catalog and also exposed via REST per the consolidation table above.

## REST Endpoints

New routes in `StudioRestEndpoints.cs`, each a thin wrapper over `IWorkspaceExecutionCommandService`:

```csharp
// Slice 16d — workspace execution endpoints (MCP parity per Phase 6.5 pattern)
app.MapPost("/studio/workspace/{branchId}/build", async (
    string branchId, BuildRequestBody body,
    IWorkspaceExecutionCommandService cmd, CancellationToken ct) =>
{
    var result = await cmd.BuildAsync(branchId, body.BuildCommand, body.TimeoutSeconds, ct);
    return Results.Ok(result);
});

app.MapPost("/studio/workspace/{branchId}/test", async (
    string branchId, TestRequestBody body,
    IWorkspaceExecutionCommandService cmd, CancellationToken ct) =>
{
    var result = await cmd.TestAsync(branchId, body.TestCommand, body.TimeoutSeconds, ct);
    return Results.Ok(result);
});

app.MapPost("/studio/workspace/{branchId}/exec", async (
    string branchId, WorkspaceExecutionRequest body,
    IWorkspaceExecutionCommandService cmd, CancellationToken ct) =>
{
    var result = await cmd.ExecAsync(branchId, body, ct);
    return Results.Ok(result);
});

app.MapGet("/studio/workspace/{branchId}/exec/latest", async (
    string branchId,
    IWorkspaceExecutionCommandService cmd, CancellationToken ct) =>
{
    var result = await cmd.GetLatestAsync(branchId, ct);
    return result is not null ? Results.Ok(result) : Results.NotFound();
});

app.MapGet("/studio/workspace/{branchId}/path", async (
    string branchId,
    IWorkspaceExecutionCommandService cmd, CancellationToken ct) =>
{
    var path = await cmd.GetBranchPathAsync(branchId, ct);
    return path is not null
        ? Results.Ok(new { branchId, workingDirectory = path, exists = true })
        : Results.Ok(new { branchId, workingDirectory = (string?)null, exists = false });
});
```

### User-Initiated Execution

Users trigger branch execution through multiple surfaces — not just automated policy gates:

- **Workspace tab** [Build] and [Test] buttons call the REST endpoints directly. Results surface as VS Code information/warning toasts.
- **Workspace tab** [📂 Open Folder] and [>_ Terminal] buttons enable fully interactive inspection — users open the branch directory in VS Code or launch a terminal at the branch path and run any commands they want, including custom build steps, debugging, or exploratory commands the execution service doesn't know about.
- **Home tab** work unit inspector shows the latest persisted execution result.

No special permissions or agent involvement required — these are ordinary VS Code commands available to any user of the extension.

---

## Policy Hook (`WorkspaceExecutionRule`)

New `IPolicyRule` implementation at checkpoint `ProposalCreated`:

```csharp
public sealed class WorkspaceExecutionRule(
    WorkspaceOptions options,
    IWorkspaceExecutionService execution) : IPolicyRule
{
    string IPolicyRule.RuleId => "workspace-execution";
    PolicyCheckpoint IPolicyRule.Checkpoint => PolicyCheckpoint.ProposalCreated;

    async Task<PolicyResult> IPolicyRule.EvaluateAsync(
        IReadOnlyDictionary<string, object?> context, CancellationToken ct)
    {
        if (!options.RequireBuildBeforeProposal && !options.RequireTestBeforeProposal)
            return new PolicyResult(true, []);

        var branchId = context["branchId"] as string;
        if (branchId is null)
            return new PolicyResult(true, []);

        var result = await execution.ExecuteAsync(branchId, new(
            Build: options.RequireBuildBeforeProposal,
            Test: options.RequireTestBeforeProposal,
            BuildCommand: options.BuildCommand,
            TestCommand: options.TestCommand,
            TimeoutSeconds: options.ExecutionTimeoutSeconds), ct).ConfigureAwait(false);

        // Attach result to context so MergeCommandService can store it on the proposal
        context["executionResult"] = result;

        var violations = new List<PolicyViolation>();
        foreach (var build in result.Builds.Where(b => !b.Success))
            violations.Add(new("workspace-execution",
                $"[{build.BuildSystem ?? "build"}] failed (exit {build.ExitCode}): {Truncate(build.StdErr)}"));
        foreach (var test in result.Tests.Where(t => !t.Success))
            violations.Add(new("workspace-execution",
                $"[{test.BuildSystem ?? "test"}] {test.Failed}/{test.TotalTests} tests failed"));

        return violations.Count == 0
            ? new PolicyResult(true, [])
            : new PolicyResult(false, violations);
    }

    private static string Truncate(string s, int max = 300) =>
        s.Length <= max ? s : s[..max] + "...";
}
```

This policy is **opt-in** via `WorkspaceOptions` flags. When off, proposals work exactly as today.

---

## MergeCommandService Changes

In `MergeCommandService.ProposeAsync`, before diff generation:

```csharp
// Check ProposalCreated policy gate (includes WorkspaceExecutionRule if enabled)
var policyContext = new Dictionary<string, object?>
{
    ["branchId"] = sourceBranch,
    ["workUnitId"] = workUnitId,
};
var gateResult = await policyGate.EvaluateAsync(
    PolicyCheckpoint.ProposalCreated, policyContext, cancellationToken)
    .ConfigureAwait(false);

// Extract execution result if the policy ran it
BranchExecutionResult? execResult = null;
if (policyContext.TryGetValue("executionResult", out var obj) && obj is BranchExecutionResult er)
    execResult = er;
```

The `BranchExecutionResult` is serialized into `MergeProposal.VerificationResults` as structured JSON alongside any LLM review text. The proposal is still created even if the policy blocks — the blocked status is stored for display.

---

## Grouping / Composite Execution

### Problem

Individual child proposals from fanned-out workers may not be independently compilable. For example, a worker that added a single endpoint depends on a different worker's middleware change to compile.

### Solution A — Merger-Triggered Composite (Primary Path)

In `MergeReconciliationService`, after reconciling child proposals into a unified change set but before creating the reconciled proposal:

1. Collect all child branch IDs from the constituent proposals
2. Create a temp composite branch via `fileWorkspace.InitBranchAsync`
3. Apply all child branch files to the composite via `ApplyBranchAsync`
4. Run `ExecuteAsync` on the composite with configured build/test commands
5. Attach `BranchExecutionResult` to the reconciled proposal's `VerificationResults`
6. Delete the temp composite branch

If composite execution fails, the reconciled proposal is still created but marked with build/test failure data. The merger does not submit a reconciled proposal that silently fails.

### Solution B — On-Demand Composite (Manual Override)

A human or orchestrator calls `nm_v1_workspace_exec` with multiple `branchId` values. The tool resolves to `IWorkspaceExecutionService.ExecuteCompositeAsync`.

### Conflict Handling During Composite Assembly

When two child branches wrote different content to the same file path:
- The conflict is logged in `BranchExecutionResult` as a warning
- The last writer's content wins (deterministic based on branch creation order)
- The Merge Review UI shows the conflict alongside build/test results

---

## Configuration

### `WorkspaceOptions` (new fields)

```csharp
public sealed class WorkspaceOptions
{
    // ... existing fields ...
    public bool RequireBuildBeforeProposal { get; set; }   // default false
    public bool RequireTestBeforeProposal  { get; set; }   // default false
    public string? BuildCommand { get; set; }               // null = auto-detect
    public string? TestCommand  { get; set; }               // null = auto-detect
    public int ExecutionTimeoutSeconds { get; set; } = 300;
}
```

### VS Code Settings (new)

| Setting | Type | Default | Description |
|---|---|---|---|
| `nodalmerge.requireBuildBeforeProposal` | boolean | `false` | Require passing build before proposal submission |
| `nodalmerge.requireTestBeforeProposal` | boolean | `false` | Require passing tests before proposal submission |
| `nodalmerge.buildCommand` | string | `""` | Global build command (empty = auto-detect per branch) |
| `nodalmerge.testCommand` | string | `""` | Global test command (empty = auto-detect per branch) |

These map to the studio options endpoint (`/studio/options`) and are runtime-mutable like existing settings.

### Per-Work-Unit Metadata

Set via `workunit.create` metadata or via the UI:

```json
{
  "metadata": {
    "buildCommand": "cargo build -p api",
    "testCommand": "cargo test -p api -- --nocapture"
  }
}
```

Read by `WorkspaceExecutionService.ExecuteAsync` before auto-detection.

---

## UI Surface

### Merge Review Tab — New Section

```
┌─────────────────────────────────────────────┐
│ Workspace Execution                         │
│                                             │
│ Build                                       │
│  dotnet:  ✅ Passed (1.7s)                  │
│  npm:     ✅ Passed (3.2s)                  │
│                                             │
│ Tests                                       │
│  dotnet:  ✅ 47 passed / 0 failed (8.7s)    │
│  pytest:  ⚠ 12 passed / 3 failed (4.1s)    │
│                                             │
│ See output ▼                                │
│   (expandable stdout/stderr per result)     │
└─────────────────────────────────────────────┘
```

### Proposal Inspector (Home Tab)

Execution status badge next to proposal status badge:
- 🟢 **Built & Tested** (all green)
- 🟡 **Build failed / Tests failing**
- ⚪ **Not executed** (when policies are off)

### Workspace Tab — Quick Actions

Inline buttons on each work unit card:
- **[Build]** — triggers `nm_v1_workspace_build`
- **[Test]** — triggers `nm_v1_workspace_test`

Results surface as toasts and the latest result is cached and shown in the work unit inspector.

---

## Updated Pipeline (After Phase 6.6)

```
Work Unit Created
  → BranchV1 node written
  → Filesystem: directory created at {root}/{branch-id}/
  → Optional: seeded from parent branch or repository

Agent executes (Observe → Think → Act → Verify loop)
  → Reads/writes/deletes files in branch directory

Agent proposes merge
  → ProposalCreated policy gate fires
  → [NEW] WorkspaceExecutionRule: runs build/test in branch directory
  → [NEW] If policy blocks: proposal rejected with execution failure details
  → [NEW] If policy passes: execution results attached to proposal
  → Diff computed between source and target branch
  → MergeProposal created with Status=Draft

Automated reviewer (LLM agent)
  → Reads files + execution results + diff
  → Submits automated review: Approved or Rejected with verification notes

Human review
  → Sees: build status, test pass/fail counts, LLM review, file diffs, rollback plan
  → Approves or Rejects

Apply
  → Files copied source → target via ApplyBranchAsync
  → MergeResult artifact recorded
  → Work unit status → Merged
```

---

## Slices

| Slice | Scope | Estimated Impact |
|---|---|---|
| **16a** | `BranchExecutionResult`, `BuildResult`, `TestResult`, `LintResult`, `WorkspaceExecutionRequest` domain types in Contracts | New types, no behavioral change |
| **16b** | `IWorkspaceExecutionService` interface + `WorkspaceExecutionService` implementation (Process.Start, auto-detection, composite execution) | New service, no existing code changed |
| **16c** | MCP tools: `nm_v1_workspace_build`, `nm_v1_workspace_test`, `nm_v1_workspace_exec` + tool registration | New tools in McpServer, frozen names |
| **16d** | REST endpoints: `POST /studio/workspace/{branchId}/build`, `POST .../test`, `POST .../exec` | New routes in StudioRestEndpoints |
| **16e** | `WorkspaceExecutionRule` (IPolicyRule at ProposalCreated) + `WorkspaceOptions` fields | New rule in Storage, opt-in, zero behavioral change when off |
| **16f** | `MergeCommandService.ProposeAsync` hook: run policy gate before proposal, attach `BranchExecutionResult` to proposal | Small change to existing method |
| **16g** | Composite execution in `MergeReconciliationService`: build composite branch, run execution, attach results to reconciled proposal | New code path in existing merger |
| **16h** | UI: Merge Review execution section, proposal inspector badges, Workspace tab Build/Test buttons | New HTML/CSS/JS in existing panels |
| **16i** | VS Code settings: `requireBuildBeforeProposal`, `requireTestBeforeProposal`, `buildCommand`, `testCommand` | New settings in package.json, reads in AgentConfigService |
| **16j** | Interactive branch access: `GET /studio/workspace/{branchId}/path` endpoint, `nodalmerge.openBranchInTerminal` + `nodalmerge.openBranchFolder` commands, UI buttons (Workspace tab, Home inspector, DAG Replay) | New REST endpoint, 2 new VS Code commands, UI additions |
| **16k** | Execution result persistence: `StudioNodeKind.ExecutionResultV1`, rehydration, expiry cleanup | New node kind, new IRehydratable, background cleanup |
| **16l** | Agent tool access: `nm_v1_workspace_exec_status` tool, `nm_v1_workspace_path` tool, projection integration in `AgentWorkspace` | 2 new MCP tools, 1 projection change |
| **16m** | ✅ Output size management: truncation logic in `WorkspaceExecutionService`, `MaxOutputBytes`/`TruncationMode` options, scrollable `<pre>` UI, download endpoint, `Truncated`/`NodeId` domain fields, attach exec results on pass | Complete — 142 tests pass |

---

## Verification Checklist

- [x] `BranchExecutionResult` domain types compile and serialize/deserialize correctly
- [x] Auto-detection correctly identifies build systems in temp directories with known files
- [x] Single-branch execution runs commands and captures exit codes, stdout, stderr
- [x] Command timeout fires after configured seconds without hanging
- [x] Composite execution merges files from 2+ branches and runs commands on the composite
- [x] Composite execution cleans up temp directories after completion
- [x] MCP tools return structured results matching domain types
- [x] REST endpoints match MCP tools feature-for-feature
- [x] `WorkspaceExecutionRule` is a no-op when `RequireBuildBeforeProposal` and `RequireTestBeforeProposal` are both false
- [x] `WorkspaceExecutionRule` blocks proposals when build fails and `RequireBuildBeforeProposal` is true
- [x] `MergeCommandService.ProposeAsync` attaches execution results to proposal `VerificationResults`
- [x] `MergeReconciliationService` creates composite branch, runs execution, attaches to reconciled proposal
- [x] Merge Review tab renders execution results (build/test status, expandable output)
- [x] Proposal inspector shows execution status badge
- [x] Workspace tab Build/Test buttons trigger execution and show results
- [x] Per-work-unit metadata commands override auto-detection
- [x] Multi-language repos (dotnet + npm, cargo + pytest) run all detected systems
- [x] Output is truncated to `MaxOutputBytes` when exceeding limit, with marker appended (truncation logic in WorkspaceExecutionService)
- [x] Truncation modes (Head, Tail, HeadTail) produce correct results
- [x] Interactive branch access: `GET /studio/workspace/{branchId}/path` returns correct working directory
- [x] `nodalmerge.openBranchInTerminal` opens a terminal at the branch directory
- [x] `nodalmerge.openBranchFolder` opens the branch as a VS Code workspace folder
- [x] Workspace tab [Open Folder] and [Terminal] buttons function correctly
- [x] Execution results persist as `ExecutionResultV1` NodalMerge nodes
- [ ] Execution results survive host restart (rehydration of ExecutionResultV1 nodes — no IRehydratable for execution results yet; descoped, future slice)
- [ ] Execution result expiry cleanup removes results older than 24h not referenced by a proposal (descoped, future slice)
- [x] `nm_v1_workspace_exec_status` returns latest result for a branch
- [x] `nm_v1_workspace_path` returns branch working directory
- [x] `AgentWorkspace` projection includes execution field at Compact and Full levels
- [x] Agent loops' tool schemas include `nm_v1_workspace_exec_status`
- [x] Merge Review UI renders scrollable `<pre>` for build/test output, download link when truncated (16m)
- [x] REST endpoint `GET /studio/workspace/{branchId}/exec/{resultId}/output` for downloading execution output (16m)
- [x] `BranchExecutionResult.NodeId` field to correlate results with persisted nodes for download (16m)
- [x] Truncation detection: `BuildResult.Truncated` and `TestResult.Truncated` computed from output (16m)
- [x] All existing tests pass with policy gate disabled (backward compatibility)

**Phase 6.6 complete.** 142 tests pass, 0 failures. Two items descoped (rehydration of ExecutionResultV1 and expiry cleanup) — neither blocks any current feature; both are future enhancements that require an IRehydratable for the execution result store.
