using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;
using NodalMerge.Studio.Storage.TreeObjects;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 2 slice 2.4 (plans/cas-distribution-and-storage.md) — scoped fetch + prefetch. Canonical
/// acceptance: "Materializing a 40-file scope in a 5,000-file repo fetches ≤ 40 file blobs + the
/// tree path." These tests use a smaller synthetic repo (125 files, 5x5x5 nested) but pin the same
/// shape of bound: a scoped resolve/materialize/prefetch must never touch anywhere near the whole
/// tree or file set.
/// </summary>
[Trait("Category", "Integration")]
public class ScopedTreeFetchTests
{
    // 5x5x5 nested directories (d{i}/d{j}/d{k}/leaf.txt), 125 files at depth 3. Each distinct
    // (i, j, k) leaf's own file-blob hash is deterministic and unique, so scope membership is
    // trivially checkable by looking the hash up in the map.
    private static Dictionary<string, string> BuildNestedMap()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < 5; i++)
        for (var j = 0; j < 5; j++)
        for (var k = 0; k < 5; k++)
            map[$"d{i}/d{j}/d{k}/leaf.txt"] = $"filehash-{i}-{j}-{k}-000000000000000000000000000000";
        return map;
    }

    // 5 files, each on its own distinct root->d{i}->d{i}/d{i}->d{i}/d{i}/d{i} path (i.e. j=k=i) —
    // so resolving the scope requires walking 5 distinct edges at each of the 3 directory levels
    // (d{i}, d{i}/d{j}, d{i}/d{j}/d{k}) below the (shared) root: exactly "depth (3) × scoped dirs
    // (5) + root (1)" = 16 tree-blob fetches, if pruning works. No pruning (a naive full resolve)
    // would instead need every tree blob in the repo: root(1) + 5 + 25 + 125 = 156 — the two are
    // far enough apart that a regression collapsing the scoped walk into a full walk cannot pass
    // unnoticed.
    private static readonly string[] Scope =
        [.. Enumerable.Range(0, 5).Select(i => $"d{i}/d{i}/d{i}/leaf.txt")];

    private static RepositorySnapshot MakeSnapshot(string rootHash) =>
        new(SnapshotId: "s-scoped", RepositoryId: "repo", TreeHash: rootHash, Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree");

    [Fact]
    public async Task ResolveTreeAsync_with_scope_returns_exactly_the_in_scope_entries_and_bounds_tree_fetches()
    {
        var store = new InMemoryBlobStoreProvider();
        var recorder = new RecordingBlobStoreProvider(store);
        var resolver = new SnapshotTreeResolver(recorder, NullLogger<SnapshotTreeResolver>.Instance);

        var map = BuildNestedMap();
        var rootHash = await resolver.WriteTreeAsync(map);
        // root(1) + d{i}(5) + d{i}/d{j}(25) + d{i}/d{j}/d{k}(125) = 156 tree blobs total.
        Assert.Equal(156, store.Count);

        var snapshot = MakeSnapshot(rootHash);
        recorder.GetHashes.Clear();

        var resolved = await resolver.ResolveTreeAsync(snapshot, Scope);

        Assert.NotNull(resolved);
        Assert.Equal(Scope.Length, resolved!.Count);
        foreach (var path in Scope)
            Assert.Equal(map[path], resolved[path]);

        // Bound: at most depth(3) * scopedDirs(5) + root(1) = 16 tree-blob GETs — nowhere near the
        // 156 a full unscoped walk would need.
        Assert.True(recorder.GetHashes.Count <= 16,
            $"Expected <= 16 tree-blob fetches for the scoped walk, got {recorder.GetHashes.Count}.");
    }

    [Fact]
    public async Task MaterializeAsync_on_a_cold_cache_fetches_at_most_scoped_file_count_plus_tree_path()
    {
        var store = new InMemoryBlobStoreProvider();
        var recorder = new RecordingBlobStoreProvider(store);
        var resolver = new SnapshotTreeResolver(recorder, NullLogger<SnapshotTreeResolver>.Instance);

        var map = BuildNestedMap();
        var rootHash = await resolver.WriteTreeAsync(map);

        // Store dummy content for every file blob so a cold-cache materialize can actually fetch
        // and write it (content is irrelevant to this test — only fetch counts are asserted).
        foreach (var (path, hash) in map)
            await store.PutBlobAsync(hash, System.Text.Encoding.UTF8.GetBytes(path), "text/plain");

        var snapshot = MakeSnapshot(rootHash);
        var materializer = new MaterializationEngine(recorder, new WorkspaceOptions(), resolver);

        var targetDir = Path.Combine(Path.GetTempPath(), $"scoped-materialize-{Guid.NewGuid():N}");
        Directory.CreateDirectory(targetDir);
        try
        {
            recorder.GetHashes.Clear();
            var written = await materializer.MaterializeAsync(snapshot, targetDir, Scope);

            Assert.Equal(Scope.Length, written);
            foreach (var path in Scope)
                Assert.True(File.Exists(Path.Combine(targetDir, path.Replace('/', Path.DirectorySeparatorChar))));

            // Bound: scoped file count (5) + tree path (<= 16, per the resolve test above) = 21.
            Assert.True(recorder.GetHashes.Count <= Scope.Length + 16,
                $"Expected <= {Scope.Length + 16} total fetches (tree + file), got {recorder.GetHashes.Count}.");
        }
        finally
        {
            Directory.Delete(targetDir, recursive: true);
        }
    }

    [Fact]
    public async Task PrefetchScopeAsync_warms_exactly_the_scoped_file_blobs()
    {
        var store = new InMemoryBlobStoreProvider();
        var recorder = new RecordingBlobStoreProvider(store);
        var resolver = new SnapshotTreeResolver(recorder, NullLogger<SnapshotTreeResolver>.Instance);

        var map = BuildNestedMap();
        foreach (var (path, hash) in map)
            await store.PutBlobAsync(hash, System.Text.Encoding.UTF8.GetBytes(path), "text/plain");

        var nodeStore = new InMemoryStudioNodeStore();
        var snapshotService = new InMemoryRepositorySnapshotService(nodeStore, null, resolver);
        const string repositoryId = "repo-prefetch-scoped";
        await snapshotService.CreateAsync(repositoryId, map);

        var prefetch = new WorkUnitPrefetchService(snapshotService, resolver, recorder);
        recorder.GetHashes.Clear();

        var fetched = await prefetch.PrefetchScopeAsync(repositoryId, Scope);

        Assert.Equal(Scope.Length, fetched);

        var scopedHashes = new HashSet<string>(Scope.Select(p => map[p]), StringComparer.Ordinal);
        var outOfScopeHashes = new HashSet<string>(
            map.Where(kv => !Scope.Contains(kv.Key)).Select(kv => kv.Value), StringComparer.Ordinal);

        var fetchedFileHashes = recorder.GetHashes.Where(h => scopedHashes.Contains(h) || outOfScopeHashes.Contains(h)).ToList();
        Assert.Equal(scopedHashes.Count, fetchedFileHashes.Count);
        Assert.Equal(scopedHashes, new HashSet<string>(fetchedFileHashes, StringComparer.Ordinal));
        Assert.DoesNotContain(recorder.GetHashes, h => outOfScopeHashes.Contains(h));
    }

    [Fact]
    public async Task PrefetchScopeAsync_returns_zero_when_there_is_no_snapshot_yet()
    {
        var store = new InMemoryBlobStoreProvider();
        var resolver = new SnapshotTreeResolver(store, NullLogger<SnapshotTreeResolver>.Instance);
        var nodeStore = new InMemoryStudioNodeStore();
        var snapshotService = new InMemoryRepositorySnapshotService(nodeStore, null, resolver);
        var prefetch = new WorkUnitPrefetchService(snapshotService, resolver, store);

        var fetched = await prefetch.PrefetchScopeAsync("repo-never-registered", ["a.txt"]);

        Assert.Equal(0, fetched);
    }

    [Fact]
    public async Task PrefetchScopeAsync_returns_zero_when_fileScope_is_null_or_empty()
    {
        var store = new InMemoryBlobStoreProvider();
        var resolver = new SnapshotTreeResolver(store, NullLogger<SnapshotTreeResolver>.Instance);
        var nodeStore = new InMemoryStudioNodeStore();
        var snapshotService = new InMemoryRepositorySnapshotService(nodeStore, null, resolver);
        const string repositoryId = "repo-empty-scope";
        await snapshotService.CreateAsync(repositoryId, BuildNestedMap());
        var prefetch = new WorkUnitPrefetchService(snapshotService, resolver, store);

        Assert.Equal(0, await prefetch.PrefetchScopeAsync(repositoryId, null));
        Assert.Equal(0, await prefetch.PrefetchScopeAsync(repositoryId, []));
    }
}
