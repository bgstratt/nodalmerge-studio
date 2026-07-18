using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

/// <summary>
/// plans/first-class-goals-and-materialization.md Phase 4 — mints the "produced state" a work unit
/// reached, as a RepositorySnapshot attributed with the work unit's id, so it can be materialized to
/// a folder later (POST /studio/repository-snapshots/{id}/materialize). Fires when a work unit reaches
/// Proposed — the moment its branch dir is final and still guaranteed live (a Merged work unit's
/// branch dir is a candidate for cache eviction). Purely additive: subscribes to the participant bus
/// like LocalFilesystemProjectionMaterializer, so it never edits the work-unit or merge critical path,
/// and every failure is swallowed + logged (a missing produced snapshot only disables that one
/// checkout, it never breaks the run).
/// </summary>
public sealed class WorkUnitProducedSnapshotObserver : IHostedService, IDisposable
{
    public const string SnapshotSource = "WorkUnitCompletion";

    private readonly IWorkUnitService _workUnits;
    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly IRepositorySnapshotService _snapshots;
    private readonly IParticipantEventBus _eventBus;
    private readonly IBlobStoreProvider? _blobStore;
    private readonly IRepositoryRegistryService? _repositories;
    private readonly ILogger<WorkUnitProducedSnapshotObserver> _logger;
    private IDisposable? _subscription;

    public WorkUnitProducedSnapshotObserver(
        IWorkUnitService workUnits,
        IFileWorkspaceService fileWorkspace,
        IRepositorySnapshotService snapshots,
        IParticipantEventBus eventBus,
        ILogger<WorkUnitProducedSnapshotObserver> logger,
        IBlobStoreProvider? blobStore = null,
        IRepositoryRegistryService? repositories = null)
    {
        _workUnits = workUnits;
        _fileWorkspace = fileWorkspace;
        _snapshots = snapshots;
        _eventBus = eventBus;
        _blobStore = blobStore;
        _repositories = repositories;
        _logger = logger;
    }

    // Subscribe in StartAsync (not the ctor) so this only runs when force-instantiated as a hosted
    // service — the same eager-startup contract WorkspaceCacheManager uses.
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = _eventBus.Subscribe("WorkUnitStatusChanged", HandleAsync);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _subscription?.Dispose();
        _subscription = null;
        return Task.CompletedTask;
    }

    private async Task HandleAsync(IDomainEvent domainEvent)
    {
        if (domainEvent is not WorkUnitStatusChangedEvent { NewStatus: WorkUnitStatus.Proposed } ev)
            return;
        // No CAS configured → the file blobs a materialize would pull don't exist anywhere, so there's
        // nothing worth minting. WsOnly/no-blob-store deployments simply don't get produced snapshots.
        if (_blobStore is null)
            return;

        try
        {
            await MintAsync(ev.WorkUnitId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "[produced-snapshot] failed to mint a WorkUnitCompletion snapshot for work unit {WorkUnitId}",
                ev.WorkUnitId);
        }
    }

    private async Task MintAsync(string workUnitId)
    {
        var wu = await _workUnits.GetAsync(workUnitId).ConfigureAwait(false);
        if (wu is null)
            return;

        // Resolve the CAS repository identity (physical-path id space) + base lineage the produced
        // snapshot belongs to. Prefer the seed snapshot (it pins both), but fall back to resolving the
        // repo's CAS identity directly so a work unit whose repo hasn't minted a base snapshot yet
        // still gets a produced snapshot (generation 0, no base).
        string? casRepoId = null;
        string? baseSnapshotId = null;
        if (!string.IsNullOrEmpty(wu.SeedSnapshotId))
        {
            var baseSnapshot = await _snapshots.GetAsync(wu.SeedSnapshotId).ConfigureAwait(false);
            if (baseSnapshot is not null)
            {
                casRepoId = baseSnapshot.RepositoryId;
                baseSnapshotId = wu.SeedSnapshotId;
            }
        }
        if (casRepoId is null && _repositories is not null)
        {
            casRepoId = await _repositories.ResolveCasIdentityAsync(wu.RepositoryId).ConfigureAwait(false);
        }
        if (string.IsNullOrEmpty(casRepoId))
            return; // can't attribute the snapshot to a repository → skip

        var tree = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in await _fileWorkspace.ListAsync(wu.BranchId).ConfigureAwait(false))
        {
            var content = await _fileWorkspace.ReadAsync(wu.BranchId, path).ConfigureAwait(false);
            if (content is null)
                continue;

            var bytes = Encoding.UTF8.GetBytes(content);
            var blobId = BlobHasher.ComputeHash(bytes);
            // Idempotent by hash — guarantees the tree's referenced blob is retrievable at materialize
            // time regardless of how the branch write originally stored it.
            await _blobStore!.PutBlobAsync(blobId, bytes, "text/plain").ConfigureAwait(false);
            tree[path] = blobId;
        }

        await _snapshots.CreateAsync(
            casRepoId, tree,
            baseSnapshotId: baseSnapshotId,
            workUnitId: wu.WorkUnitId,
            source: SnapshotSource).ConfigureAwait(false);
    }

    public void Dispose()
    {
        _subscription?.Dispose();
        _subscription = null;
    }
}
