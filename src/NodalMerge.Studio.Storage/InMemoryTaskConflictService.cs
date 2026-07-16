using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Persists and serves TaskConflictRecord nodes. Mirrors InMemoryCandidateConflictService's shape
// exactly — detection logic lives in InMemoryMergeService.TryApplyAdditivelyAsync; this is the
// write-once record keeper.
public sealed class InMemoryTaskConflictService(IStudioNodeStore nodeStore)
    : ITaskConflictService, IRehydratable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, TaskConflictRecord> _byId = new(StringComparer.Ordinal);

    public async Task<TaskConflictRecord> RecordAsync(TaskConflictRecord conflict, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(conflict.ConflictId))
                return _byId[conflict.ConflictId];
            _byId[conflict.ConflictId] = conflict;
        }

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskConflictV1, conflict.ConflictId, JsonSerializer.Serialize(conflict), conflict.RepositoryId, ct)
            .ConfigureAwait(false);
        return conflict;
    }

    public Task<TaskConflictRecord?> GetAsync(string conflictId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _byId.TryGetValue(conflictId, out var conflict);
            return Task.FromResult(conflict);
        }
    }

    public Task<IReadOnlyList<TaskConflictRecord>> GetOpenAsync(string? parentWorkUnitId = null, CancellationToken ct = default)
    {
        lock (_lock)
        {
            IReadOnlyList<TaskConflictRecord> open = _byId.Values
                .Where(c => c.Status != TaskConflictStatus.Resolved)
                .Where(c => parentWorkUnitId is null || c.ParentWorkUnitId == parentWorkUnitId)
                .ToList();
            return Task.FromResult(open);
        }
    }

    public async Task<TaskConflictRecord?> TryStartReconcilingAsync(string conflictId, CancellationToken ct = default)
    {
        TaskConflictRecord? updated;
        lock (_lock)
        {
            if (!_byId.TryGetValue(conflictId, out var existing) || existing.Status != TaskConflictStatus.Open)
                return null;
            updated = existing with { Status = TaskConflictStatus.Reconciling };
            _byId[conflictId] = updated;
        }

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskConflictV1, conflictId, JsonSerializer.Serialize(updated), updated.RepositoryId, ct)
            .ConfigureAwait(false);
        return updated;
    }

    public async Task<TaskConflictRecord?> TryReopenAsync(string conflictId, CancellationToken ct = default)
    {
        TaskConflictRecord? updated;
        lock (_lock)
        {
            if (!_byId.TryGetValue(conflictId, out var existing) || existing.Status != TaskConflictStatus.Reconciling)
                return null;
            updated = existing with { Status = TaskConflictStatus.Open };
            _byId[conflictId] = updated;
        }

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskConflictV1, conflictId, JsonSerializer.Serialize(updated), updated.RepositoryId, ct)
            .ConfigureAwait(false);
        return updated;
    }

    public async Task<TaskConflictRecord?> MarkResolvedAsync(string conflictId, CancellationToken ct = default)
    {
        TaskConflictRecord? updated;
        lock (_lock)
        {
            if (!_byId.TryGetValue(conflictId, out var existing))
                return null;
            updated = existing with { Status = TaskConflictStatus.Resolved };
            _byId[conflictId] = updated;
        }

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskConflictV1, conflictId, JsonSerializer.Serialize(updated), updated.RepositoryId, ct)
            .ConfigureAwait(false);
        return updated;
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.TaskConflictV1, cancellationToken)
            .ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var (_, json) in nodes)
            {
                var conflict = JsonSerializer.Deserialize<TaskConflictRecord>(json);
                if (conflict is not null && !_byId.ContainsKey(conflict.ConflictId))
                    _byId[conflict.ConflictId] = conflict;
            }
        }
    }
}
