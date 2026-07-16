using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 2 — durable snapshot checkpoints for the repository op log.
// One snapshot per goal cycle (created by between-run sync) keeps the replay window small;
// replaying 10–30 ops from the latest snapshot is fast enough that aggressive snapshotting
// is unnecessary. See WorkspaceOptions.SnapshotPolicy for the configurable knobs.
// IRepositoryOpService is resolved lazily via IServiceProvider to break a circular DI dependency:
// InMemoryRepositoryOpService → IRepositorySnapshotService → InMemoryRepositorySnapshotService
// → IRepositoryOpService (already being constructed). In .NET 10, this causes infinite recursion
// instead of throwing InvalidOperationException as earlier DI versions did.
internal sealed class InMemoryRepositorySnapshotService(
    IStudioNodeStore nodeStore,
    IServiceProvider? serviceProvider = null,
    // Slice 1.1 — when CanWrite, CreateAsync stores the tree map in the CAS (TreeEntries: null,
    // TreeFormat: "cas-tree") instead of inlining it. Null/CanWrite==false preserves today's
    // behavior exactly (inline TreeEntries, SHA256 ComputeTreeHash).
    ISnapshotTreeResolver? treeResolver = null)
    : IRepositorySnapshotService, IRehydratable
{
    // Per-repository gate serializes CreateAsync so two concurrent callers can't produce
    // duplicate generations — same pattern as RepositorySyncService._gates.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    // repositoryId → snapshot with highest Generation
    private readonly Dictionary<string, RepositorySnapshot> _latest = new(StringComparer.Ordinal);
    // Phase 14 — snapshotId → snapshot for GetAsync(snapshotId) lookups
    private readonly Dictionary<string, RepositorySnapshot> _byId = new(StringComparer.Ordinal);

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var nodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.RepositorySnapshotV1, ct)
            .ConfigureAwait(false);
        lock (_lock)
        {
            foreach (var (_, json) in nodes)
            {
                var snapshot = JsonSerializer.Deserialize<RepositorySnapshot>(json);
                if (snapshot is null) continue;
                _byId[snapshot.SnapshotId] = snapshot;
                if (!_latest.TryGetValue(snapshot.RepositoryId, out var existing)
                    || snapshot.Generation > existing.Generation)
                {
                    _latest[snapshot.RepositoryId] = snapshot;
                }
            }
        }
    }

    public Task<RepositorySnapshot?> GetLatestAsync(string repositoryId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _latest.TryGetValue(repositoryId, out var snapshot);
            return Task.FromResult(snapshot);
        }
    }

    public Task<RepositorySnapshot?> GetAsync(string snapshotId, CancellationToken ct = default)
    {
        lock (_lock)
        {
            _byId.TryGetValue(snapshotId, out var snapshot);
            return Task.FromResult(snapshot);
        }
    }

    public async Task<RepositorySnapshot> CreateAsync(
        string repositoryId,
        IReadOnlyDictionary<string, string> treeEntries,
        string? baseSnapshotId = null,
        string? workUnitId = null,
        string? gitCommit = null,
        string? source = null,
        CancellationToken ct = default)
    {
        var gate = _gates.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            long nextGeneration;
            lock (_lock)
            {
                nextGeneration = _latest.TryGetValue(repositoryId, out var existing)
                    ? existing.Generation + 1
                    : 0;
            }

            RepositorySnapshot snapshot;
            if (treeResolver?.CanWrite == true)
            {
                // Slice 1.1 — move the map into the CAS; the node carries only the root hash.
                var treeHash = await treeResolver.WriteTreeAsync(treeEntries, ct).ConfigureAwait(false);
                snapshot = new RepositorySnapshot(
                    SnapshotId: Guid.NewGuid().ToString("N"),
                    RepositoryId: repositoryId,
                    TreeHash: treeHash,
                    Generation: nextGeneration,
                    CreatedAt: DateTimeOffset.UtcNow,
                    BaseSnapshotId: baseSnapshotId,
                    GitCommit: gitCommit,
                    WorkUnitId: workUnitId,
                    Source: source,
                    TreeEntries: null,
                    TreeFormat: "cas-tree");
            }
            else
            {
                // No blob store configured — today's behavior exactly: inline TreeEntries, SHA256
                // ComputeTreeHash, TreeFormat left null.
                var treeHash = ComputeTreeHash(treeEntries);
                snapshot = new RepositorySnapshot(
                    SnapshotId: Guid.NewGuid().ToString("N"),
                    RepositoryId: repositoryId,
                    TreeHash: treeHash,
                    Generation: nextGeneration,
                    CreatedAt: DateTimeOffset.UtcNow,
                    BaseSnapshotId: baseSnapshotId,
                    GitCommit: gitCommit,
                    WorkUnitId: workUnitId,
                    Source: source,
                    TreeEntries: treeEntries);
            }

            await nodeStore.WriteNodeAsync(
                StudioNodeKind.RepositorySnapshotV1, snapshot.SnapshotId,
                JsonSerializer.Serialize(snapshot), snapshot.RepositoryId, ct).ConfigureAwait(false);

            lock (_lock)
            {
                _latest[repositoryId] = snapshot;
                _byId[snapshot.SnapshotId] = snapshot;
            }

            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<RepositorySnapshot?> ConsiderCompactionAsync(
        string repositoryId, int? threshold, CancellationToken ct = default)
    {
        if (threshold is null) return null;
        var repoOpService = serviceProvider?.GetService<IRepositoryOpService>();
        if (repoOpService is null) return null;

        RepositorySnapshot? latest;
        lock (_lock) { _latest.TryGetValue(repositoryId, out latest); }

        var since = latest?.CreatedAt ?? DateTimeOffset.MinValue;
        var opsSince = await repoOpService.GetOpsSinceAsync(repositoryId, since, ct).ConfigureAwait(false);

        if (opsSince.Count < threshold.Value) return null;

        IReadOnlyDictionary<string, string>? baseEntries = null;
        if (latest is not null)
        {
            baseEntries = treeResolver is not null
                ? await treeResolver.ResolveTreeAsync(latest, ct).ConfigureAwait(false)
                : latest.TreeEntries;

            if (baseEntries is null)
            {
                // Never replay ops onto an empty base — the resolver already logged why (CAS
                // miss/corrupt, or a pre-Phase-2 legacy snapshot with no map at all). Skip
                // compaction this cycle; the accumulated ops stay pending for a later attempt.
                return null;
            }
        }

        var newEntries = ApplyOps(baseEntries, opsSince);
        return await CreateAsync(
            repositoryId, newEntries,
            baseSnapshotId: latest?.SnapshotId,
            source: "Compaction",
            ct: ct).ConfigureAwait(false);
    }

    // Replay ops onto a base tree in chronological order.
    // Add/Replace/Import → set path. Delete → remove path. Rename/Move reserved for Phase 10.
    private static Dictionary<string, string> ApplyOps(
        IReadOnlyDictionary<string, string>? baseEntries,
        IReadOnlyList<RepositoryOperation> ops)
    {
        var tree = baseEntries is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(baseEntries, StringComparer.Ordinal);

        foreach (var op in ops)
        {
            switch (op.Kind)
            {
                case OperationType.Add:
                case OperationType.Replace:
                case OperationType.Import:
                    if (op.NewBlobId is not null) tree[op.Path] = op.NewBlobId;
                    break;
                case OperationType.Delete:
                    tree.Remove(op.Path);
                    break;
            }
        }
        return tree;
    }

    // SHA256 of sorted "path:blobId\n" pairs — same algorithm as RepositoryImportService.
    private static string ComputeTreeHash(IReadOnlyDictionary<string, string> entries)
    {
        var sb = new StringBuilder();
        foreach (var (path, blobId) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            sb.Append(path).Append(':').Append(blobId).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }
}
