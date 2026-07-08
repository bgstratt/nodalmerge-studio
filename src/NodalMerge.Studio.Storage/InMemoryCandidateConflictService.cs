using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Persists and serves CandidateConflictRecord nodes. Detection logic lives in
// InMemoryMergeService.TryApplyAdditivelyAsync; this is the write-once record keeper, mirroring
// InMemoryConflictService's shape for the unrelated CAS-level RepositoryConflict concept.
public sealed class InMemoryCandidateConflictService(IStudioNodeStore nodeStore)
    : ICandidateConflictService, IRehydratable
{
    private readonly object _lock = new();
    private readonly Dictionary<string, CandidateConflictRecord> _byId = new(StringComparer.Ordinal);

    public async Task<CandidateConflictRecord> RecordAsync(CandidateConflictRecord conflict, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(conflict.ConflictId))
                return _byId[conflict.ConflictId];
            _byId[conflict.ConflictId] = conflict;
        }

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.CandidateConflictV1, conflict.ConflictId, JsonSerializer.Serialize(conflict), ct)
            .ConfigureAwait(false);
        return conflict;
    }

    public Task<CandidateConflictRecord?> GetAsync(string conflictId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _byId.TryGetValue(conflictId, out var conflict);
            return Task.FromResult(conflict);
        }
    }

    public Task<IReadOnlyList<CandidateConflictRecord>> GetOpenAsync(CancellationToken ct = default)
    {
        lock (_lock)
        {
            // "Open" here means "not yet Resolved" — includes Reconciling so the promote UI can
            // still show (and disable) a conflict card while its reconciliation work unit is
            // in flight, not just before one's been triggered.
            IReadOnlyList<CandidateConflictRecord> open =
                _byId.Values.Where(c => c.Status != CandidateConflictStatus.Resolved).ToList();
            return Task.FromResult(open);
        }
    }

    public async Task<CandidateConflictRecord?> TryStartReconcilingAsync(string conflictId, CancellationToken ct = default)
    {
        CandidateConflictRecord? updated;
        lock (_lock)
        {
            if (!_byId.TryGetValue(conflictId, out var existing) || existing.Status != CandidateConflictStatus.Open)
                return null;
            updated = existing with { Status = CandidateConflictStatus.Reconciling };
            _byId[conflictId] = updated;
        }

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.CandidateConflictV1, conflictId, JsonSerializer.Serialize(updated), ct)
            .ConfigureAwait(false);
        return updated;
    }

    public async Task<CandidateConflictRecord?> MarkResolvedAsync(string conflictId, CancellationToken ct = default)
    {
        CandidateConflictRecord? updated;
        lock (_lock)
        {
            if (!_byId.TryGetValue(conflictId, out var existing))
                return null;
            updated = existing with { Status = CandidateConflictStatus.Resolved };
            _byId[conflictId] = updated;
        }

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.CandidateConflictV1, conflictId, JsonSerializer.Serialize(updated), ct)
            .ConfigureAwait(false);
        return updated;
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.CandidateConflictV1, cancellationToken)
            .ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var (_, json) in nodes)
            {
                var conflict = JsonSerializer.Deserialize<CandidateConflictRecord>(json);
                if (conflict is not null && !_byId.ContainsKey(conflict.ConflictId))
                    _byId[conflict.ConflictId] = conflict;
            }
        }
    }
}
