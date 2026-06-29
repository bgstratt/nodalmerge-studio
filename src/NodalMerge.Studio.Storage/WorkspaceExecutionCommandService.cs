using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 16c — shared entry point for workspace execution commands — called by both MCP tools
// (WorkspaceTools) and REST endpoints (StudioRestEndpoints) so they cannot drift.
internal sealed class WorkspaceExecutionCommandService(
    IWorkspaceExecutionService execution,
    IWorkspaceProfileService profiles,
    RunningProcessRegistry runningProcesses,
    IFileWorkspaceService fileWorkspace,
    IStudioNodeStore nodeStore) : IWorkspaceExecutionCommandService
{
    public async Task<BranchExecutionResult> BuildAsync(
        string branchId,
        string? buildCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default,
        string? rootPath = null)
    {
        var request = new WorkspaceExecutionRequest(
            Build: true,
            BuildCommand: buildCommand,
            TimeoutSeconds: timeoutSeconds,
            RootPath: rootPath);
        return await ExecAndPersistAsync(branchId, request, ct).ConfigureAwait(false);
    }

    public async Task<BranchExecutionResult> TestAsync(
        string branchId,
        string? testCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default,
        string? rootPath = null)
    {
        var request = new WorkspaceExecutionRequest(
            Test: true,
            TestCommand: testCommand,
            TimeoutSeconds: timeoutSeconds,
            RootPath: rootPath);
        return await ExecAndPersistAsync(branchId, request, ct).ConfigureAwait(false);
    }

    public async Task<BranchExecutionResult> ExecAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default) =>
        await ExecAndPersistAsync(branchId, request, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<BuildResult>> RunAsync(
        string branchId,
        string? rootPath = null,
        string? runCommand = null,
        int timeoutSeconds = 120,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default)
    {
        var workDir = await GetWorkingDirAsync(branchId, ct).ConfigureAwait(false);

        // Explicit command override: always one-shot — long-running-ness can't be inferred for an
        // arbitrary caller-supplied command, so this keeps the pre-9c blocking behavior exactly.
        if (runCommand is { Length: > 0 })
        {
            var dir = rootPath is { Length: > 0 } ? CombineRoot(workDir, rootPath) : workDir;
            var result = await runningProcesses.RunOnceAsync(
                rootPath ?? "", runCommand, dir, buildSystem: null, timeoutSeconds, environmentVariables, ct)
                .ConfigureAwait(false);
            return [result];
        }

        var profile = await profiles.GetOrDetectAsync(branchId, ct).ConfigureAwait(false);

        IReadOnlyList<ProjectRoot> targets;
        if (rootPath is { Length: > 0 })
        {
            var match = profile.Roots.FirstOrDefault(r => r.RelativePath == rootPath);
            if (match is null)
                return [new BuildResult(false, -1, "", $"No detected root at '{rootPath}'.", null, "",
                    DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProjectRoot: rootPath)];
            targets = [match];
        }
        else
        {
            targets = profile.Roots.Where(r => r.RunCommand is not null).ToList();
        }

        var results = new List<BuildResult>();
        foreach (var root in targets)
        {
            if (root.RunCommand is null)
            {
                results.Add(new BuildResult(false, -1, "", $"Root '{root.RelativePath}' has no resolved run command.",
                    null, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ProjectRoot: root.RelativePath));
                continue;
            }

            var dir = CombineRoot(workDir, root.RelativePath);
            var result = root.IsLongRunning
                ? runningProcesses.StartLongRunning(branchId, root.RelativePath, root.RunCommand, dir, root.Stack, environmentVariables)
                : await runningProcesses.RunOnceAsync(
                    root.RelativePath, root.RunCommand, dir, root.Stack, timeoutSeconds, environmentVariables, ct)
                    .ConfigureAwait(false);
            results.Add(result);
        }
        return results;
    }

    public Task<string?> GetRunOutputAsync(string branchId, string? rootPath = null, CancellationToken ct = default) =>
        Task.FromResult(runningProcesses.GetRunOutput(branchId, rootPath ?? ""));

    public Task<int> StopAsync(
        string branchId,
        int? pid = null,
        string? rootPath = null,
        CancellationToken ct = default) =>
        Task.FromResult(runningProcesses.Stop(branchId, pid, rootPath));

    private static string CombineRoot(string workDir, string relativePath) =>
        relativePath.Length == 0 ? workDir : Path.Combine(workDir, relativePath);

    public async Task<BranchExecutionResult?> GetLatestAsync(
        string branchId,
        CancellationToken ct = default)
    {
        // Find the most recent execution result for this branch
        var allNodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.ExecutionResultV1, ct)
            .ConfigureAwait(false);

        var prefix = $"exec/{branchId}/";
        var latest = allNodes
            .Where(n => n.EntityId.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(n => n.EntityId)
            .FirstOrDefault();

        if (latest == default)
            return null;

        try
        {
            return JsonSerializer.Deserialize<BranchExecutionResult>(latest.PayloadJson);
        }
        catch
        {
            return null;
        }
    }

    public async Task<string?> GetBranchPathAsync(
        string branchId,
        CancellationToken ct = default) =>
        await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false);

    public async Task<ExecutionOutput?> GetOutputAsync(
        string branchId,
        string resultId,
        CancellationToken ct = default)
    {
        var json = await nodeStore.ReadNodeAsync(StudioNodeKind.ExecutionResultV1, resultId, ct)
            .ConfigureAwait(false);
        if (json is null)
            return null;

        try
        {
            var result = JsonSerializer.Deserialize<BranchExecutionResult>(json);
            if (result is null)
                return null;

            var entries = new List<ExecutionOutputEntry>();
            foreach (var build in result.Builds)
                entries.Add(new ExecutionOutputEntry("build", build.BuildSystem, build.Command, build.StdOut, build.StdErr, build.Truncated));
            foreach (var test in result.Tests)
                entries.Add(new ExecutionOutputEntry("test", test.BuildSystem, test.Command, test.StdOut, "", test.Truncated));

            return new ExecutionOutput(result.BranchId, resultId, entries, result.ExecutedAt);
        }
        catch
        {
            return null;
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private async Task<BranchExecutionResult> ExecAndPersistAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct)
    {
        var result = await execution.ExecuteAsync(branchId, request, ct).ConfigureAwait(false);

        // Persist as ExecutionResultV1 node
        var nodeId = $"exec/{branchId}/{DateTimeOffset.UtcNow:yyyyMMddHHmmss}";
        try
        {
            await nodeStore.WriteNodeAsync(
                StudioNodeKind.ExecutionResultV1,
                nodeId,
                JsonSerializer.Serialize(result),
                ct).ConfigureAwait(false);
            result = result with { NodeId = nodeId };
        }
        catch
        {
            // Persistence is best-effort — don't fail the command
        }

        return result;
    }

    private async Task<string> GetWorkingDirAsync(string branchId, CancellationToken ct)
    {
        var workDir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false);
        if (workDir is null)
            throw new InvalidOperationException($"Branch '{branchId}' has no working directory.");
        return workDir;
    }
}