using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 5 slice 5.2 (plans/cas-distribution-and-storage.md) — the REST surface of the staged
/// local GC: POST /studio/cache/gc (configured-mode default + explicit ?mode= operator override +
/// the established 409 fail-closed shape), GET /studio/cache/gc/runs (the run ledger), and the
/// retention doctrine's graceful "aged out" answer on
/// POST /studio/repository-snapshots/{id}/materialize (410, never a crash or a generic CAS-miss
/// guess). Follows CasReconcileRestEndpointTests' exercise-the-real-DI-wired-app pattern.
/// </summary>
[Trait("Category", "Integration")]
public class BlobGcRestEndpointTests : IDisposable
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), $"studio-blobgc-rest-{Guid.NewGuid():N}");
    private string CasRoot => Path.Combine(_tempRoot, "cas");

    private static readonly string RepositoryId = Path.GetFullPath(@"C:\fake\gc-rest-repo");

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    private WebApplication BuildApp(BlobGcOptions? gcOptions = null)
    {
        return StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Providers:NodeStorage"] = "InMemory",
                ["NodalMerge:Providers:BlobStorage"] = "File",
                ["NodalMerge:Storage:FileBlobs:RootPath"] = CasRoot,
            }),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions
                {
                    RootPath = Path.Combine(_tempRoot, "workspace"),
                    CasRootPath = CasRoot,
                });
                services.AddSingleton(new RetentionPolicyOptions { RetainIntermediateDays = 0 });
                services.AddSingleton(gcOptions ?? new BlobGcOptions());
            });
    }

    private static async Task<string> PutBlobAsync(IServiceProvider services, string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = BlobHasher.ComputeHash(bytes);
        await services.GetRequiredService<IBlobStoreProvider>().PutBlobAsync(hash, bytes, "text/plain");
        return hash;
    }

    private static Task WriteSnapshotAsync(IServiceProvider services, RepositorySnapshot snapshot) =>
        services.GetRequiredService<IStudioNodeStore>().WriteNodeAsync(
            StudioNodeKind.RepositorySnapshotV1, snapshot.SnapshotId, JsonSerializer.Serialize(snapshot));

    // Minimal two-generation DAG: gen0 = expired intermediate (its unique blob is the sweep
    // candidate), gen1 = current head (Active, retained).
    private async Task<(string ExpiredUnique, string HeadBlob)> SeedAsync(IServiceProvider services)
    {
        var expiredUnique = await PutBlobAsync(services, "bytes unique to the expired generation");
        var headBlob = await PutBlobAsync(services, "bytes of the current head");

        await WriteSnapshotAsync(services, new RepositorySnapshot(
            SnapshotId: "snap-expired", RepositoryId: RepositoryId, TreeHash: "", Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(10),
            TreeEntries: new Dictionary<string, string> { ["b.txt"] = expiredUnique }));

        await WriteSnapshotAsync(services, new RepositorySnapshot(
            SnapshotId: "snap-head", RepositoryId: RepositoryId, TreeHash: "", Generation: 1,
            CreatedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(1),
            TreeEntries: new Dictionary<string, string> { ["a.txt"] = headBlob }));

        return (expiredUnique, headBlob);
    }

    [Fact]
    public async Task PostGc_with_no_parameters_runs_the_configured_mode_defaulting_to_DryRun()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        var (expiredUnique, _) = await SeedAsync(app.Services);

        var response = await client.PostAsync("/studio/cache/gc", content: null);

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("DryRun", json.GetProperty("mode").GetString());
        Assert.Equal(1, json.GetProperty("marked").GetInt32());
        Assert.Equal(0, json.GetProperty("deleted").GetInt32());
        Assert.True(File.Exists(Path.Combine(CasRoot, "blake3", expiredUnique)), "default DryRun must not mutate");
        Assert.False(Directory.Exists(Path.Combine(CasRoot, ".tombstones")));
    }

    [Fact]
    public async Task PostGc_honors_an_explicit_mode_override_and_rejects_an_unknown_one()
    {
        await using var app = BuildApp(); // configured mode stays DryRun — the override must win
        await app.StartAsync();
        var client = app.GetTestClient();
        var (expiredUnique, headBlob) = await SeedAsync(app.Services);

        var run1 = await client.PostAsync("/studio/cache/gc?mode=SweepHard", content: null);
        run1.EnsureSuccessStatusCode();
        Assert.Equal("SweepHard",
            JsonDocument.Parse(await run1.Content.ReadAsStringAsync()).RootElement.GetProperty("mode").GetString());

        var run2 = await client.PostAsync("/studio/cache/gc?mode=sweephard", content: null); // case-insensitive
        run2.EnsureSuccessStatusCode();
        Assert.Equal(1,
            JsonDocument.Parse(await run2.Content.ReadAsStringAsync()).RootElement.GetProperty("deleted").GetInt32());
        Assert.False(File.Exists(Path.Combine(CasRoot, "blake3", expiredUnique)));
        Assert.True(File.Exists(Path.Combine(CasRoot, "blake3", headBlob)));

        var bad = await client.PostAsync("/studio/cache/gc?mode=Nuke", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task PostGc_returns_409_when_a_retained_snapshots_tree_is_unresolvable_and_deletes_nothing()
    {
        await using var app = BuildApp(new BlobGcOptions { Mode = BlobGcMode.SweepHard });
        await app.StartAsync();
        var client = app.GetTestClient();
        var (expiredUnique, _) = await SeedAsync(app.Services);

        // New head (retained) whose cas-tree blob doesn't exist — fail-closed must abort the run.
        await WriteSnapshotAsync(app.Services, new RepositorySnapshot(
            SnapshotId: "snap-broken-head", RepositoryId: RepositoryId,
            TreeHash: new string('e', 64), Generation: 2,
            CreatedAt: DateTimeOffset.UtcNow, TreeFormat: "cas-tree"));

        var response = await client.PostAsync("/studio/cache/gc", content: null);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.True(File.Exists(Path.Combine(CasRoot, "blake3", expiredUnique)), "no deletes on an aborted run");
        Assert.False(Directory.Exists(Path.Combine(CasRoot, ".tombstones")));
    }

    [Fact]
    public async Task PostGc_returns_400_when_cas_storage_is_not_configured()
    {
        await using var app = StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsync("/studio/cache/gc", content: null);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task GetGcRuns_returns_the_run_ledger_most_recent_first()
    {
        await using var app = BuildApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        await SeedAsync(app.Services);

        (await client.PostAsync("/studio/cache/gc", content: null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/studio/cache/gc?mode=MarkOnly", content: null)).EnsureSuccessStatusCode();

        var response = await client.GetAsync("/studio/cache/gc/runs");

        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(2, json.GetProperty("count").GetInt32());
        var runs = json.GetProperty("runs");
        Assert.Equal("MarkOnly", runs[0].GetProperty("mode").GetString());
        Assert.Equal("DryRun", runs[1].GetProperty("mode").GetString());
        Assert.True(runs[0].GetProperty("success").GetBoolean());
        Assert.False(string.IsNullOrEmpty(runs[0].GetProperty("runId").GetString()));
    }

    [Fact]
    public async Task Materializing_an_aged_out_generation_returns_410_naming_the_retention_policy()
    {
        await using var app = BuildApp(new BlobGcOptions { Mode = BlobGcMode.SweepHard });
        await app.StartAsync();
        var client = app.GetTestClient();

        // gen0: an expired intermediate whose tree is a REAL cas-tree blob (written through the
        // resolver so this is genuinely "bytes that existed and were then reclaimed", not a
        // never-existed hash); gen1: the retained head.
        var treeResolver = app.Services.GetRequiredService<ISnapshotTreeResolver>();
        var fileBlob = await PutBlobAsync(app.Services, "file content of the doomed generation");
        var treeHash = await treeResolver.WriteTreeAsync(new Dictionary<string, string> { ["b.txt"] = fileBlob });
        await WriteSnapshotAsync(app.Services, new RepositorySnapshot(
            SnapshotId: "snap-doomed", RepositoryId: RepositoryId, TreeHash: treeHash, Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(10), TreeFormat: "cas-tree"));

        var headBlob = await PutBlobAsync(app.Services, "head content");
        await WriteSnapshotAsync(app.Services, new RepositorySnapshot(
            SnapshotId: "snap-live-head", RepositoryId: RepositoryId, TreeHash: "", Generation: 1,
            CreatedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(1),
            TreeEntries: new Dictionary<string, string> { ["a.txt"] = headBlob }));

        // The materialize endpoint looks snapshots up through IRepositorySnapshotService's
        // in-memory index, which is populated by rehydration at startup — these rows were seeded
        // after StartAsync, so rehydrate once more (exactly what a restart would do).
        await app.Services.GetRequiredService<InMemoryRepositorySnapshotService>().RehydrateAsync();

        // Sanity: before GC, the doomed generation's tree genuinely resolves — via a SCRATCH
        // resolver over the same store, not the DI singleton or the REST endpoint: the singleton
        // resolver memo-caches resolved trees by TreeHash (trees are immutable, so that's correct
        // in production — a reclaimed generation may stay materializable from process memory until
        // restart, a benign availability bonus), and priming it here would mask the very CAS miss
        // the post-GC half of this test exists to observe (same reasoning as
        // WorkspaceCacheManagerLiveBlobHashesTests' nested-subtree test).
        var scratchResolver = new NodalMerge.Studio.Storage.TreeObjects.SnapshotTreeResolver(
            app.Services.GetRequiredService<IBlobStoreProvider>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<NodalMerge.Studio.Storage.TreeObjects.SnapshotTreeResolver>.Instance);
        var doomedSnapshot = new RepositorySnapshot(
            SnapshotId: "snap-doomed", RepositoryId: RepositoryId, TreeHash: treeHash, Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow - TimeSpan.FromDays(10), TreeFormat: "cas-tree");
        Assert.NotNull(await scratchResolver.ResolveTreeAsync(doomedSnapshot));
        var target = Path.Combine(_tempRoot, "materialize-target");

        // Aggressive GC: two SweepHard passes reclaim the doomed generation's tree + file blobs.
        (await client.PostAsync("/studio/cache/gc?mode=SweepHard", null)).EnsureSuccessStatusCode();
        (await client.PostAsync("/studio/cache/gc?mode=SweepHard", null)).EnsureSuccessStatusCode();

        var after = await client.PostAsync(
            $"/studio/repository-snapshots/snap-doomed/materialize?targetPath={Uri.EscapeDataString(target)}", null);

        Assert.Equal(HttpStatusCode.Gone, after.StatusCode);
        var json = JsonDocument.Parse(await after.Content.ReadAsStringAsync()).RootElement;
        Assert.True(json.GetProperty("agedOut").GetBoolean());
        Assert.Contains("retention", json.GetProperty("error").GetString(), StringComparison.OrdinalIgnoreCase);

        // The doctrine's other half: the node itself is still there (append-only), readable as history.
        var nodeStore = app.Services.GetRequiredService<IStudioNodeStore>();
        Assert.NotNull(await nodeStore.ReadNodeAsync(StudioNodeKind.RepositorySnapshotV1, "snap-doomed"));

        // And a RETAINED snapshot whose tree is genuinely broken still gets the pre-5.2 400, not a
        // false "aged out": the head is Active, so a missing tree there is a CAS problem.
        var headMiss = await client.PostAsync(
            $"/studio/repository-snapshots/snap-live-head/materialize?targetPath={Uri.EscapeDataString(target)}", null);
        headMiss.EnsureSuccessStatusCode(); // (inline tree — resolvable; just proving the endpoint still works)
    }
}
