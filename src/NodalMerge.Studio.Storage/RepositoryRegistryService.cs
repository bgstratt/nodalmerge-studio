using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IRepositoryRegistryService
{
    Task<RepositoryV1> RegisterAsync(string path, string? label, CancellationToken ct = default);
    Task<IReadOnlyList<RepositoryV1>> ListAsync(CancellationToken ct = default);
    Task<RepositoryV1?> GetAsync(string repositoryId, CancellationToken ct = default);
}

public sealed class RepositoryRegistryService : IRepositoryRegistryService, IRehydratable
{
    private readonly ConcurrentDictionary<string, RepositoryV1> _repositories = new();
    private readonly IStudioNodeStore _nodeStore;

    public RepositoryRegistryService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<RepositoryV1> RegisterAsync(string path, string? label, CancellationToken ct = default)
    {
        var normalized = NormalizePath(path);

        // Idempotent by normalized path — re-registering the same repository returns the existing
        // entry rather than duplicating it.
        var existing = _repositories.Values.FirstOrDefault(r => NormalizePath(r.Path) == normalized);
        if (existing is not null)
            return existing;

        var repository = new RepositoryV1(
            RepositoryId: $"repo-{Guid.NewGuid():N}",
            Path: path,
            Label: label,
            RegisteredAt: DateTimeOffset.UtcNow);

        _repositories[repository.RepositoryId] = repository;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.RepositoryV1, repository.RepositoryId,
            JsonSerializer.Serialize(repository), ct).ConfigureAwait(false);

        return repository;
    }

    public Task<IReadOnlyList<RepositoryV1>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<RepositoryV1>>(
            _repositories.Values.OrderBy(r => r.RegisteredAt).ToList());

    public Task<RepositoryV1?> GetAsync(string repositoryId, CancellationToken ct = default)
    {
        _repositories.TryGetValue(repositoryId, out var repository);
        return Task.FromResult(repository);
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore
            .ReadAllNodesAsync(StudioNodeKind.RepositoryV1, cancellationToken).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var repository = JsonSerializer.Deserialize<RepositoryV1>(payloadJson);
            if (repository is not null)
                _repositories[repository.RepositoryId] = repository;
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
}
