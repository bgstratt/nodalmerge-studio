using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 8 — branch workspace directories are ephemeral cache entries.
// Any evicted directory can be reconstructed from the latest repository snapshot + CAS.
// IWorkUnitService is resolved lazily via IServiceProvider to avoid a circular constructor graph
// (same pattern as AgentWorkspaceService).
public sealed class WorkspaceCacheManager(
    IFileWorkspaceService fileWorkspace,
    IServiceProvider serviceProvider,
    IRepositorySnapshotService snapshotService,
    IMaterializationEngine materializer,
    IStudioNodeStore nodeStore,
    WorkspaceOptions? options = null) : IWorkspaceCacheManager, IHostedService
{
    private static readonly HashSet<WorkUnitStatus> TerminalEvictableStatuses =
    [
        WorkUnitStatus.Completed,
        WorkUnitStatus.Merged,
        WorkUnitStatus.Cancelled,
    ];

    // IHostedService.StartAsync — fire-and-forget orphan sweep so startup is not blocked.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = Task.Run(() => EvictOrphanedAsync(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // ── IWorkspaceCacheManager ─────────────────────────────────────────────────

    public async Task<bool> MaterializeAsync(string workUnitId, CancellationToken ct = default)
    {
        var workUnit = await GetWorkUnitAsync(workUnitId, ct).ConfigureAwait(false);
        if (workUnit is null) return false;

        var repositoryId = GetRepositoryId();
        var snapshot     = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot?.TreeEntries is null) return false;

        var branchDir = await fileWorkspace.GetWorkingDirectoryAsync(workUnit.BranchId, ct).ConfigureAwait(false);
        if (branchDir is null) return false;

        await materializer.MaterializeAsync(snapshot, branchDir, ct: ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> EvictAsync(string workUnitId, CancellationToken ct = default)
    {
        var workUnit = await GetWorkUnitAsync(workUnitId, ct).ConfigureAwait(false);
        if (workUnit is null) return false;

        // Cancelled work units: always safe — their changes were never merged.
        if (workUnit.Status != WorkUnitStatus.Cancelled)
        {
            if (!await PassesSafeEvictionInvariantAsync(workUnit, ct).ConfigureAwait(false))
                return false;
        }

        return await DeleteBranchDirAsync(workUnit.BranchId, ct).ConfigureAwait(false);
    }

    public async Task<int> EvictOrphanedAsync(CancellationToken ct = default)
    {
        var workUnits = await GetWorkUnitService().ListAsync(null, ct).ConfigureAwait(false);
        var evicted = 0;

        foreach (var wu in workUnits)
        {
            if (!TerminalEvictableStatuses.Contains(wu.Status)) continue;

            try
            {
                if (wu.Status == WorkUnitStatus.Cancelled)
                {
                    if (await DeleteBranchDirAsync(wu.BranchId, ct).ConfigureAwait(false))
                        evicted++;
                }
                else if (await PassesSafeEvictionInvariantAsync(wu, ct).ConfigureAwait(false))
                {
                    if (await DeleteBranchDirAsync(wu.BranchId, ct).ConfigureAwait(false))
                        evicted++;
                }
            }
            catch
            {
                // best-effort sweep; a single failure must not halt the rest
            }
        }

        return evicted;
    }

    public async Task<IReadOnlySet<string>> GetLiveBlobHashesAsync(CancellationToken ct = default)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Collect all blob hashes from every stored snapshot's TreeEntries.
        var snapshotNodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.RepositorySnapshotV1, ct)
            .ConfigureAwait(false);
        foreach (var (_, json) in snapshotNodes)
        {
            try
            {
                var snapshot = JsonSerializer.Deserialize<RepositorySnapshot>(json);
                if (snapshot?.TreeEntries is null) continue;
                foreach (var blobId in snapshot.TreeEntries.Values)
                    live.Add(blobId);
            }
            catch { /* malformed node — skip */ }
        }

        // Also protect blobs referenced by ops that haven't been compacted into a snapshot yet.
        var opNodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.RepositoryOpV1, ct)
            .ConfigureAwait(false);
        foreach (var (_, json) in opNodes)
        {
            try
            {
                var op = JsonSerializer.Deserialize<RepositoryOperation>(json);
                if (op?.NewBlobId is not null) live.Add(op.NewBlobId);
                if (op?.OldBlobId is not null) live.Add(op.OldBlobId);
            }
            catch { /* malformed node — skip */ }
        }

        return live;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // A Completed/Merged work unit is safe to evict when the latest snapshot post-dates its
    // last update — i.e., the between-run sync ran after its changes landed in the seed repo.
    private async Task<bool> PassesSafeEvictionInvariantAsync(WorkUnit wu, CancellationToken ct)
    {
        var repositoryId = GetRepositoryId();
        var snapshot = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot?.TreeEntries is null) return false;
        return snapshot.CreatedAt > wu.UpdatedAt;
    }

    private async Task<bool> DeleteBranchDirAsync(string branchId, CancellationToken ct)
    {
        var dir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false);
        if (dir is null || !Directory.Exists(dir)) return false;

        Directory.Delete(dir, recursive: true);
        return true;
    }

    private string GetRepositoryId() =>
        Path.GetFullPath(options?.SeedRepositoryPath ?? Directory.GetCurrentDirectory());

    private IWorkUnitService GetWorkUnitService() =>
        serviceProvider.GetRequiredService<IWorkUnitService>();

    private async Task<WorkUnit?> GetWorkUnitAsync(string workUnitId, CancellationToken ct)
    {
        return await GetWorkUnitService().GetAsync(workUnitId, ct).ConfigureAwait(false);
    }
}
