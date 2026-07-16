using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 11.5 — mines the RepositoryOp log for pairwise co-modification frequency.
// Computation is on-demand (not triggered per-op) since a full pairwise scan over all ops
// is expensive. Results are persisted as CoModPatternV1 nodes so they survive restarts.
internal sealed class InMemoryCoModService(IStudioNodeStore nodeStore) : ICoModService, IRehydratable
{
    // repositoryId → patterns computed during the last ComputeAsync call.
    private readonly Dictionary<string, List<CoModificationPattern>> _byRepo =
        new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _lock = new(1, 1);

    public async Task<IReadOnlyList<CoModificationPattern>> ComputeAsync(
        string repositoryId, CancellationToken ct = default)
    {
        var opNodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.RepositoryOpV1, ct)
            .ConfigureAwait(false);

        // Group paths touched per work unit for this repository.
        var pathsByWorkUnit = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var (_, json) in opNodes)
        {
            var op = JsonSerializer.Deserialize<RepositoryOperation>(json);
            if (op is null || op.WorkUnitId is null || op.Path is null) continue;
            if (!string.Equals(op.RepositoryId, repositoryId, StringComparison.Ordinal)) continue;

            if (!pathsByWorkUnit.TryGetValue(op.WorkUnitId, out var paths))
                pathsByWorkUnit[op.WorkUnitId] = paths = new HashSet<string>(StringComparer.Ordinal);
            paths.Add(op.Path);
        }

        var totalWorkUnitsScanned = pathsByWorkUnit.Count;
        if (totalWorkUnitsScanned == 0)
            return [];

        // Pairwise co-occurrence: always store with alphabetically-lower path as PathA so
        // (A,B) and (B,A) collapse into one canonical entry.
        var coOccurrences = new Dictionary<(string PathA, string PathB), int>();
        foreach (var paths in pathsByWorkUnit.Values)
        {
            var sorted = paths.OrderBy(p => p, StringComparer.Ordinal).ToArray();
            for (var i = 0; i < sorted.Length; i++)
                for (var j = i + 1; j < sorted.Length; j++)
                {
                    var key = (sorted[i], sorted[j]);
                    coOccurrences[key] = coOccurrences.GetValueOrDefault(key, 0) + 1;
                }
        }

        var now = DateTimeOffset.UtcNow;
        var patterns = new List<CoModificationPattern>(coOccurrences.Count);
        foreach (var ((pathA, pathB), count) in coOccurrences)
        {
            var confidence = (double)count / totalWorkUnitsScanned;
            // Deterministic ID so recomputes overwrite rather than duplicate.
            var patternId = $"comod-{Math.Abs(HashCode.Combine(repositoryId, pathA, pathB)):x8}";
            var pattern = new CoModificationPattern(
                patternId, repositoryId, pathA, pathB,
                count, totalWorkUnitsScanned, confidence, now);
            patterns.Add(pattern);

            var nodeJson = JsonSerializer.Serialize(pattern);
            await nodeStore.WriteNodeAsync(StudioNodeKind.CoModPatternV1, patternId, nodeJson, pattern.RepositoryId, ct)
                .ConfigureAwait(false);
        }

        await _lock.WaitAsync(ct).ConfigureAwait(false);
        try { _byRepo[repositoryId] = patterns; }
        finally { _lock.Release(); }

        return patterns;
    }

    public Task<IReadOnlyList<CoModificationPattern>> GetAsync(
        string repositoryId, CancellationToken ct = default)
    {
        _lock.Wait(ct);
        try
        {
            return Task.FromResult<IReadOnlyList<CoModificationPattern>>(
                _byRepo.TryGetValue(repositoryId, out var list)
                    ? (IReadOnlyList<CoModificationPattern>)list
                    : []);
        }
        finally { _lock.Release(); }
    }

    public Task<IReadOnlyList<CoModificationPattern>> GetForPathsAsync(
        string repositoryId, IReadOnlyList<string> paths,
        double minConfidence = 0.6, CancellationToken ct = default)
    {
        // paths are directory prefixes (e.g. "src/Auth") or exact file paths.
        // Match co-mod patterns where PathA or PathB starts with any of the prefixes.
        _lock.Wait(ct);
        try
        {
            if (!_byRepo.TryGetValue(repositoryId, out var all))
                return Task.FromResult<IReadOnlyList<CoModificationPattern>>([]);

            var matching = all
                .Where(p => p.Confidence >= minConfidence && MatchesAnyPrefix(p.PathA, paths, p.PathB))
                .ToList();
            return Task.FromResult<IReadOnlyList<CoModificationPattern>>(matching);
        }
        finally { _lock.Release(); }
    }

    private static bool MatchesAnyPrefix(string pathA, IReadOnlyList<string> prefixes, string pathB)
    {
        foreach (var prefix in prefixes)
        {
            if (pathA.StartsWith(prefix, StringComparison.Ordinal)
                || pathB.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    // Slice 6.5 Part 1 — see InMemoryWorkUnitService.RehydratedKinds' doc comment.
    public IReadOnlyCollection<string> RehydratedKinds => [StudioNodeKind.CoModPatternV1];

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.CoModPatternV1, cancellationToken)
            .ConfigureAwait(false);

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (var (_, json) in nodes)
            {
                var pattern = JsonSerializer.Deserialize<CoModificationPattern>(json);
                if (pattern is null) continue;
                if (!_byRepo.TryGetValue(pattern.RepositoryId, out var list))
                    _byRepo[pattern.RepositoryId] = list = [];
                list.Add(pattern);
            }
        }
        finally { _lock.Release(); }
    }
}
