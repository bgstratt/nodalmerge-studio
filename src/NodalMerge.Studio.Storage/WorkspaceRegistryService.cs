using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IWorkspaceRegistryService
{
    /// <summary>
    /// Returns the single workspace for this Studio instance, creating it on first call.
    /// Idempotent — every subsequent call returns the same WorkspaceId. Cardinality is 1 per
    /// instance today (see plans/phase-16-workspace-aggregate.md); this is the only creation path.
    /// </summary>
    Task<WorkspaceV1> GetOrCreateDefaultAsync(CancellationToken ct = default);

    Task<WorkspaceV1?> GetAsync(string workspaceId, CancellationToken ct = default);

    /// <summary>
    /// Records that <paramref name="repositoryId"/> belongs to the default workspace. Idempotent —
    /// attaching an already-attached repository is a no-op.
    /// </summary>
    Task<WorkspaceV1> AttachRepositoryAsync(string repositoryId, CancellationToken ct = default);
}

public sealed class WorkspaceRegistryService : IWorkspaceRegistryService, IRehydratable
{
    public const string DefaultWorkspaceId = "workspace-default";

    private readonly ConcurrentDictionary<string, WorkspaceV1> _workspaces = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceRegistryService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<WorkspaceV1> GetOrCreateDefaultAsync(CancellationToken ct = default)
    {
        if (_workspaces.TryGetValue(DefaultWorkspaceId, out var existing))
            return existing;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_workspaces.TryGetValue(DefaultWorkspaceId, out existing))
                return existing;

            var workspace = new WorkspaceV1(
                WorkspaceId: DefaultWorkspaceId,
                Name: "Default",
                CreatedAt: DateTimeOffset.UtcNow,
                RepositoryIds: []);

            await PersistAsync(workspace, ct).ConfigureAwait(false);
            return workspace;
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task<WorkspaceV1?> GetAsync(string workspaceId, CancellationToken ct = default)
    {
        _workspaces.TryGetValue(workspaceId, out var workspace);
        return Task.FromResult(workspace);
    }

    public async Task<WorkspaceV1> AttachRepositoryAsync(string repositoryId, CancellationToken ct = default)
    {
        var workspace = await GetOrCreateDefaultAsync(ct).ConfigureAwait(false);
        if (workspace.RepositoryIds.Contains(repositoryId))
            return workspace;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            workspace = _workspaces[DefaultWorkspaceId];
            if (workspace.RepositoryIds.Contains(repositoryId))
                return workspace;

            var updated = workspace with { RepositoryIds = [..workspace.RepositoryIds, repositoryId] };
            await PersistAsync(updated, ct).ConfigureAwait(false);
            return updated;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PersistAsync(WorkspaceV1 workspace, CancellationToken ct)
    {
        _workspaces[workspace.WorkspaceId] = workspace;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkspaceV1, workspace.WorkspaceId,
            JsonSerializer.Serialize(workspace), ct).ConfigureAwait(false);
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore
            .ReadAllNodesAsync(StudioNodeKind.WorkspaceV1, cancellationToken).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var workspace = JsonSerializer.Deserialize<WorkspaceV1>(payloadJson);
            if (workspace is not null)
                _workspaces[workspace.WorkspaceId] = workspace;
        }
    }
}
