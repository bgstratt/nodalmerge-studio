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
/// Phase 2 slice 2.4 (plans/cas-distribution-and-storage.md) — POST
/// /studio/repositories/{repositoryId}/prefetch, the REST trigger for IBlobPrefetchService.
/// Exercises the endpoint through the real DI-wired app (InMemoryBlobStoreProvider standing in for
/// a chained remote-origin provider) rather than unit-testing WorkUnitPrefetchService directly —
/// that's already covered by ScopedTreeFetchTests; this is specifically about the route, body
/// parsing, and response shape.
/// </summary>
[Trait("Category", "Integration")]
public class PrefetchRestEndpointTests
{
    private static WebApplication BuildTestApp(InMemoryBlobStoreProvider blobStore) =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton<IBlobStoreProvider>(blobStore);
            });

    [Fact]
    public async Task PostPrefetch_returns_the_count_of_scoped_blobs_fetched()
    {
        var store = new InMemoryBlobStoreProvider();
        await using var app = BuildTestApp(store);
        await app.StartAsync();
        var client = app.GetTestClient();

        var snapshots = app.Services.GetRequiredService<IRepositorySnapshotService>();
        const string repositoryId = "repo-prefetch-rest";
        var entries = new Dictionary<string, string>
        {
            ["a.txt"] = "aaaa1111111111111111111111111111111111111111111111111111111111111",
            ["b.txt"] = "bbbb2222222222222222222222222222222222222222222222222222222222222",
        };
        foreach (var (_, hash) in entries)
            await store.PutBlobAsync(hash, System.Text.Encoding.UTF8.GetBytes(hash), "text/plain");
        await snapshots.CreateAsync(repositoryId, entries);

        var response = await client.PostAsJsonAsync(
            $"/studio/repositories/{repositoryId}/prefetch",
            new { fileScope = new[] { "a.txt" } });

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, json.GetProperty("fetched").GetInt32());
    }

    [Fact]
    public async Task PostPrefetch_with_no_body_is_a_no_op()
    {
        var store = new InMemoryBlobStoreProvider();
        await using var app = BuildTestApp(store);
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync(
            "/studio/repositories/repo-never-registered/prefetch", content: null);

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, json.GetProperty("fetched").GetInt32());
    }
}
