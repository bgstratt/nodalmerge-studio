using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class WorkspaceTools(
    IWorkspaceService workspace,
    IWorkspaceExecutionCommandService executionCommand,
    IWorkspaceProfileService workspaceProfiles,
    IWorkspaceSemanticNavigationService semanticNavigation,
    IRepositorySyncService repositorySync,
    IRepositoryRegistryService repositories,
    WorkspaceOptions workspaceOptions)
{
    // ── Existing ──────────────────────────────────────────────────────────

    [McpServerTool(Name = McpToolNames.WorkspaceSummary), Description("Get workspace summary for control tower UIs, including the managed root path and seed repository path — call this before assuming a directory exists or attempting any operation outside your current branch.")]
    public async Task<string> SummaryAsync(string? branchId = null, CancellationToken cancellationToken = default)
    {
        var summary = await workspace.GetSummaryAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(summary);
    }

    [McpServerTool(Name = McpToolNames.WorkspaceStatus), Description("Get a concise workspace status view with changed files and proposal summaries.")]
    public async Task<string> StatusAsync(
        [Description("Optional branch ID filter.")] string? branchId = null,
        [Description("Optional work unit ID to resolve the authoritative branch and current proposal chain.")] string? workUnitId = null,
        [Description("Maximum changed-file entries to return.")] int limit = 50,
        [Description("Changed-file page offset.")] int offset = 0,
        CancellationToken cancellationToken = default)
    {
        var status = await workspace.GetStatusAsync(branchId, workUnitId, limit, offset, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(status);
    }

    // ── Phase 16 — structured capabilities snapshot ───────────────────────

    [McpServerTool(Name = McpToolNames.WorkspaceCapabilities), Description("Get a structured snapshot of what this workspace can do (build/test/run/git/create-repository/import-repository/doc-fetch/enabled domain agents) — resolved fresh, never persisted.")]
    public string Capabilities() => McpJson.Ok(WorkspaceCapabilityResolver.Resolve(workspaceOptions));

    // ── Multi-repo Phase 2 — deliberate workspace switch ──────────────────

    [McpServerTool(Name = McpToolNames.WorkspaceSwitch), Description("Explicitly switch the active workspace to a different repository — re-syncs the branch (default \"main\") from the given repository and sets it as the new default for goals that don't specify their own repository. Either repositoryId or repositoryPath is required.")]
    public async Task<string> SwitchAsync(
        [Description("A previously registered repository's id. Takes priority over repositoryPath when both are given.")] string? repositoryId = null,
        [Description("A filesystem path to switch to. Ignored if repositoryId is given.")] string? repositoryPath = null,
        [Description("The branch to re-sync. Defaults to \"main\".")] string? branchId = null,
        CancellationToken cancellationToken = default)
    {
        var path = repositoryPath;
        if (repositoryId is not null)
        {
            var repository = await repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Repository '{repositoryId}' was not found.");
            path = repository.Path;
        }

        if (string.IsNullOrWhiteSpace(path))
            return McpJson.Error(McpToolNames.WorkspaceSwitch, "Either repositoryId or repositoryPath is required.");

        var effectiveBranchId = branchId ?? "main";
        var pending = await repositorySync.SyncBranchFromRepositoryAsync(
            effectiveBranchId, path, SyncTrigger.ManualRefresh, cancellationToken).ConfigureAwait(false);
        workspaceOptions.SeedRepositoryPath = path;

        return McpJson.Ok(new { branchId = effectiveBranchId, repositoryPath = path, sync = pending });
    }

    // ── Slice 16d — workspace execution tools ─────────────────────────────

    [McpServerTool(Name = McpToolNames.WorkspaceBuild), Description("Run build on a branch.")]
    public async Task<string> BuildAsync(
        [Description("The branch ID to build.")] string branchId,
        [Description("Optional custom build command. If not provided, auto-detects.")] string? buildCommand = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 300,
        CancellationToken cancellationToken = default,
        [Description("Limit auto-detected build to one WorkspaceProfile root (ProjectRoot.RelativePath). Ignored if buildCommand is set.")] string? rootPath = null)
    {
        var result = await executionCommand.BuildAsync(branchId, buildCommand, timeoutSeconds, cancellationToken, rootPath).ConfigureAwait(false);
        return McpJson.Ok(result);
    }

    [McpServerTool(Name = McpToolNames.WorkspaceTest), Description("Run tests on a branch.")]
    public async Task<string> TestAsync(
        [Description("The branch ID to test.")] string branchId,
        [Description("Optional custom test command. If not provided, auto-detects.")] string? testCommand = null,
        [Description("Timeout in seconds.")] int timeoutSeconds = 300,
        CancellationToken cancellationToken = default,
        [Description("Limit auto-detected test run to one WorkspaceProfile root (ProjectRoot.RelativePath). Ignored if testCommand is set.")] string? rootPath = null)
    {
        var result = await executionCommand.TestAsync(branchId, testCommand, timeoutSeconds, cancellationToken, rootPath).ConfigureAwait(false);
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

    [McpServerTool(Name = McpToolNames.WorkspaceRun), Description("Run the application in the branch. With no rootPath/runCommand, runs every detected project root that has a resolved run command — long-running roots (dev servers) start detached and keep running; use workspace_run_stop to stop them.")]
    public async Task<string> RunAsync(
        [Description("The branch ID to run.")] string branchId,
        [Description("Restrict to one detected project root (its RelativePath from the workspace profile). Omit to run every root with a resolved run command.")] string? rootPath = null,
        [Description("Custom run command, overriding auto-detection. Always runs once (blocking), since long-running-ness can't be inferred for an arbitrary command.")] string? runCommand = null,
        [Description("Timeout in seconds. Ignored for long-running roots, which never time out.")] int timeoutSeconds = 120,
        CancellationToken cancellationToken = default)
    {
        var result = await executionCommand.RunAsync(branchId, rootPath, runCommand, timeoutSeconds, environmentVariables: null, ct: cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(result);
    }

    [McpServerTool(Name = McpToolNames.WorkspaceRunStop), Description("Stop long-running process(es) started by workspace_run for a branch.")]
    public async Task<string> RunStopAsync(
        [Description("The branch ID.")] string branchId,
        [Description("Stop only this process id. Omit to stop every tracked process for the branch (optionally narrowed by rootPath).")] int? pid = null,
        [Description("Stop only the process for this project root. Omit to match any root.")] string? rootPath = null,
        CancellationToken cancellationToken = default)
    {
        var stopped = await executionCommand.StopAsync(branchId, pid, rootPath, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { stopped });
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

    // ── Phase 9d — workspace profile tools ────────────────────────────────

    [McpServerTool(Name = McpToolNames.WorkspaceProfileGet), Description("Get the detected project roots for a branch (paths, stacks, resolved build/test/run commands). Cached — use workspace_profile_rescan after adding a new sub-project.")]
    public async Task<string> ProfileGetAsync(
        [Description("The branch ID.")] string branchId,
        CancellationToken cancellationToken = default)
    {
        var profile = await workspaceProfiles.GetOrDetectAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(profile);
    }

    [McpServerTool(Name = McpToolNames.WorkspaceProfileRescan), Description("Force re-detection of a branch's project roots, bypassing the cache. Call this after creating a new sub-project (e.g. a new package.json) so later build/test/run calls see it.")]
    public async Task<string> ProfileRescanAsync(
        [Description("The branch ID.")] string branchId,
        CancellationToken cancellationToken = default)
    {
        var profile = await workspaceProfiles.RescanAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(profile);
    }

    // ── Phase 15b — semantic navigation tools ───────────────────────────

    [McpServerTool(Name = McpToolNames.WorkspaceSymbolDefinition), Description("Find symbol definition locations in a branch using compiler-backed semantic navigation.")]
    public async Task<string> SymbolDefinitionAsync(
        [Description("The branch ID.")] string branchId,
        [Description("Symbol name to resolve (optional when path+line are supplied). Example: IUserRepository")] string? symbol = null,
        [Description("Relative file path to resolve a symbol at a location (optional).") ] string? path = null,
        [Description("1-based line number for path-based lookup (optional).") ] int? line = null,
        [Description("1-based column number for path-based lookup (optional).") ] int? column = null,
        [Description("Maximum results to return.")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        var query = new WorkspaceSymbolQuery(symbol, path, line, column, maxResults);
        var (locations, truncated) = await semanticNavigation.FindDefinitionsAsync(branchId, query, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { locations, truncated, branchId });
    }

    [McpServerTool(Name = McpToolNames.WorkspaceSymbolReferences), Description("Find symbol reference locations in a branch using compiler-backed semantic navigation.")]
    public async Task<string> SymbolReferencesAsync(
        [Description("The branch ID.")] string branchId,
        [Description("Symbol name to resolve (optional when path+line are supplied). Example: IUserRepository")] string? symbol = null,
        [Description("Relative file path to resolve a symbol at a location (optional).") ] string? path = null,
        [Description("1-based line number for path-based lookup (optional).") ] int? line = null,
        [Description("1-based column number for path-based lookup (optional).") ] int? column = null,
        [Description("Maximum results to return.")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        var query = new WorkspaceSymbolQuery(symbol, path, line, column, maxResults);
        var (locations, truncated) = await semanticNavigation.FindReferencesAsync(branchId, query, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { locations, truncated, branchId });
    }

    [McpServerTool(Name = McpToolNames.WorkspaceSymbolImplementation), Description("Find symbol implementation locations in a branch using compiler-backed semantic navigation.")]
    public async Task<string> SymbolImplementationAsync(
        [Description("The branch ID.")] string branchId,
        [Description("Symbol name to resolve (optional when path+line are supplied). Example: IUserRepository")] string? symbol = null,
        [Description("Relative file path to resolve a symbol at a location (optional).") ] string? path = null,
        [Description("1-based line number for path-based lookup (optional).") ] int? line = null,
        [Description("1-based column number for path-based lookup (optional).") ] int? column = null,
        [Description("Maximum results to return.")] int maxResults = 200,
        CancellationToken cancellationToken = default)
    {
        var query = new WorkspaceSymbolQuery(symbol, path, line, column, maxResults);
        var (locations, truncated) = await semanticNavigation.FindImplementationsAsync(branchId, query, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { locations, truncated, branchId });
    }
}