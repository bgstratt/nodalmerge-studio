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
    IRepositoryRegistryService repositories,
    ISnapshotTreeResolver treeResolver,
    ISnapshotRetentionPolicy retentionPolicy,
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

        var repositoryId = await GetRepositoryIdAsync(workUnit, ct).ConfigureAwait(false);
        var snapshot     = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot is null) return false;
        var tree = await treeResolver.ResolveTreeAsync(snapshot, ct).ConfigureAwait(false);
        if (tree is null) return false;

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

    // Phase 5 slice 5.2 — LiveHashSource v2. Live = union of (a) trees+blobs reachable from every
    // snapshot in the retention policy's RetainedSnapshotIds (Pinned union Active union
    // unexpired-Intermediate — see ISnapshotRetentionPolicy), (b) blobs referenced by ops not yet
    // compacted into a snapshot (unchanged from v1), (c) admin pins. (c) needs no separate walk
    // here: an admin-pinned SnapshotId is classified Pinned by the policy itself, so it is already
    // inside RetainedSnapshotIds and gets walked via (a) — see SnapshotRetentionPolicy's own admin
    // pin handling. A snapshot NOT in the retained set is simply not walked at all — its unique
    // blobs/trees fall out of the live set and become sweep candidates, which is the entire point
    // of this slice (v1's rule was "every stored snapshot", i.e. permanently zero reclamation).
    //
    // FAIL CLOSED, UNCHANGED CONTRACT: any resolution failure on a *retained* snapshot still
    // throws rather than silently under-report — this now also covers a retained snapshot's own
    // node failing to deserialize (previously silently skipped for every snapshot regardless of
    // retention; tightened here because SnapshotRetentionPolicy's own fail-safe bias already
    // classifies an unparseable snapshot node Active, i.e. retained — so if it lands in
    // RetainedSnapshotIds, this method must not silently treat it as if it contributed zero
    // blobs). A non-retained malformed node is skipped exactly like a non-retained snapshot that
    // parsed cleanly: it isn't walked, consistent with "not in the retained set" either way.
    // GetReachableHashesAsync's own fail-closed exception (CAS miss / corrupt tree object anywhere
    // in the walk) is unchanged and still let to propagate as-is.
    public async Task<IReadOnlySet<string>> GetLiveBlobHashesAsync(CancellationToken ct = default)
    {
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var report = await retentionPolicy.ClassifyAsync(ct).ConfigureAwait(false);
        var retainedIds = report.RetainedSnapshotIds;

        var snapshotNodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.RepositorySnapshotV1, ct)
            .ConfigureAwait(false);
        foreach (var (entityId, json) in snapshotNodes)
        {
            RepositorySnapshot? snapshot;
            try
            {
                snapshot = JsonSerializer.Deserialize<RepositorySnapshot>(json);
            }
            catch (Exception ex)
            {
                if (retainedIds.Contains(entityId))
                {
                    throw new InvalidOperationException(
                        $"Live blob set computation aborted: retained snapshot '{entityId}' failed to " +
                        "deserialize (malformed JSON) — its live bytes cannot be verified, so a GC sweep " +
                        "must not run against a partial live set this round.", ex);
                }
                continue; // not retained — not walked, same as any other non-retained snapshot
            }

            if (snapshot is null)
            {
                if (retainedIds.Contains(entityId))
                {
                    throw new InvalidOperationException(
                        $"Live blob set computation aborted: retained snapshot '{entityId}' deserialized " +
                        "to null — its live bytes cannot be verified, so a GC sweep must not run against " +
                        "a partial live set this round.");
                }
                continue;
            }

            if (!retainedIds.Contains(snapshot.SnapshotId)) continue; // aged out — a sweep candidate, not walked

            if (snapshot.TreeEntries is not null)
            {
                foreach (var blobId in snapshot.TreeEntries.Values)
                    live.Add(blobId);
                continue;
            }

            if (string.Equals(snapshot.TreeFormat, "cas-tree", StringComparison.Ordinal))
            {
                // Slice 1.3 — GetReachableHashesAsync already implements the exact fail-closed
                // contract this method needs (throws naming the snapshot + missing hash rather than
                // returning a partial set), so it's let to propagate as-is rather than re-wrapped.
                var reachable = await treeResolver.GetReachableHashesAsync(snapshot, ct).ConfigureAwait(false);
                foreach (var hash in reachable) live.Add(hash);
            }
            // else: pre-Phase-2 legacy snapshot (neither TreeEntries nor TreeFormat) — nothing to add.
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

    // A work unit is safe to evict when its merged content is captured in a repo snapshot. When a
    // proposal for this work unit was applied, the apply records the snapshot that captured its own
    // write-back (appliedSnapshotId metadata) — a direct, race-free proof the content is persisted,
    // so it's evictable regardless of timestamp ordering. The apply-time resync stamps that snapshot
    // BEFORE the unit's own status/UpdatedAt bump, so the CreatedAt-postdates-UpdatedAt heuristic
    // below can never hold for a freshly-applied real-repo unit — it survives only as the fallback
    // for units with no recorded applied snapshot (resync failed, no snapshot service, or a unit
    // whose changes reached the repo via a later between-run sync instead). The applied-snapshot
    // signal is deliberately not gated on WorkUnitStatus.Merged: the metadata is only ever set at
    // apply time, and the eviction sweep (EvictOrphanedAsync) already restricts itself to terminal
    // statuses, so on-demand eviction of an applied unit stays correct even if the caller drove the
    // apply without advancing the unit's own status machine.
    private async Task<bool> PassesSafeEvictionInvariantAsync(WorkUnit wu, CancellationToken ct)
    {
        if (wu.Metadata is { } metadata
            && metadata.TryGetValue("appliedSnapshotId", out var appliedSnapshotId)
            && !string.IsNullOrEmpty(appliedSnapshotId))
        {
            return true;
        }

        var repositoryId = await GetRepositoryIdAsync(wu, ct).ConfigureAwait(false);
        var snapshot = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot is null) return false;
        var tree = await treeResolver.ResolveTreeAsync(snapshot, ct).ConfigureAwait(false);
        if (tree is null) return false;
        return snapshot.CreatedAt > wu.UpdatedAt;
    }

    private async Task<bool> DeleteBranchDirAsync(string branchId, CancellationToken ct)
    {
        var dir = await fileWorkspace.GetWorkingDirectoryAsync(branchId, ct).ConfigureAwait(false);
        if (dir is null || !Directory.Exists(dir)) return false;

        Directory.Delete(dir, recursive: true);
        return true;
    }

    // Prefers the work unit's own registered repository (multi-repo goals) over the global
    // default — matches the snapshot store's actual key convention (Path.GetFullPath of the
    // physical repo path, not the registry's opaque RepositoryId) used everywhere else
    // (RepositorySyncService, FileSystemWorkspaceService). Falls back to today's global-default
    // behavior when RepositoryId is null or the registry lookup misses (stale/deleted entry) —
    // eviction should degrade safely, not throw.
    private async Task<string> GetRepositoryIdAsync(WorkUnit workUnit, CancellationToken ct)
    {
        if (workUnit.RepositoryId is { } repositoryId)
        {
            var repository = await repositories.GetAsync(repositoryId, ct).ConfigureAwait(false);
            if (repository is not null)
                return Path.GetFullPath(repository.Path);
        }

        return Path.GetFullPath(options?.SeedRepositoryPath ?? Directory.GetCurrentDirectory());
    }

    private IWorkUnitService GetWorkUnitService() =>
        serviceProvider.GetRequiredService<IWorkUnitService>();

    private async Task<WorkUnit?> GetWorkUnitAsync(string workUnitId, CancellationToken ct)
    {
        return await GetWorkUnitService().GetAsync(workUnitId, ct).ConfigureAwait(false);
    }
}
