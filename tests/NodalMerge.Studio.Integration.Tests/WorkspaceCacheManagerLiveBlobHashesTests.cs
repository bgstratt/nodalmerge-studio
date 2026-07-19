using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;
using NodalMerge.Studio.Storage.TreeObjects;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 1.1 (plans/cas-distribution-and-storage.md Phase 1) — GetLiveBlobHashesAsync's live set
/// must include tree-object blobs (a cas-tree snapshot's root, not just its file blobs), and must
/// FAIL CLOSED (throw, not silently under-report) when a cas-tree snapshot's tree can't be resolved
/// — an incomplete live set is unsafe to hand to a GC sweep.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceCacheManagerLiveBlobHashesTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-live-hashes-{Guid.NewGuid():N}");
    private readonly InMemoryBlobStoreProvider _blobStore = new();

    public WorkspaceCacheManagerLiveBlobHashesTests()
    {
        Directory.CreateDirectory(_repoPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath)) Directory.Delete(_repoPath, recursive: true);
    }

    private async Task<(IWorkspaceCacheManager Cache, IRepositorySnapshotService Snapshots, ISnapshotTreeResolver TreeResolver, string RepositoryId)>
        BuildAndBootstrapAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");

        var app = StudioWebApplication.Build([], configureServices: services =>
        {
            services.AddInMemoryStorage();
            services.AddSingleton<IBlobStoreProvider>(_blobStore);
        });

        var import = app.Services.GetRequiredService<IRepositoryImportService>();
        var repositoryId = Path.GetFullPath(_repoPath);
        await import.EnsureBootstrappedAsync(repositoryId, _repoPath);

        return (app.Services.GetRequiredService<IWorkspaceCacheManager>(),
                app.Services.GetRequiredService<IRepositorySnapshotService>(),
                app.Services.GetRequiredService<ISnapshotTreeResolver>(),
                repositoryId);
    }

    [Fact]
    public async Task GetLiveBlobHashesAsync_includes_the_tree_blob_hash_for_a_cas_tree_snapshot()
    {
        var (cache, snapshots, _, repositoryId) = await BuildAndBootstrapAsync();
        var snapshot = await snapshots.GetLatestAsync(repositoryId);
        Assert.NotNull(snapshot);
        Assert.Equal("cas-tree", snapshot!.TreeFormat);

        var live = await cache.GetLiveBlobHashesAsync();

        Assert.Contains(snapshot.TreeHash, live);
    }

    [Fact]
    public async Task GetLiveBlobHashesAsync_throws_when_the_tree_blob_is_missing_fail_closed()
    {
        var (cache, snapshots, _, repositoryId) = await BuildAndBootstrapAsync();
        var snapshot = await snapshots.GetLatestAsync(repositoryId);
        Assert.NotNull(snapshot);

        // Simulate a CAS miss for the tree blob (e.g. eviction, corruption, a partially-synced peer).
        Assert.True(_blobStore.Remove(snapshot!.TreeHash));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetLiveBlobHashesAsync());
    }

    // Slice 1.3 — GetLiveBlobHashesAsync now goes through GetReachableHashesAsync, whose fail-closed
    // check must fire for a miss anywhere in the walk, not just at the root — a subdirectory's tree
    // blob is exactly as load-bearing for GC safety as the root's.
    [Fact]
    public async Task GetLiveBlobHashesAsync_throws_when_a_nested_subtree_blob_is_missing()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "b.txt"), "v1");
        Directory.CreateDirectory(Path.Combine(_repoPath, "src"));
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "src", "main.txt"), "v1");

        var (cache, snapshots, _, repositoryId) = await BuildAndBootstrapAsync();
        var snapshot = await snapshots.GetLatestAsync(repositoryId);
        Assert.NotNull(snapshot);
        Assert.Equal("cas-tree", snapshot!.TreeFormat);

        // A *separate* resolver instance over the same store — walking via the app's own
        // (DI-singleton) resolver here would prime its fragment/doc caches with the subtree we're
        // about to delete, masking the very miss this test exists to catch (the cache would answer
        // from memory instead of hitting the now-empty store).
        var scratchResolver = new SnapshotTreeResolver(_blobStore, Microsoft.Extensions.Logging.Abstractions.NullLogger<SnapshotTreeResolver>.Instance);
        var treeBlobHashes = await scratchResolver.GetTreeBlobHashesAsync(snapshot);
        var subtreeHash = treeBlobHashes.Single(h => h != snapshot.TreeHash);
        Assert.True(_blobStore.Remove(subtreeHash));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetLiveBlobHashesAsync());
    }
}
