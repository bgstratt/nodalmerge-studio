using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class ArtifactLineageService : IArtifactLineageService, IRehydratable
{
    private readonly ConcurrentDictionary<string, ArtifactRef> _byId = new();
    // workUnitId / parentArtifactId → ordered list of artifactIds
    private readonly ConcurrentDictionary<string, List<string>> _byWorkUnit = new();
    private readonly ConcurrentDictionary<string, List<string>> _byParent = new();
    private readonly Lock _indexLock = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IExecutionEventStream? _events;
    private readonly IRuntimeEventBroadcaster? _broadcaster;
    private readonly IServiceProvider? _serviceProvider;

    // events/broadcaster/serviceProvider are optional (default to null, silently skipping the
    // audit-trail append / live broadcast / RepositoryId resolution) so the many existing call
    // sites constructing this service directly in tests don't all need updating — same
    // optional-collaborator convention used throughout this codebase.
    //
    // Slice 6.3a — serviceProvider resolves IWorkUnitService LAZILY (never constructor-injected
    // directly): InMemoryWorkUnitService itself constructor-depends on IArtifactLineageService, so
    // a direct dependency here would be the exact circular constructor graph InMemoryMergeService
    // and others already dodge for the same interface via this identical pattern.
    public ArtifactLineageService(
        IStudioNodeStore nodeStore, IExecutionEventStream? events = null, IRuntimeEventBroadcaster? broadcaster = null,
        IServiceProvider? serviceProvider = null)
    {
        _nodeStore = nodeStore;
        _events = events;
        _broadcaster = broadcaster;
        _serviceProvider = serviceProvider;
    }

    public async Task<ArtifactRef> RecordAsync(ArtifactRef artifact, CancellationToken ct = default)
    {
        // Idempotent: a second record with the same ArtifactId is a no-op.
        if (_byId.TryGetValue(artifact.ArtifactId, out var existing))
            return existing;

        // Slice 6.3a — denormalize from OwnedByWorkUnitId's own RepositoryId when the caller didn't
        // already supply one. Null OwnedByWorkUnitId (global artifacts) or an unresolvable work
        // unit both fall back to null → "studio", never blocking the record.
        var stored = artifact;
        if (stored.RepositoryId is null && stored.OwnedByWorkUnitId is not null)
        {
            var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            var repositoryId = await RepositoryIdResolution
                .ResolveFromWorkUnitAsync(workUnits, stored.OwnedByWorkUnitId, ct).ConfigureAwait(false);
            if (repositoryId is not null)
                stored = stored with { RepositoryId = repositoryId };
        }

        // L2.2b — ArtifactRefV1 is repo-scoped (replicates to every peer), so an uncapped Body
        // (Plan/Research/Decision/RevisionContext free text) would bloat the replication plane.
        stored = stored with { Body = NodePayloadLimits.Cap(stored.Body) };

        _byId[stored.ArtifactId] = stored;
        Index(stored);

        // Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — route by (Reach,
        // RepositoryId). Reach == null is legacy routing (repo room if RepositoryId set, else
        // "studio"), byte-identical to pre-Phase-1 behavior, so existing artifacts are unchanged.
        var json = JsonSerializer.Serialize(stored);
        var id = stored.ArtifactId;
        if (stored.Reach == ArtifactReach.Private)
        {
            // Peer-private "studio" room — never replicated. RepositoryId (if any) is only an
            // application filter, not a routing key.
            await _nodeStore.WriteNodeAsync(StudioNodeKind.ArtifactRefV1, id, json, ct).ConfigureAwait(false);
        }
        else if (stored.Reach == ArtifactReach.Workgroup && string.IsNullOrEmpty(stored.RepositoryId))
        {
            // Workgroup reach, all repos → the shared "workgroup" room (the old "global", now actually
            // replicated to every member instead of stranded in the local room).
            await _nodeStore.WriteWorkgroupNodeAsync(StudioNodeKind.ArtifactRefV1, id, json, ct).ConfigureAwait(false);
        }
        else
        {
            // Workgroup reach + a RepositoryId → that repo's room; and legacy (Reach null) → the
            // existing repo-room-or-"studio" fallback.
            await _nodeStore.WriteNodeAsync(StudioNodeKind.ArtifactRefV1, id, json, stored.RepositoryId, ct).ConfigureAwait(false);
        }

        return stored;
    }

    public Task<ArtifactRef?> GetAsync(string artifactId, CancellationToken ct = default)
    {
        _byId.TryGetValue(artifactId, out var artifact);
        return Task.FromResult(artifact);
    }

    public Task<IReadOnlyList<ArtifactRef>> GetChainAsync(string workUnitId, CancellationToken ct = default) =>
        Task.FromResult(ResolveOrdered(_byWorkUnit, workUnitId));

    public Task<IReadOnlyList<ArtifactRef>> GetChildrenAsync(string parentArtifactId, CancellationToken ct = default) =>
        Task.FromResult(ResolveOrdered(_byParent, parentArtifactId));

    public async Task<ArtifactRef> UpdateStatusAsync(string artifactId, ArtifactStatus status, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(artifactId, out var existing))
            throw new KeyNotFoundException($"Artifact '{artifactId}' was not found.");

        var updated = existing with { Status = status };
        _byId[artifactId] = updated;

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ArtifactRefV1,
            artifactId,
            JsonSerializer.Serialize(updated),
            updated.RepositoryId,
            ct).ConfigureAwait(false);

        return updated;
    }

    // Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — progressive promotion: widen a
    // constraint's *application* from one repo to all repos (RepositoryId → null), keeping Workgroup
    // reach. This re-routes it into the shared "workgroup" room, so every member sees it regardless of
    // repo. Idempotent: a no-op (returns unchanged) if already workgroup-wide; null if not found.
    // Mirrors UpdateStatusAsync (mutate _byId + persist) — OwnedByWorkUnitId/ParentArtifactId are
    // unchanged, so the parent/work-unit indices don't need re-indexing.
    public async Task<ArtifactRef?> ElevateToWorkgroupAsync(string artifactId, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(artifactId, out var existing))
            return null;
        if (existing.Reach == ArtifactReach.Workgroup && string.IsNullOrEmpty(existing.RepositoryId))
            return existing;

        var updated = existing with { Reach = ArtifactReach.Workgroup, RepositoryId = null };
        _byId[artifactId] = updated;

        await _nodeStore.WriteWorkgroupNodeAsync(
            StudioNodeKind.ArtifactRefV1, artifactId, JsonSerializer.Serialize(updated), ct).ConfigureAwait(false);

        return updated;
    }

    public async Task<ArtifactRef> ReparentAsync(string artifactId, string newParentArtifactId, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(artifactId, out var existing))
            throw new KeyNotFoundException($"Artifact '{artifactId}' was not found.");

        var updated = existing with { ParentArtifactId = newParentArtifactId };
        _byId[artifactId] = updated;

        lock (_indexLock)
        {
            if (existing.ParentArtifactId is { } oldParent)
            {
                if (_byParent.TryGetValue(oldParent, out var oldList))
                    oldList.Remove(artifactId);
            }

            if (!_byParent.TryGetValue(newParentArtifactId, out var newList))
                _byParent[newParentArtifactId] = newList = [];
            if (!newList.Contains(artifactId))
                newList.Add(artifactId);
        }

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ArtifactRefV1,
            artifactId,
            JsonSerializer.Serialize(updated),
            updated.RepositoryId,
            ct).ConfigureAwait(false);

        return updated;
    }

    private static readonly HashSet<ArtifactType> InvalidatableTypes =
        new() { ArtifactType.Research, ArtifactType.Decision, ArtifactType.Constraint };

    public async Task<ArtifactRef> InvalidateAsync(
        string artifactId, string reason, string? sessionId = null, CancellationToken ct = default)
    {
        if (!_byId.TryGetValue(artifactId, out var target))
            throw new KeyNotFoundException($"Artifact '{artifactId}' was not found.");
        if (!InvalidatableTypes.Contains(target.Type))
            throw new ArgumentException(
                $"Cannot invalidate artifact of type '{target.Type}' — only Research, Decision, and Constraint artifacts can be invalidated.",
                nameof(artifactId));

        var previousStatus = target.Status;
        var invalidated = target with { Status = ArtifactStatus.Invalidated };
        _byId[artifactId] = invalidated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ArtifactRefV1, artifactId, JsonSerializer.Serialize(invalidated), invalidated.RepositoryId, ct)
            .ConfigureAwait(false);

        var flaggedIds = new List<string>();
        var queue = new Queue<string>(await GetChildrenIdsAsync(artifactId, ct).ConfigureAwait(false));
        var visited = new HashSet<string> { artifactId };
        while (queue.Count > 0)
        {
            var descendantId = queue.Dequeue();
            if (!visited.Add(descendantId) || !_byId.TryGetValue(descendantId, out var descendant))
                continue;

            var flagged = descendant with { InvalidatedByArtifactId = artifactId };
            _byId[descendantId] = flagged;
            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.ArtifactRefV1, descendantId, JsonSerializer.Serialize(flagged), flagged.RepositoryId, ct)
                .ConfigureAwait(false);
            flaggedIds.Add(descendantId);

            foreach (var childId in await GetChildrenIdsAsync(descendantId, ct).ConfigureAwait(false))
                queue.Enqueue(childId);
        }

        if (sessionId is not null && _events is not null)
        {
            await _events.AppendAsync(
                sessionId, target.OwnedByWorkUnitId, ExecutionEventKind.ArtifactStatusChanged,
                new ArtifactStatusChangedPayload(artifactId, previousStatus, ArtifactStatus.Invalidated),
                ct: ct).ConfigureAwait(false);

            if (flaggedIds.Count > 0)
            {
                await _events.AppendAsync(
                    sessionId, target.OwnedByWorkUnitId, ExecutionEventKind.ArtifactInvalidationCascaded,
                    new ArtifactInvalidationCascadedPayload(artifactId, reason, flaggedIds),
                    ct: ct).ConfigureAwait(false);
            }
        }

        if (_broadcaster is not null)
        {
            await _broadcaster.BroadcastArtifactInvalidatedAsync(
                target.OwnedByWorkUnitId, artifactId, flaggedIds, reason, ct).ConfigureAwait(false);
        }

        return invalidated;
    }

    private async Task<IReadOnlyList<string>> GetChildrenIdsAsync(string parentArtifactId, CancellationToken ct)
    {
        var children = await GetChildrenAsync(parentArtifactId, ct).ConfigureAwait(false);
        return children.Select(c => c.ArtifactId).ToList();
    }

    public Task<IReadOnlyList<ArtifactRef>> GetGlobalConstraintsAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ArtifactRef>>(
            _byId.Values
                .Where(a => a.OwnedByWorkUnitId is null && a.Type == ArtifactType.Constraint)
                .OrderBy(a => a.CreatedAt)
                .ToList());

    public Task<IReadOnlyList<ArtifactRef>> GetByTypeAsync(ArtifactType type, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ArtifactRef>>(
            _byId.Values
                .Where(a => a.Type == type)
                .OrderBy(a => a.CreatedAt)
                .ToList());

    // Slice 6.5 Part 1 — see InMemoryWorkUnitService.RehydratedKinds' doc comment.
    public IReadOnlyCollection<string> RehydratedKinds => [StudioNodeKind.ArtifactRefV1];

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.ArtifactRefV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var artifact = JsonSerializer.Deserialize<ArtifactRef>(payloadJson);
            if (artifact is not null && _byId.TryAdd(artifact.ArtifactId, artifact))
                Index(artifact);
        }
    }

    // Slice 6.5 Part 1 — RehydrateAsync's TryAdd guard is correct for startup (every row is new)
    // but would hide a remote UpdateStatusAsync/InvalidateAsync on an artifact this peer already
    // knows about. A brand-new artifact is added and indexed exactly as RehydrateAsync does; an
    // already-known artifact has its value overwritten in place without touching _byWorkUnit/
    // _byParent — OwnedByWorkUnitId never changes after RecordAsync, and a *remote* ReparentAsync
    // (the one mutation that does change ParentArtifactId) isn't reconciled by this refresh, a
    // known gap consistent with this class's existing index-vs-value split.
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.ArtifactRefV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var artifact = JsonSerializer.Deserialize<ArtifactRef>(payloadJson);
            if (artifact is null)
                continue;

            if (_byId.TryAdd(artifact.ArtifactId, artifact))
                Index(artifact);
            else
                _byId[artifact.ArtifactId] = artifact;
        }
    }

    private IReadOnlyList<ArtifactRef> ResolveOrdered(ConcurrentDictionary<string, List<string>> index, string key)
    {
        List<string> ids;
        lock (_indexLock)
            ids = index.TryGetValue(key, out var list) ? [.. list] : [];

        return ids
            .Select(id => _byId.TryGetValue(id, out var a) ? a : null)
            .Where(a => a is not null)
            .Cast<ArtifactRef>()
            .OrderBy(a => a.CreatedAt)
            .ToList();
    }

    private void Index(ArtifactRef artifact)
    {
        lock (_indexLock)
        {
            if (artifact.OwnedByWorkUnitId is { } workUnitId)
            {
                if (!_byWorkUnit.TryGetValue(workUnitId, out var byWorkUnitList))
                    _byWorkUnit[workUnitId] = byWorkUnitList = [];
                byWorkUnitList.Add(artifact.ArtifactId);
            }

            if (artifact.ParentArtifactId is { } parentId)
            {
                if (!_byParent.TryGetValue(parentId, out var byParentList))
                    _byParent[parentId] = byParentList = [];
                byParentList.Add(artifact.ArtifactId);
            }
        }
    }
}
