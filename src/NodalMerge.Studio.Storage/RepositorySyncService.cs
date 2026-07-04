using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class RepositorySyncService : IRepositorySyncService, IRehydratable
{
    private readonly ConcurrentDictionary<string, RepositorySyncState> _states = new();

    // Serializes SyncBranchFromRepositoryAsync per branch so two goals created back-to-back can't
    // race on mutating the same branch — same pattern as FanOutService._parentGates.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();

    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly IKnownGoodStateService _knownGoodState;
    private readonly IArtifactLineageService _artifactLineage;
    private readonly IWorkspaceProfileService _workspaceProfiles;
    private readonly IStudioNodeStore _nodeStore;
    private readonly IRepositoryImportService? _repositoryImport;

    public RepositorySyncService(
        IFileWorkspaceService fileWorkspace,
        IKnownGoodStateService knownGoodState,
        IArtifactLineageService artifactLineage,
        IWorkspaceProfileService workspaceProfiles,
        IStudioNodeStore nodeStore,
        IRepositoryImportService? repositoryImport = null)
    {
        _fileWorkspace = fileWorkspace;
        _knownGoodState = knownGoodState;
        _artifactLineage = artifactLineage;
        _workspaceProfiles = workspaceProfiles;
        _nodeStore = nodeStore;
        _repositoryImport = repositoryImport;
    }

    public async Task<PendingExternalSync?> SyncBranchFromRepositoryAsync(
        string branchId, string repositoryPath, SyncTrigger trigger, CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(branchId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await SyncCoreAsync(branchId, repositoryPath, trigger, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<PendingExternalSync?> SyncCoreAsync(
        string branchId, string repositoryPath, SyncTrigger trigger, CancellationToken ct)
    {
        // Phase 5 — one-time CAS bootstrap: walk the repo, put every file to CAS, emit Import ops,
        // and record the Generation-0 RepositorySnapshot. Fast no-op on subsequent GoalCreation/
        // StartupRecovery calls (a one-time seed is all those need). PostMergeWriteBack and
        // ManualRefresh instead force a fresh Case 1/Case 2 check every time — without this,
        // EnsureBootstrappedAsync's one-time gate means RepositorySnapshot would never advance
        // again after a repo's first goal creation, no matter how many merges land or how many
        // times a resync is explicitly requested.
        if (_repositoryImport is not null)
        {
            var repositoryId = Path.GetFullPath(repositoryPath);
            if (trigger is SyncTrigger.PostMergeWriteBack or SyncTrigger.ManualRefresh)
                await _repositoryImport.ForceSyncAsync(repositoryId, repositoryPath, ct).ConfigureAwait(false);
            else
                await _repositoryImport.EnsureBootstrappedAsync(repositoryId, repositoryPath, ct).ConfigureAwait(false);
        }

        // No-op if main is already populated (today's existing one-time seed mechanism, untouched).
        await _fileWorkspace.InitBranchAsync(branchId, ct: ct).ConfigureAwait(false);

        var state = _states.TryGetValue(branchId, out var existing)
            ? existing
            : new RepositorySyncState(branchId, null, null, 0, null, null, null);

        var diff = await _fileWorkspace.DiffExternalPathAsync(branchId, repositoryPath, ct).ConfigureAwait(false);

        var reason = state.RepositoryPath is null
            ? SyncReason.InitialBootstrap
            : state.RepositoryPath != repositoryPath
                ? SyncReason.RepositorySwitch
                : SyncReason.RepositoryDrift;

        var now = DateTimeOffset.UtcNow;

        // Empty diff: no artifact, no snapshot, regardless of reason. A switch/bootstrap that
        // happens to diff empty still resets identity (generation/chain) so the next real change
        // against this repository starts clean rather than inheriting the prior repo's count.
        if (diff.IsEmpty)
        {
            var noOpState = reason == SyncReason.RepositoryDrift
                ? state with
                {
                    RepositoryPath = repositoryPath,
                    RepositoryFingerprint = diff.ExternalFingerprint,
                    LastSyncedAt = now,
                }
                : state with
                {
                    RepositoryPath = repositoryPath,
                    RepositoryFingerprint = diff.ExternalFingerprint,
                    Generation = 0,
                    LatestExternalChangesetId = null,
                    LastSyncedAt = now,
                    RepositoryIdentityEstablishedAt = now,
                };

            await PersistStateAsync(noOpState, ct).ConfigureAwait(false);
            return null;
        }

        var syncStartedAt = now;

        var preState = await _knownGoodState.MarkKnownGoodAsync(
            new KnownGoodState(
                $"sync-pre-{Guid.NewGuid():N}", branchId,
                "Pre-sync snapshot before importing repository changes", null,
                DateTimeOffset.UtcNow, "system:repository-sync"),
            ct).ConfigureAwait(false);

        await _fileWorkspace.ApplyExternalPathAsync(branchId, repositoryPath, ct).ConfigureAwait(false);

        var postState = await _knownGoodState.MarkKnownGoodAsync(
            new KnownGoodState(
                $"sync-post-{Guid.NewGuid():N}", branchId,
                "Post-sync snapshot after importing repository changes", null,
                DateTimeOffset.UtcNow, "system:repository-sync"),
            ct).ConfigureAwait(false);

        _workspaceProfiles.Invalidate(branchId);

        var syncCompletedAt = DateTimeOffset.UtcNow;

        // Switch/bootstrap reset generation and the chain root rather than continuing the previous
        // repository's count/lineage — "Repo B, generation 47" inherited from Repo A would be
        // actively misleading.
        var generation = reason == SyncReason.RepositoryDrift ? state.Generation + 1 : 1;
        var parentArtifactId = reason == SyncReason.RepositoryDrift ? state.LatestExternalChangesetId : null;
        var identityEstablishedAt = reason == SyncReason.RepositoryDrift
            ? state.RepositoryIdentityEstablishedAt ?? syncCompletedAt
            : syncCompletedAt;

        var artifactId = $"EXT-{Guid.NewGuid():N}";
        var artifact = new ArtifactRef(
            artifactId,
            ArtifactType.ExternalChangeset,
            parentArtifactId,
            ArtifactStatus.Applied,
            syncCompletedAt,
            OwnedByWorkUnitId: null,
            OwnedByAgentId: null,
            Title: $"{reason}: {diff.Added.Count} added, {diff.Modified.Count} modified, {diff.Deleted.Count} deleted (generation {generation})",
            Body: JsonSerializer.Serialize(new
            {
                repositoryPath,
                repositoryFingerprint = diff.ExternalFingerprint,
                reason = reason.ToString(),
                trigger = trigger.ToString(),
                generation,
                syncStartedAt,
                syncCompletedAt,
                added = diff.Added,
                modified = diff.Modified,
                deleted = diff.Deleted,
                preSyncSnapshotStateId = preState.StateId,
                postSyncSnapshotStateId = postState.StateId,
            }));

        await _artifactLineage.RecordAsync(artifact, ct).ConfigureAwait(false);

        var newState = new RepositorySyncState(
            branchId, repositoryPath, diff.ExternalFingerprint, generation, artifactId, syncCompletedAt,
            identityEstablishedAt);
        await PersistStateAsync(newState, ct).ConfigureAwait(false);

        return new PendingExternalSync(artifactId, reason, generation, diff.Added, diff.Modified, diff.Deleted);
    }

    public Task<RepositorySyncState?> GetStateAsync(string branchId, CancellationToken ct = default)
    {
        _states.TryGetValue(branchId, out var state);
        return Task.FromResult<RepositorySyncState?>(state);
    }

    private async Task PersistStateAsync(RepositorySyncState state, CancellationToken ct)
    {
        _states[state.BranchId] = state;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.RepositorySyncStateV1, state.BranchId, JsonSerializer.Serialize(state), ct)
            .ConfigureAwait(false);
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore
            .ReadAllNodesAsync(StudioNodeKind.RepositorySyncStateV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var state = JsonSerializer.Deserialize<RepositorySyncState>(payloadJson);
            if (state is not null)
                _states[entityId] = state;
        }
    }
}
