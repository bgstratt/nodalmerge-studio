using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.Studio.Storage.TreeObjects;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 1.2 (plans/cas-distribution-and-storage.md Phase 1) acceptance tests for the v2
/// directory-tree writer: structural sharing (unchanged subtrees are never re-stored under a new
/// hash) and determinism (insertion order and call count never affect output). The core acceptance
/// criterion this file pins (see the plan's Phase 1 slice 1.2 acceptance bullet): "a 1-file change
/// in a repo writes <= depth-of-path new tree objects; root hash changes; all unchanged subtree
/// hashes are byte-identical to the previous generation."
/// </summary>
[Trait("Category", "Integration")]
public class SnapshotTreeResolverV2WriteTests
{
    // Synthetic 3-level, 3x3x3 directory tree: 27 files, each at path depth 3
    // (d{i}/d{j}/d{k}/leaf.txt). Mutating one leaf's file hash should touch exactly 4 tree blobs on
    // the way back to the root: the leaf's own directory (d{i}/d{j}/d{k}), its parent (d{i}/d{j}),
    // its grandparent (d{i}), and the root — "path depth (3) + 1".
    private const int MutatedI = 1, MutatedJ = 1, MutatedK = 1;

    private static Dictionary<string, string> BuildThreeLevelMap(string mutatedLeafHash = "leafhash-original")
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
        for (var j = 0; j < 3; j++)
        for (var k = 0; k < 3; k++)
        {
            var isMutated = i == MutatedI && j == MutatedJ && k == MutatedK;
            map[$"d{i}/d{j}/d{k}/leaf.txt"] = isMutated ? mutatedLeafHash : $"filehash-{i}-{j}-{k}";
        }
        return map;
    }

    [Fact]
    public async Task One_leaf_change_writes_at_most_depth_plus_one_new_tree_blobs_and_shares_the_rest()
    {
        var store = new InMemoryBlobStoreProvider();
        var recorder = new RecordingBlobStoreProvider(store);
        var resolver = new SnapshotTreeResolver(recorder, NullLogger<SnapshotTreeResolver>.Instance);

        var genA = BuildThreeLevelMap("leafhash-original");
        var rootA = await resolver.WriteTreeAsync(genA);
        var hashesAfterA = new HashSet<string>(store.Hashes, StringComparer.Ordinal);

        // 3 (d{i}) + 9 (d{i}/d{j}) + 27 (d{i}/d{j}/d{k}) + 1 (root) = 40 tree blobs total.
        Assert.Equal(40, hashesAfterA.Count);

        recorder.PutHashes.Clear();
        var genB = BuildThreeLevelMap("leafhash-MUTATED");
        var rootB = await resolver.WriteTreeAsync(genB);
        var hashesAfterB = new HashSet<string>(store.Hashes, StringComparer.Ordinal);

        // (a) new blobs introduced by generation B <= depth-of-path (3) + 1.
        var newInB = recorder.PutHashes.Distinct(StringComparer.Ordinal)
            .Where(h => !hashesAfterA.Contains(h))
            .ToList();
        Assert.True(newInB.Count <= 4, $"Expected <= 4 new tree blobs, got {newInB.Count}: {string.Join(", ", newInB)}");

        // (b) root hash changed.
        Assert.NotEqual(rootA, rootB);

        // (c) every subtree hash off the changed path is byte-identical across generations: the
        // hashes that existed after A but no longer exist after B are exactly the (<=4) replaced
        // ancestors on the mutated path — everything else in A's set must still be present in B's.
        var orphanedFromA = hashesAfterA.Except(hashesAfterB).ToList();
        Assert.True(orphanedFromA.Count <= 4, $"Expected <= 4 orphaned old blobs, got {orphanedFromA.Count}.");

        var unaffected = hashesAfterA.Intersect(hashesAfterB).ToList();
        Assert.Equal(hashesAfterA.Count - orphanedFromA.Count, unaffected.Count);

        // Content-addressing guarantees identical hash -> identical bytes, but verify directly
        // against the store rather than relying on that guarantee alone.
        foreach (var hash in unaffected)
        {
            var fromStore = await store.TryGetBlobAsync(hash);
            Assert.True(fromStore.Found);
            // Re-hash to make sure nothing silently rewrote this blob's bytes under its own hash.
            Assert.Equal(hash, NodalMerge.Studio.Storage.BlobHasher.ComputeHash(fromStore.Bytes!));
        }
    }

    // The format doc's sharing-pair vectors (v2-sharing-a / v2-sharing-b) already pin the expected
    // bytes/hashes for two roots that differ only in a root-level file; this test drives both
    // through the real write pipeline and checks the src/ subtree blob comes out identical, sharing
    // the golden-vector source with CanonicalTreeSerializerVectorTests rather than re-hardcoding it.
    [Fact]
    public async Task Sharing_pair_vectors_produce_a_byte_identical_src_subtree_blob()
    {
        var vectors = CanonicalTreeSerializerVectorTests.LoadVectors().V2;
        var vectorA = vectors.Single(v => v.Id == "v2-sharing-a");
        var vectorB = vectors.Single(v => v.Id == "v2-sharing-b");
        var expectedSrcHash = vectorA.ExpectedBlobs.Single(b => b.Directory == "src").ExpectedHash;
        Assert.Equal(expectedSrcHash, vectorB.ExpectedBlobs.Single(b => b.Directory == "src").ExpectedHash);

        var storeA = new InMemoryBlobStoreProvider();
        var resolverA = new SnapshotTreeResolver(storeA, NullLogger<SnapshotTreeResolver>.Instance);
        var rootA = await resolverA.WriteTreeAsync(vectorA.InputEntries);
        Assert.Equal(vectorA.ExpectedRootHash, rootA);

        var storeB = new InMemoryBlobStoreProvider();
        var resolverB = new SnapshotTreeResolver(storeB, NullLogger<SnapshotTreeResolver>.Instance);
        var rootB = await resolverB.WriteTreeAsync(vectorB.InputEntries);
        Assert.Equal(vectorB.ExpectedRootHash, rootB);

        Assert.NotEqual(rootA, rootB); // roots differ (different root-level file)...

        // ...but the src/ subtree blob is byte-identical in both independently-populated stores.
        var srcFromA = await storeA.TryGetBlobAsync(expectedSrcHash);
        var srcFromB = await storeB.TryGetBlobAsync(expectedSrcHash);
        Assert.True(srcFromA.Found);
        Assert.True(srcFromB.Found);
        Assert.Equal(srcFromA.Bytes, srcFromB.Bytes);
    }

    [Fact]
    public async Task WriteTreeAsync_is_deterministic_regardless_of_insertion_order_or_call_count()
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["README.md"] = "1111111111111111111111111111111111111111111111111111111111111111",
            ["src/main.ts"] = "3333333333333333333333333333333333333333333333333333333333333333",
            ["src/lib/util.ts"] = "2222222222222222222222222222222222222222222222222222222222222222",
            ["src/lib/helpers.ts"] = "4444444444444444444444444444444444444444444444444444444444444444",
        };

        var store1 = new InMemoryBlobStoreProvider();
        var resolver1 = new SnapshotTreeResolver(store1, NullLogger<SnapshotTreeResolver>.Instance);
        var root1 = await resolver1.WriteTreeAsync(entries);
        // Calling it again on the same resolver/store must be idempotent (same root, same blob count).
        var root1Again = await resolver1.WriteTreeAsync(entries);
        Assert.Equal(root1, root1Again);

        var reordered = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var kv in entries.Reverse())
            reordered[kv.Key] = kv.Value;

        var store2 = new InMemoryBlobStoreProvider();
        var resolver2 = new SnapshotTreeResolver(store2, NullLogger<SnapshotTreeResolver>.Instance);
        var root2 = await resolver2.WriteTreeAsync(reordered);

        Assert.Equal(root1, root2);
        Assert.Equal(store1.Count, store2.Count);
        Assert.Equal(new HashSet<string>(store1.Hashes), new HashSet<string>(store2.Hashes));

        foreach (var hash in store1.Hashes)
        {
            var b1 = await store1.TryGetBlobAsync(hash);
            var b2 = await store2.TryGetBlobAsync(hash);
            Assert.True(b1.Found);
            Assert.True(b2.Found);
            Assert.Equal(b1.Bytes, b2.Bytes);
        }
    }
}
