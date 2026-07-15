using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage.TreeObjects;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 1 slice 1.3 (plans/cas-distribution-and-storage.md) — GetReachableHashesAsync is the seam
/// Phase 5's LiveHashSource is built on: the union of every tree-object hash and file-blob hash
/// reachable from a snapshot's root. Distinct from ResolveTreeAsync's null-on-failure semantics —
/// this one THROWS on partial resolution, because a partial reachable set handed to a GC sweep would
/// look like a valid (smaller) live set and let live blobs get deleted.
/// </summary>
[Trait("Category", "Integration")]
public class SnapshotTreeResolverReachabilityTests
{
    private static RepositorySnapshot MakeCasTreeSnapshot(string treeHash) =>
        new(SnapshotId: "s-reach", RepositoryId: "repo", TreeHash: treeHash, Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree");

    [Fact]
    public async Task GetReachableHashesAsync_returns_exactly_the_root_subtree_and_file_hashes_for_a_two_level_tree()
    {
        var store = new InMemoryBlobStoreProvider();
        var resolver = new SnapshotTreeResolver(store, NullLogger<SnapshotTreeResolver>.Instance);

        // Two-level map: a root-level file, and two files under one subdirectory — hand-computed
        // expectation is {root tree, "src" subtree, 3 file hashes} = 5 hashes, nothing else.
        var entries = new Dictionary<string, string>
        {
            ["README.md"] = "1111111111111111111111111111111111111111111111111111111111111111",
            ["src/main.ts"] = "2222222222222222222222222222222222222222222222222222222222222222",
            ["src/util.ts"] = "3333333333333333333333333333333333333333333333333333333333333333",
        };
        var rootHash = await resolver.WriteTreeAsync(entries);
        Assert.Equal(2, store.Count); // root blob + src/ blob

        var snapshot = MakeCasTreeSnapshot(rootHash);
        var reachable = await resolver.GetReachableHashesAsync(snapshot);

        var expected = new HashSet<string>(entries.Values, StringComparer.Ordinal)
        {
            rootHash,
        };
        // The src/ subtree hash is whatever WriteTreeAsync assigned it — read it back off the store
        // rather than recomputing bytes by hand, since GetTreeBlobHashesAsync already proves that
        // reconstruction elsewhere; here we just need "exactly what the store holds".
        foreach (var hash in store.Hashes) expected.Add(hash);

        Assert.Equal(5, reachable.Count);
        Assert.Equal(expected, reachable);
    }

    [Fact]
    public async Task GetReachableHashesAsync_throws_when_a_subtree_blob_is_missing_fail_closed()
    {
        var store = new InMemoryBlobStoreProvider();
        var resolver = new SnapshotTreeResolver(store, NullLogger<SnapshotTreeResolver>.Instance);

        var entries = new Dictionary<string, string>
        {
            ["README.md"] = "1111111111111111111111111111111111111111111111111111111111111111",
            ["src/main.ts"] = "2222222222222222222222222222222222222222222222222222222222222222",
        };
        var rootHash = await resolver.WriteTreeAsync(entries);

        // Find and remove the "src" subtree blob specifically (not the root) — proves the fail-
        // closed check fires for a miss anywhere in the walk, not just at the root.
        var srcHash = store.Hashes.Single(h => h != rootHash);
        Assert.True(store.Remove(srcHash));

        var snapshot = MakeCasTreeSnapshot(rootHash);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => resolver.GetReachableHashesAsync(snapshot));

        Assert.Contains("s-reach", ex.Message);
        Assert.Contains(srcHash, ex.Message);
    }

    [Fact]
    public async Task GetReachableHashesAsync_returns_only_file_hashes_for_a_legacy_inline_snapshot()
    {
        // Legacy snapshots never touch the blob store — a throwing store proves it.
        var resolver = new SnapshotTreeResolver(
            new ThrowingBlobStoreProviderForReachability(), NullLogger<SnapshotTreeResolver>.Instance);

        var entries = new Dictionary<string, string>
        {
            ["a.txt"] = "aaaa1111111111111111111111111111111111111111111111111111111111111",
            ["b/c.txt"] = "bbbb2222222222222222222222222222222222222222222222222222222222222",
        };
        var snapshot = new RepositorySnapshot(
            SnapshotId: "s-legacy", RepositoryId: "repo", TreeHash: "unused", Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow, TreeEntries: entries);

        var reachable = await resolver.GetReachableHashesAsync(snapshot);

        Assert.Equal(new HashSet<string>(entries.Values, StringComparer.Ordinal), reachable);
    }

    [Fact]
    public async Task GetReachableHashesAsync_returns_empty_for_a_pre_Phase2_snapshot()
    {
        var resolver = new SnapshotTreeResolver(
            new ThrowingBlobStoreProviderForReachability(), NullLogger<SnapshotTreeResolver>.Instance);
        var snapshot = new RepositorySnapshot(
            SnapshotId: "s-pre", RepositoryId: "repo", TreeHash: "unused", Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow);

        var reachable = await resolver.GetReachableHashesAsync(snapshot);

        Assert.Empty(reachable);
    }

    private sealed class ThrowingBlobStoreProviderForReachability : NodalMerge.Host.Abstractions.Providers.IBlobStoreProvider
    {
        public ValueTask<NodalMerge.Host.Abstractions.Providers.BlobReadResult> TryGetBlobAsync(string hashHex, CancellationToken ct = default) =>
            throw new InvalidOperationException("Must not be called for a legacy/pre-Phase-2 snapshot.");
        public ValueTask PutBlobAsync(string hashHex, byte[] bytes, string? contentType, CancellationToken ct = default) =>
            throw new InvalidOperationException("Must not be called by GetReachableHashesAsync in this test.");
    }
}
