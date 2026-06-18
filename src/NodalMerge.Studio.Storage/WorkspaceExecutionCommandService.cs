using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 16c — shared entry point for workspace execution commands — called by both MCP tools
// (WorkspaceTools) and REST endpoints (StudioRestEndpoints) so they cannot drift.
internal sealed class WorkspaceExecutionCommandService(
    IWorkspaceExecutionService execution,
    IFileWorkspaceService fileWorkspace,
    IStudioNodeStore nodeStore) : IWorkspaceExecutionCommandService
{
    public async Task<BranchExecutionResult> BuildAsync(
        string branchId,
        string? buildCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        var request = new WorkspaceExecutionRequest(
            Build: true,
            BuildCommand: buildCommand,
            TimeoutSeconds: timeoutSeconds);
        return await ExecAndPersistAsync(branchId, request, ct).ConfigureAwait(false);
    }

    public async Task<BranchExecutionResult> TestAsync(
        string branchId,
        string? testCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default)
    {
        var request = new WorkspaceExecutionRequest(
            Test: true,
            TestCommand: testCommand,
            TimeoutSeconds: timeoutSeconds);
        return await ExecAndPersistAsync(branchId, request, ct).ConfigureAwait(false);
    }

    public async Task<BranchExecutionResult> ExecAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default) =>
        await ExecAndPersistAsync(branchId, request, ct).ConfigureAwait(false);

    public async Task<BuildResult> RunAsync(
        string branchId,
        string? runCommand = null,
        int timeoutSeconds = 120,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default)
    {
        // For "run", we do a single Build-style execution without test parsing
        var workDir = await GetWorkingDirAsync(branchId, ct).ConfigureAwait(false);

        var command = runCommand ?? "dotnet run";
        var request = new WorkspaceExecutionRequest(
            Build: true,
            BuildCommand: command,
            TimeoutSeconds: timeoutSeconds,
            AllowAutoDetect: runCommand is not null,
            EnvironmentVariables: environmentVariables);

        var result = await execution.ExecuteAsync(branchId, request, ct).ConfigureAwait(false);
        return result.Builds.FirstOrDefault()
            ?? new BuildResult(false, -1, "", "No build results.", null, command,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    }

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