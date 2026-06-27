using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class CausalGraphRestEndpointTests
{
    private static WebApplication BuildTestApp(IStudioCausalGraphService stub) =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(stub);
            });

    // ── Frontier ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFrontier_returns_empty_array_when_no_promoted_nodes()
    {
        var stub = new StubCausalGraphService();
        await using var app = BuildTestApp(stub);
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/studio/causal/frontier");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, doc.GetProperty("frontierHeads").GetArrayLength());
    }

    [Fact]
    public async Task GetFrontier_returns_heads_when_graph_is_promoted()
    {
        var head = new string('a', 64);
        var stub = new StubCausalGraphService { FrontierHeads = [head] };
        await using var app = BuildTestApp(stub);
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/studio/causal/frontier");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var heads = doc.GetProperty("frontierHeads");
        Assert.Equal(1, heads.GetArrayLength());
        Assert.Equal(head, heads[0].GetString());
    }

    // ── Causal parents ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetCausalParents_returns_not_found_for_unknown_node()
    {
        var stub = new StubCausalGraphService();
        await using var app = BuildTestApp(stub);
        await app.StartAsync();
        var client = app.GetTestClient();

        var nodeId = new string('b', 64);
        var response = await client.GetAsync($"/studio/causal/parents/{nodeId}");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.False(doc.GetProperty("nodeFound").GetBoolean());
        Assert.Equal(0, doc.GetProperty("parentIds").GetArrayLength());
    }

    [Fact]
    public async Task GetCausalParents_returns_parents_for_known_node()
    {
        var parentId = new string('c', 64);
        var nodeId = new string('d', 64);
        var stub = new StubCausalGraphService
        {
            ParentsResult = new CausalParentsResult([parentId], NodeFound: true)
        };
        await using var app = BuildTestApp(stub);
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync($"/studio/causal/parents/{nodeId}");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.True(doc.GetProperty("nodeFound").GetBoolean());
        Assert.Equal(parentId, doc.GetProperty("parentIds")[0].GetString());
    }

    // ── Canonical resolution ──────────────────────────────────────────────────

    [Fact]
    public async Task GetCanonicalResolution_returns_empty_when_not_promoted()
    {
        var stub = new StubCausalGraphService();
        await using var app = BuildTestApp(stub);
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/studio/causal/resolution");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(0, doc.GetProperty("entryCount").GetInt32());
    }

    [Fact]
    public async Task GetCanonicalResolution_returns_entries_after_promotion()
    {
        var stub = new StubCausalGraphService
        {
            ResolutionResult = new CanonicalResolutionResult(
                [new CanonicalResolutionEntry("my-key", "dmFsdWU=")])
        };
        await using var app = BuildTestApp(stub);
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/studio/causal/resolution");
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, doc.GetProperty("entryCount").GetInt32());
        var entry = doc.GetProperty("entries")[0];
        Assert.Equal("my-key", entry.GetProperty("key").GetString());
        Assert.Equal("dmFsdWU=", entry.GetProperty("valueBytesB64").GetString());
    }

    // ── Sync diff ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeSyncDiff_returns_all_server_nodes_when_peer_is_empty()
    {
        var serverNode = new string('e', 64);
        var stub = new StubCausalGraphService
        {
            SyncDiffResult = new SyncDiffResult([serverNode], [])
        };
        await using var app = BuildTestApp(stub);
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/studio/causal/sync-diff",
            new { peerNodeIdsHex = Array.Empty<string>() });
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(1, doc.GetProperty("onlyInServer").GetArrayLength());
        Assert.Equal(serverNode, doc.GetProperty("onlyInServer")[0].GetString());
        Assert.Equal(0, doc.GetProperty("onlyInPeer").GetArrayLength());
    }

    // ── Stub ──────────────────────────────────────────────────────────────────

    private sealed class StubCausalGraphService : IStudioCausalGraphService
    {
        public string[] FrontierHeads { get; init; } = [];
        public CausalParentsResult ParentsResult { get; init; } = new([], false);
        public CanonicalResolutionResult ResolutionResult { get; init; } = new([]);
        public SyncDiffResult SyncDiffResult { get; init; } = new([], []);

        public Task<string[]> GetFrontierAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(FrontierHeads);

        public Task<CausalParentsResult> GetCausalParentsAsync(string nodeIdHex, CancellationToken cancellationToken = default)
            => Task.FromResult(ParentsResult);

        public Task<CanonicalResolutionResult> GetCanonicalResolutionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ResolutionResult);

        public Task<SyncDiffResult> ComputeSyncDiffAsync(string[] peerNodeIdsHex, CancellationToken cancellationToken = default)
            => Task.FromResult(SyncDiffResult);
    }
}
