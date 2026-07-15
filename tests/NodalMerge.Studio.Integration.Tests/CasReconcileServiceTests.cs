using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 2.3 (plans/cas-distribution-and-storage.md Phase 2) — CAS reconcile sweep. Follows the
/// same real-DI bootstrap pattern as WorkspaceCacheManagerLiveBlobHashesTests: a real repository
/// snapshot (via IRepositoryImportService.EnsureBootstrappedAsync) produces a real cas-tree, so
/// ICasReconcileService exercises the actual IWorkspaceCacheManager.GetLiveBlobHashesAsync path
/// rather than a hand-rolled live set.
/// </summary>
[Trait("Category", "Integration")]
public class CasReconcileServiceTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-cas-reconcile-{Guid.NewGuid():N}");
    private readonly InMemoryBlobStoreProvider _blobStore = new();

    public CasReconcileServiceTests()
    {
        Directory.CreateDirectory(_repoPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath)) Directory.Delete(_repoPath, recursive: true);
    }

    private async Task<(ICasReconcileService Reconcile, IWorkspaceCacheManager Cache, IRepositorySnapshotService Snapshots, string RepositoryId)>
        BuildAndBootstrapAsync(FakeRemoteBlobPushTarget? remote)
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "b.txt"), "v1");

        var app = StudioWebApplication.Build([], configureServices: services =>
        {
            services.AddInMemoryStorage();
            services.AddSingleton<IBlobStoreProvider>(_blobStore);
            if (remote is not null)
                services.AddSingleton<IRemoteBlobPushTarget>(remote);
        });

        var import = app.Services.GetRequiredService<IRepositoryImportService>();
        var repositoryId = Path.GetFullPath(_repoPath);
        await import.EnsureBootstrappedAsync(repositoryId, _repoPath);

        return (app.Services.GetRequiredService<ICasReconcileService>(),
                app.Services.GetRequiredService<IWorkspaceCacheManager>(),
                app.Services.GetRequiredService<IRepositorySnapshotService>(),
                repositoryId);
    }

    [Fact]
    public async Task ReconcileAsync_with_no_remote_configured_is_a_zero_result_no_op()
    {
        var (reconcile, _, _, _) = await BuildAndBootstrapAsync(remote: null);

        var result = await reconcile.ReconcileAsync();

        Assert.Equal(0, result.Scanned);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(0, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.MissingLocally);
    }

    [Fact]
    public async Task ReconcileAsync_against_an_empty_remote_pushes_every_live_hash()
    {
        var remote = new FakeRemoteBlobPushTarget();
        var (reconcile, cache, _, _) = await BuildAndBootstrapAsync(remote);
        var live = await cache.GetLiveBlobHashesAsync();

        var result = await reconcile.ReconcileAsync();

        Assert.Equal(live.Count, result.Scanned);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(live.Count, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.MissingLocally);
        foreach (var hash in live)
            Assert.True(await remote.ExistsAsync(hash));
    }

    [Fact]
    public async Task ReconcileAsync_against_a_partially_populated_remote_splits_already_present_and_pushed()
    {
        var remote = new FakeRemoteBlobPushTarget();
        var (reconcile, cache, _, _) = await BuildAndBootstrapAsync(remote);
        var live = await cache.GetLiveBlobHashesAsync();
        Assert.True(live.Count >= 2, "test needs at least two live hashes to seed one and leave the rest missing");

        var seeded = live.First();
        remote.Seed(seeded);

        var result = await reconcile.ReconcileAsync();

        Assert.Equal(live.Count, result.Scanned);
        Assert.Equal(1, result.AlreadyPresent);
        Assert.Equal(live.Count - 1, result.Pushed);
        Assert.Equal(0, result.Failed);
        Assert.Equal(0, result.MissingLocally);
    }

    [Fact]
    public async Task ReconcileAsync_counts_failing_pushes_and_still_completes_the_sweep()
    {
        var remote = new FakeRemoteBlobPushTarget();
        var (reconcile, cache, _, _) = await BuildAndBootstrapAsync(remote);
        var live = await cache.GetLiveBlobHashesAsync();
        Assert.True(live.Count >= 2, "test needs at least two live hashes to see a partial failure");

        remote.FailFirstN = 1;

        var result = await reconcile.ReconcileAsync();

        Assert.Equal(live.Count, result.Scanned);
        Assert.Equal(1, result.Failed);
        Assert.Equal(live.Count - 1, result.Pushed);
        Assert.Equal(0, result.AlreadyPresent);
        Assert.Equal(0, result.MissingLocally);
    }

    [Fact]
    public async Task ReconcileAsync_counts_a_live_hash_missing_from_both_local_and_remote_without_throwing()
    {
        var remote = new FakeRemoteBlobPushTarget();
        var (reconcile, cache, snapshots, repositoryId) = await BuildAndBootstrapAsync(remote);
        var live = await cache.GetLiveBlobHashesAsync();

        var snapshot = await snapshots.GetLatestAsync(repositoryId);
        Assert.NotNull(snapshot);

        // A file blob hash (not the tree root itself) — removing it from the local store does not
        // break tree resolution (leaf file bytes are never fetched to compute the live set, only
        // referenced by hash — see SnapshotTreeResolver's own walk), so GetLiveBlobHashesAsync
        // still reports it live, exactly the "gone everywhere but the tree still points at it"
        // scenario this counter exists for.
        var fileHash = live.First(h => h != snapshot!.TreeHash);
        Assert.True(_blobStore.Remove(fileHash));

        var result = await reconcile.ReconcileAsync();

        Assert.Equal(live.Count, result.Scanned);
        Assert.Equal(1, result.MissingLocally);
        Assert.Equal(live.Count - 1, result.Pushed);
        Assert.Equal(0, result.Failed);
    }

    [Fact]
    public async Task ReconcileAsync_throws_when_the_live_set_cannot_be_computed_fail_closed()
    {
        var remote = new FakeRemoteBlobPushTarget();
        var (reconcile, _, snapshots, repositoryId) = await BuildAndBootstrapAsync(remote);

        var snapshot = await snapshots.GetLatestAsync(repositoryId);
        Assert.NotNull(snapshot);
        Assert.True(_blobStore.Remove(snapshot!.TreeHash));

        await Assert.ThrowsAsync<InvalidOperationException>(() => reconcile.ReconcileAsync());
    }
}
