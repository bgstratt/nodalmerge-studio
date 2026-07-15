using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage.TreeObjects;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 1.1 (plans/cas-distribution-and-storage.md Phase 1) — SnapshotTreeResolver is the one
/// place that turns a RepositorySnapshot into a path→blobId map, whichever of the three shapes
/// it's in (legacy inline, cas-tree, or pre-Phase-2). These tests exercise it directly, without a
/// full Host pipeline, for each precedence branch.
/// </summary>
[Trait("Category", "Integration")]
public class SnapshotTreeResolverTests
{
    // Proves ResolveTreeAsync never touches the blob store for a legacy inline snapshot — any call
    // to either method is itself a test failure.
    private sealed class ThrowingBlobStoreProvider : IBlobStoreProvider
    {
        public ValueTask<BlobReadResult> TryGetBlobAsync(string hashHex, CancellationToken ct = default) =>
            throw new InvalidOperationException("TryGetBlobAsync must not be called for an inline-legacy snapshot.");
        public ValueTask PutBlobAsync(string hashHex, byte[] bytes, string? contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("PutBlobAsync must not be called by ResolveTreeAsync.");
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<string> Warnings { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    private static RepositorySnapshot MakeLegacySnapshot(IReadOnlyDictionary<string, string> entries) =>
        new(SnapshotId: "s-legacy", RepositoryId: "repo", TreeHash: "unused", Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow, TreeEntries: entries);

    private static RepositorySnapshot MakePrePhase2Snapshot() =>
        new(SnapshotId: "s-pre", RepositoryId: "repo", TreeHash: "unused", Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task WriteTreeAsync_then_ResolveTreeAsync_round_trips_the_entries()
    {
        var store = new InMemoryBlobStoreProvider();
        var resolver = new SnapshotTreeResolver(store, NullLogger<SnapshotTreeResolver>.Instance);
        var entries = new Dictionary<string, string> { ["a.txt"] = "h1", ["b/c.txt"] = "h2" };

        var hash = await resolver.WriteTreeAsync(entries);
        var snapshot = new RepositorySnapshot(
            SnapshotId: "s1", RepositoryId: "repo", TreeHash: hash, Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree");

        var resolved = await resolver.ResolveTreeAsync(snapshot);

        Assert.NotNull(resolved);
        Assert.Equal(
            entries.OrderBy(kv => kv.Key, StringComparer.Ordinal),
            resolved!.OrderBy(kv => kv.Key, StringComparer.Ordinal));
    }

    [Fact]
    public async Task ResolveTreeAsync_returns_the_inline_map_directly_without_touching_the_store()
    {
        var resolver = new SnapshotTreeResolver(new ThrowingBlobStoreProvider(), NullLogger<SnapshotTreeResolver>.Instance);
        var entries = new Dictionary<string, string> { ["a.txt"] = "h1" };
        var snapshot = MakeLegacySnapshot(entries);

        var resolved = await resolver.ResolveTreeAsync(snapshot);

        Assert.Same(entries, resolved); // returned directly — no CAS fetch, no copy
    }

    [Fact]
    public async Task ResolveTreeAsync_returns_null_for_a_pre_Phase2_snapshot_with_neither_field_set()
    {
        var resolver = new SnapshotTreeResolver(new InMemoryBlobStoreProvider(), NullLogger<SnapshotTreeResolver>.Instance);
        var snapshot = MakePrePhase2Snapshot();

        var resolved = await resolver.ResolveTreeAsync(snapshot);

        Assert.Null(resolved);
    }

    [Fact]
    public async Task ResolveTreeAsync_returns_null_and_logs_a_warning_on_a_CAS_miss()
    {
        var store = new InMemoryBlobStoreProvider();
        var logger = new RecordingLogger<SnapshotTreeResolver>();
        var resolver = new SnapshotTreeResolver(store, logger);
        var snapshot = new RepositorySnapshot(
            SnapshotId: "s-miss", RepositoryId: "repo",
            TreeHash: "0000000000000000000000000000000000000000000000000000000000000000",
            Generation: 0, CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree");

        var resolved = await resolver.ResolveTreeAsync(snapshot);

        Assert.Null(resolved);
        Assert.Contains(logger.Warnings, w => w.Contains("missing", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ResolveTreeAsync_returns_null_and_logs_a_distinct_warning_for_a_corrupt_blob()
    {
        var store = new InMemoryBlobStoreProvider();
        var logger = new RecordingLogger<SnapshotTreeResolver>();
        var resolver = new SnapshotTreeResolver(store, logger);

        var corruptBytes = System.Text.Encoding.UTF8.GetBytes("not a tree object at all");
        var corruptHash = NodalMerge.Studio.Storage.BlobHasher.ComputeHash(corruptBytes);
        await store.PutBlobAsync(corruptHash, corruptBytes, "text/plain");

        var snapshot = new RepositorySnapshot(
            SnapshotId: "s-corrupt", RepositoryId: "repo", TreeHash: corruptHash,
            Generation: 0, CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree");

        var resolved = await resolver.ResolveTreeAsync(snapshot);

        Assert.Null(resolved);
        Assert.Contains(logger.Warnings, w => w.Contains("corrupt", StringComparison.OrdinalIgnoreCase));
        // Distinct from the CAS-miss message — never the same wording as the missing-blob case.
        Assert.DoesNotContain(logger.Warnings, w => w.Contains("missing from the blob store", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task GetTreeBlobHashesAsync_returns_just_the_root_for_a_v1_tree()
    {
        var store = new InMemoryBlobStoreProvider();
        var resolver = new SnapshotTreeResolver(store, NullLogger<SnapshotTreeResolver>.Instance);
        var entries = new Dictionary<string, string> { ["a.txt"] = "h1" };
        var hash = await resolver.WriteTreeAsync(entries);
        var snapshot = new RepositorySnapshot(
            SnapshotId: "s1", RepositoryId: "repo", TreeHash: hash, Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree");

        var hashes = await resolver.GetTreeBlobHashesAsync(snapshot);

        Assert.Single(hashes);
        Assert.Contains(hash, hashes);
    }

    [Fact]
    public void CanWrite_is_false_without_a_blob_store()
    {
        var resolver = new SnapshotTreeResolver(null, NullLogger<SnapshotTreeResolver>.Instance);
        Assert.False(resolver.CanWrite);
    }

    [Fact]
    public async Task WriteTreeAsync_throws_when_CanWrite_is_false()
    {
        var resolver = new SnapshotTreeResolver(null, NullLogger<SnapshotTreeResolver>.Instance);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.WriteTreeAsync(new Dictionary<string, string>()));
    }

    [Fact]
    public async Task ResolveTreeAsync_without_a_blob_store_returns_null_for_a_cas_tree_snapshot()
    {
        var resolver = new SnapshotTreeResolver(null, NullLogger<SnapshotTreeResolver>.Instance);
        var snapshot = new RepositorySnapshot(
            SnapshotId: "s1", RepositoryId: "repo", TreeHash: "somehash", Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree");

        var resolved = await resolver.ResolveTreeAsync(snapshot);

        Assert.Null(resolved);
    }
}
