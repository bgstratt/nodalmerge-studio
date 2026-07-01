using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 9 — persists and serves RepositoryConflict nodes. Detection logic lives in
// InMemoryRepositoryOpService.EmitAsync; this service is the write-once record keeper.
public sealed class InMemoryConflictService(IStudioNodeStore nodeStore)
    : IConflictService, IRehydratable
{
    private readonly object _lock = new();
    // repositoryId → conflicts (all statuses; GetActiveAsync filters at read time)
    private readonly Dictionary<string, List<RepositoryConflict>> _byRepo = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RepositoryConflict> _byId = new(StringComparer.Ordinal);

    public async Task<RepositoryConflict> RecordAsync(RepositoryConflict conflict, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (_byId.ContainsKey(conflict.ConflictId))
                return _byId[conflict.ConflictId];

            Index(conflict);
        }

        var json = JsonSerializer.Serialize(conflict);
        await nodeStore.WriteNodeAsync(StudioNodeKind.RepositoryConflictV1, conflict.ConflictId, json, ct)
            .ConfigureAwait(false);
        return conflict;
    }

    public Task<IReadOnlyList<RepositoryConflict>> GetActiveAsync(string repositoryId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            if (!_byRepo.TryGetValue(repositoryId, out var all))
                return Task.FromResult<IReadOnlyList<RepositoryConflict>>([]);
            var active = all.Where(c => c.Status == ConflictStatus.Open).ToList();
            return Task.FromResult<IReadOnlyList<RepositoryConflict>>(active);
        }
    }

    public Task<RepositoryConflict?> GetAsync(string conflictId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _byId.TryGetValue(conflictId, out var c);
            return Task.FromResult(c);
        }
    }

    public async Task<RepositoryConflict?> DismissAsync(string conflictId, CancellationToken ct = default)
        => await UpdateStatusAsync(conflictId, c => c with { Status = ConflictStatus.Dismissed }, ct)
            .ConfigureAwait(false);

    public async Task<RepositoryConflict?> MarkResolvedAsync(
        string conflictId, string resolutionOpId, CancellationToken ct = default)
        => await UpdateStatusAsync(conflictId,
            c => c with { Status = ConflictStatus.Resolved, ResolvedByConflictResolutionOpId = resolutionOpId },
            ct).ConfigureAwait(false);

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.RepositoryConflictV1, cancellationToken)
            .ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var (_, json) in nodes)
            {
                var conflict = JsonSerializer.Deserialize<RepositoryConflict>(json);
                if (conflict is not null && !_byId.ContainsKey(conflict.ConflictId))
                    Index(conflict);
            }
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<RepositoryConflict?> UpdateStatusAsync(
        string conflictId,
        Func<RepositoryConflict, RepositoryConflict> mutate,
        CancellationToken ct)
    {
        RepositoryConflict? updated;
        lock (_lock)
        {
            if (!_byId.TryGetValue(conflictId, out var existing)) return null;
            updated = mutate(existing);
            _byId[conflictId] = updated;
            if (_byRepo.TryGetValue(updated.RepositoryId, out var list))
            {
                var idx = list.FindIndex(c => c.ConflictId == conflictId);
                if (idx >= 0) list[idx] = updated;
            }
        }

        var json = JsonSerializer.Serialize(updated);
        await nodeStore.WriteNodeAsync(StudioNodeKind.RepositoryConflictV1, conflictId, json, ct)
            .ConfigureAwait(false);
        return updated;
    }

    private void Index(RepositoryConflict conflict)
    {
        _byId[conflict.ConflictId] = conflict;
        if (!_byRepo.TryGetValue(conflict.RepositoryId, out var list))
            _byRepo[conflict.RepositoryId] = list = [];
        list.Add(conflict);
    }
}
