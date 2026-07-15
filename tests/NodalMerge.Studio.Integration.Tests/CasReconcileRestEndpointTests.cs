using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 2 slice 2.3 (plans/cas-distribution-and-storage.md) — POST /studio/cas/reconcile, the
/// REST trigger for ICasReconcileService. Follows PrefetchRestEndpointTests.cs's pattern: exercise
/// the real DI-wired app rather than unit-testing CasReconcileService directly (that's
/// CasReconcileServiceTests' job) — this is specifically about the route and response shape,
/// including the /studio/cache/gc-style 409 on a fail-closed live-set computation.
/// </summary>
[Trait("Category", "Integration")]
public class CasReconcileRestEndpointTests
{
    private static WebApplication BuildTestApp(InMemoryBlobStoreProvider blobStore, IRemoteBlobPushTarget? remote) =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton<IBlobStoreProvider>(blobStore);
                if (remote is not null)
                    services.AddSingleton<IRemoteBlobPushTarget>(remote);
            });

    [Fact]
    public async Task PostReconcile_returns_200_with_counts_against_a_configured_remote()
    {
        var store = new InMemoryBlobStoreProvider();
        var remote = new FakeRemoteBlobPushTarget();
        await using var app = BuildTestApp(store, remote);
        await app.StartAsync();
        var client = app.GetTestClient();

        var repoPath = Path.Combine(Path.GetTempPath(), $"studio-cas-reconcile-rest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoPath);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repoPath, "a.txt"), "v1");
            var import = app.Services.GetRequiredService<IRepositoryImportService>();
            var repositoryId = Path.GetFullPath(repoPath);
            await import.EnsureBootstrappedAsync(repositoryId, repoPath);

            var response = await client.PostAsync("/studio/cas/reconcile", content: null);

            response.EnsureSuccessStatusCode();
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Assert.True(json.GetProperty("scanned").GetInt32() > 0);
            Assert.Equal(json.GetProperty("scanned").GetInt32(), json.GetProperty("pushed").GetInt32());
            Assert.Equal(0, json.GetProperty("failed").GetInt32());
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }

    [Fact]
    public async Task PostReconcile_with_no_remote_configured_returns_a_zero_result()
    {
        var store = new InMemoryBlobStoreProvider();
        await using var app = BuildTestApp(store, remote: null);
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync("/studio/cas/reconcile", content: null);

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, json.GetProperty("scanned").GetInt32());
    }

    [Fact]
    public async Task PostReconcile_returns_409_when_a_tree_blob_is_missing_fail_closed()
    {
        var store = new InMemoryBlobStoreProvider();
        var remote = new FakeRemoteBlobPushTarget();
        await using var app = BuildTestApp(store, remote);
        await app.StartAsync();
        var client = app.GetTestClient();

        var repoPath = Path.Combine(Path.GetTempPath(), $"studio-cas-reconcile-rest-409-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoPath);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(repoPath, "a.txt"), "v1");
            var import = app.Services.GetRequiredService<IRepositoryImportService>();
            var repositoryId = Path.GetFullPath(repoPath);
            await import.EnsureBootstrappedAsync(repositoryId, repoPath);

            var snapshots = app.Services.GetRequiredService<IRepositorySnapshotService>();
            var snapshot = await snapshots.GetLatestAsync(repositoryId);
            Assert.NotNull(snapshot);
            Assert.True(store.Remove(snapshot!.TreeHash));

            var response = await client.PostAsync("/studio/cas/reconcile", content: null);

            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
        }
    }
}
