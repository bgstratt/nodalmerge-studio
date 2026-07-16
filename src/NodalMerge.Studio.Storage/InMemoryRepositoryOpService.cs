using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class InMemoryRepositoryOpService : IRepositoryOpService, IRehydratable
{
    private readonly IStudioNodeStore _nodeStore;
    private readonly IConflictService? _conflictService;
    private readonly IRepositorySnapshotService? _snapshotService;
    private readonly WorkspaceOptions? _options;

    private readonly object _lock = new();
    private readonly Dictionary<(string RepositoryId, string Path), List<RepositoryOperation>> _index = new();
    // Phase 6 — secondary index for compaction: all ops per repository in emit order.
    private readonly Dictionary<string, List<RepositoryOperation>> _byRepository = new(StringComparer.Ordinal);

    public InMemoryRepositoryOpService(
        IStudioNodeStore nodeStore,
        IConflictService? conflictService = null,
        IRepositorySnapshotService? snapshotService = null,
        WorkspaceOptions? options = null)
    {
        _nodeStore       = nodeStore;
        _conflictService = conflictService;
        _snapshotService = snapshotService;
        _options         = options;
    }

    public async Task EmitAsync(RepositoryOperation op, CancellationToken ct = default)
    {
        // Phase 9 — structural conflict detection before the op lands.
        if (_conflictService is not null && IsConflictableOp(op))
        {
            var forking = await FindForkingOpsAsync(op, ct).ConfigureAwait(false);
            foreach (var prior in forking)
            {
                var conflict = new RepositoryConflict(
                    ConflictId:    $"conflict-{op.RepositoryId}-{op.Path.Replace('/', '-')}-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}",
                    RepositoryId:  op.RepositoryId,
                    Path:          op.Path,
                    OpIdA:         prior.OperationId,
                    OpIdB:         op.OperationId,
                    OldBlobId:     op.OldBlobId,
                    BlobIdA:       prior.NewBlobId,
                    BlobIdB:       op.NewBlobId,
                    DetectedAt:    DateTimeOffset.UtcNow,
                    WorkUnitIdA:   prior.WorkUnitId,
                    WorkUnitIdB:   op.WorkUnitId);

                await _conflictService.RecordAsync(conflict, ct).ConfigureAwait(false);

                if (_options?.BlockConflictingOps == true)
                    throw new ConflictingOpException(conflict);
            }
        }

        var json = JsonSerializer.Serialize(op);
        await _nodeStore.WriteNodeAsync(StudioNodeKind.RepositoryOpV1, op.OperationId, json, op.RepositoryId, ct)
            .ConfigureAwait(false);
        IndexOp(op);
    }

    public Task<IReadOnlyList<RepositoryOperation>> GetRecentOpsForPathsAsync(
        string repositoryId, IReadOnlyList<string> paths, int limit = 5, CancellationToken ct = default)
    {
        lock (_lock)
        {
            var results = new List<RepositoryOperation>();
            foreach (var path in paths)
            {
                if (_index.TryGetValue((repositoryId, path), out var ops))
                    results.AddRange(ops.TakeLast(limit));
            }
            return Task.FromResult<IReadOnlyList<RepositoryOperation>>(
                results.OrderByDescending(o => o.Timestamp).ToList());
        }
    }

    public Task<IReadOnlyList<RepositoryOperation>> GetOpsSinceAsync(
        string repositoryId, DateTimeOffset since, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_byRepository.TryGetValue(repositoryId, out var all))
                return Task.FromResult<IReadOnlyList<RepositoryOperation>>([]);
            var results = all.Where(o => o.Timestamp > since)
                             .OrderBy(o => o.Timestamp)
                             .ToList();
            return Task.FromResult<IReadOnlyList<RepositoryOperation>>(results);
        }
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.RepositoryOpV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (_, json) in nodes)
        {
            var op = JsonSerializer.Deserialize<RepositoryOperation>(json);
            if (op is not null)
                IndexOp(op);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Only check Add/Replace/Delete — Import ops are system-level bootstraps, not agent writes.
    private static bool IsConflictableOp(RepositoryOperation op) =>
        op.Kind is OperationType.Add or OperationType.Replace or OperationType.Delete;

    // Returns ops already in the index that fork from the same OldBlobId as the incoming op.
    // Scoped to ops since the latest snapshot when the snapshot service is available — this
    // prevents ops from different goal cycles from appearing to conflict with each other.
    private async Task<List<RepositoryOperation>> FindForkingOpsAsync(
        RepositoryOperation incoming, CancellationToken ct)
    {
        DateTimeOffset? since = null;
        if (_snapshotService is not null)
        {
            var snapshot = await _snapshotService.GetLatestAsync(incoming.RepositoryId, ct)
                .ConfigureAwait(false);
            since = snapshot?.CreatedAt;
        }

        lock (_lock)
        {
            var key = (incoming.RepositoryId, incoming.Path);
            if (!_index.TryGetValue(key, out var all)) return [];

            var candidates = since.HasValue
                ? all.Where(o => o.Timestamp > since.Value)
                : all.AsEnumerable();

            // Two ops fork when they start from the same blob (same OldBlobId, including both
            // null for concurrent Adds) but produce different blobs.
            return candidates
                .Where(o => o.OldBlobId == incoming.OldBlobId
                    && o.NewBlobId != incoming.NewBlobId
                    && o.Kind is not OperationType.Import)
                .ToList();
        }
    }

    private void IndexOp(RepositoryOperation op)
    {
        lock (_lock)
        {
            var key = (op.RepositoryId, op.Path);
            if (!_index.TryGetValue(key, out var list))
                _index[key] = list = [];
            list.Add(op);

            if (!_byRepository.TryGetValue(op.RepositoryId, out var repoList))
                _byRepository[op.RepositoryId] = repoList = [];
            repoList.Add(op);
        }
    }
}

// Thrown when WorkspaceOptions.BlockConflictingOps = true and a forking op is detected.
// Default is false — callers that need to handle this must opt in via WorkspaceOptions.
public sealed class ConflictingOpException(RepositoryConflict conflict)
    : InvalidOperationException(
        $"Op on '{conflict.Path}' conflicts with op {conflict.OpIdA} — both forked from blob '{conflict.OldBlobId ?? "<new file>"}'.")
{
    public RepositoryConflict Conflict { get; } = conflict;
}
