using System.Collections.Concurrent;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class InMemoryArtifactRefService : IArtifactRefService
{
    private readonly ConcurrentDictionary<string, List<ArtifactRef>> _byWorkUnit = new();

    public Task WriteAsync(ArtifactRef artifact, CancellationToken cancellationToken = default)
    {
        var key = artifact.OwnedByWorkUnitId ?? "__unowned__";
        _byWorkUnit.AddOrUpdate(
            key,
            _ => [artifact],
            (_, list) =>
            {
                lock (list)
                {
                    // Idempotent: no-op if ArtifactId already recorded.
                    if (list.All(r => r.ArtifactId != artifact.ArtifactId))
                        list.Add(artifact);
                }
                return list;
            });
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ArtifactRef>> ListAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        if (!_byWorkUnit.TryGetValue(workUnitId, out var list))
            return Task.FromResult<IReadOnlyList<ArtifactRef>>([]);

        IReadOnlyList<ArtifactRef> snapshot;
        lock (list) { snapshot = list.OrderBy(r => r.CreatedAt).ToList(); }
        return Task.FromResult(snapshot);
    }
}
