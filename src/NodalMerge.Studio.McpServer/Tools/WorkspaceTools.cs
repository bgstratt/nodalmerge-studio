using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class WorkspaceTools(IWorkspaceService workspace, IWorkspaceExecutionCommandService executionCommand, IFileWorkspaceService fileWorkspace)
{
    // ── Existing ──────────────────────────────────────────────────────────

    [McpServerTool(Name = McpToolNames.WorkspaceSummary), Description("Get workspace summary for control tower UIs.")]
    public async Task<string> SummaryAsync(string? branchId = null, CancellationToken cancellationToken = default)
    {
        var summary = await workspace.GetSummaryAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(summary);
    }

    // ── Slice 16d — workspace execution tools ─────────────────────────────

    [McpServerTool(Name = McpToolNames.WorkspaceBuild), Description("Run build on a branch.")]
    public async Task<string> BuildAsync(
        [Description("The branch ID to build.")] string branchId,
        [Description("Optional custom build command. If not provided, auto-detects.")] string? buildCommand = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        var result = await executionCommand.BuildAsync(branchId, buildCommand, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(result);
    }

    [McpServerTool(Name = McpToolNames.WorkspaceTest), Description("Run tests on a branch.")]
    public async Task<string> TestAsync(
        [Description("The branch ID to test.")] string branchId,
        [Description("Optional custom test command. If not provided, auto-detects.")] string? testCommand = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        var result = await executionCommand.TestAsync(branchId, testCommand, timeoutSeconds, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(result);
    }

    [McpServerTool(Name = McpToolNames.WorkspaceExec), Description("Run build + test + lint on a branch. Supports multi-branch composite execution via sourceBranchIds.")]
    public async Task<string> ExecAsync(
        [Description("The branch ID to execute on.")] string branchId,
        [Description("Whether to run build.")] bool build = true,
        [Description("Whether to run tests.")] bool test = true,
        [Description("Whether to run lint.")] bool lint = false,
        [Description("Custom build command.")] string? buildCommand = null,
        [Description("Custom test command.")] string? testCommand = null,
        [Description("Custom lint command.")] string? lintCommand = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        var request = new WorkspaceExecutionRequest(
            Build: build,
            Test: test,
            Lint: lint,
            BuildCommand: buildCommand,
            TestCommand: testCommand,
            LintCommand: lintCommand,
            TimeoutSeconds: timeoutSeconds);
        var result = await executionCommand.ExecAsync(branchId, request, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(result);
    }

    [McpServerTool(Name = McpToolNames.WorkspaceRun), Description("Run the application in the branch.")]
    public async Task<string> RunAsync(
        [Description("The branch ID to run.")] string branchId,
        [Description("Custom run command. Defaults to 'dotnet run'.")] string? runCommand = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        var result = await executionCommand.RunAsync(branchId, runCommand, timeoutSeconds, environmentVariables: null, ct: cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(result);
    }

    [McpServerTool(Name = McpToolNames.WorkspaceExecStatus), Description("Query latest execution result for a branch.")]
    public async Task<string> ExecStatusAsync(
        [Description("The branch ID to query.")] string branchId,
        CancellationToken cancellationToken = default)
    {
        var result = await executionCommand.GetLatestAsync(branchId, cancellationToken).ConfigureAwait(false);
        return result is not null ? McpJson.Ok(result) : McpJson.Error(McpToolNames.WorkspaceExecStatus, "No execution result found for this branch.");
    }

    [McpServerTool(Name = McpToolNames.WorkspacePath), Description("Get branch working directory path.")]
    public async Task<string> PathAsync(
        [Description("The branch ID.")] string branchId,
        CancellationToken cancellationToken = default)
    {
        var path = await executionCommand.GetBranchPathAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { branchId, workingDirectory = path, exists = path is not null });
    }
}