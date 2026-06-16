using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class AgentWorkspaceService : IAgentWorkspaceService
{
    private readonly ConcurrentDictionary<string, AgentWorkspace> _workspaces = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IServiceProvider _serviceProvider;
    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly IExecutionEventStream _events;

    // IWorkUnitService is resolved lazily (via IServiceProvider) rather than constructor-injected:
    // its production implementation depends on IAgentControlService, which depends on IWorkScheduler,
    // which depends on this service — a direct dependency here would be a circular constructor graph.
    public AgentWorkspaceService(
        IStudioNodeStore nodeStore,
        IServiceProvider serviceProvider,
        IFileWorkspaceService fileWorkspace,
        IExecutionEventStream events)
    {
        _nodeStore       = nodeStore;
        _serviceProvider = serviceProvider;
        _fileWorkspace   = fileWorkspace;
        _events          = events;
    }

    public async Task<AgentWorkspace> CreateAsync(
        string workUnitId, string baseBranch, string? sessionId = null, CancellationToken ct = default)
    {
        var workspaceId = WorkspaceId(workUnitId);

        // Idempotent: keyed by WorkUnitId (derived 1:1 into workspaceId).
        if (_workspaces.TryGetValue(workspaceId, out var existing))
            return existing;

        // The work unit's own branch (created at WorkUnit creation time) is the isolation
        // boundary we wrap rather than forking a second branch — see WorkspaceBackingModel.
        var workUnits = _serviceProvider.GetRequiredService<IWorkUnitService>();
        var workUnit = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        var branchName = workUnit?.BranchId ?? workUnitId;
        var path = await _fileWorkspace.GetWorkingDirectoryAsync(branchName, ct).ConfigureAwait(false);

        var workspace = new AgentWorkspace(
            WorkspaceId:  workspaceId,
            WorkUnitId:   workUnitId,
            BackingModel: WorkspaceBackingModel.LogicalBranchWorkspace,
            BranchName:   branchName,
            BaseBranch:   baseBranch,
            Path:         path,
            CreatedAt:    DateTimeOffset.UtcNow,
            DestroyedAt:  null);

        _workspaces[workspaceId] = workspace;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.AgentWorkspaceV1, workspaceId,
            JsonSerializer.Serialize(workspace), ct).ConfigureAwait(false);

        if (sessionId is not null)
        {
            await _events.AppendAsync(
                sessionId, workUnitId, ExecutionEventKind.WorkspaceCreated,
                new WorkspaceCreatedPayload(workspaceId, workUnitId, branchName, baseBranch),
                ct: ct).ConfigureAwait(false);
        }

        return workspace;
    }

    public Task<AgentWorkspace?> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        _workspaces.TryGetValue(workspaceId, out var workspace);
        return Task.FromResult(workspace);
    }

    public async Task ArchiveAsync(string workspaceId, string? sessionId = null, CancellationToken ct = default)
    {
        if (!_workspaces.TryGetValue(workspaceId, out var workspace) || workspace.DestroyedAt is not null)
            return;

        var archived = workspace with { DestroyedAt = DateTimeOffset.UtcNow };
        _workspaces[workspaceId] = archived;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.AgentWorkspaceV1, workspaceId,
            JsonSerializer.Serialize(archived), ct).ConfigureAwait(false);

        if (sessionId is not null)
        {
            await _events.AppendAsync(
                sessionId, workspace.WorkUnitId, ExecutionEventKind.WorkspaceArchived,
                new WorkspaceArchivedPayload(workspaceId, workspace.BranchName),
                ct: ct).ConfigureAwait(false);
        }
    }

    public async Task DestroyAsync(
        string workspaceId, string? reason = null, string? sessionId = null, CancellationToken ct = default)
    {
        if (!_workspaces.TryGetValue(workspaceId, out var workspace) || workspace.DestroyedAt is not null)
            return;

        // Unlike a disposable worktree, the underlying branch directory is shared with the rest
        // of the system (diffing, inspection tools) — destroy marks the workspace finalized but
        // does not delete files, so a failed run stays inspectable.
        var destroyed = workspace with { DestroyedAt = DateTimeOffset.UtcNow };
        _workspaces[workspaceId] = destroyed;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.AgentWorkspaceV1, workspaceId,
            JsonSerializer.Serialize(destroyed), ct).ConfigureAwait(false);

        if (sessionId is not null)
        {
            await _events.AppendAsync(
                sessionId, workspace.WorkUnitId, ExecutionEventKind.WorkspaceDestroyed,
                new WorkspaceDestroyedPayload(workspaceId, reason ?? "Work unit failed"),
                ct: ct).ConfigureAwait(false);
        }
    }

    public Task<bool> ValidateWriteAsync(
        string workUnitId, string path, IReadOnlyList<string> fileScope, CancellationToken ct = default)
    {
        if (fileScope.Count == 0)
            return Task.FromResult(true);

        var normalized = path.Replace('\\', '/');
        var allowed = fileScope.Any(pattern => MatchesGlob(pattern.Replace('\\', '/'), normalized));
        return Task.FromResult(allowed);
    }

    private static string WorkspaceId(string workUnitId) => $"ws-{workUnitId}";

    private static bool MatchesGlob(string pattern, string path)
    {
        var regexPattern = "^" + Regex.Escape(pattern)
            .Replace(@"\*\*", ".*")
            .Replace(@"\*", "[^/]*")
            + "$";
        return Regex.IsMatch(path, regexPattern, RegexOptions.IgnoreCase);
    }
}
