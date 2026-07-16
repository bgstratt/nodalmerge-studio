using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 6.3 (plans/cas-distribution-and-storage.md Phase 6, D1/D3) — room-per-repository:
///
///  1. Kind→room routing: a repo-scoped StudioNodeKind write (WorkUnitV1 with a bound
///     RepositoryId) lands in repo/{repoId}'s engine room, never "studio"; a workspace-global
///     kind stays in "studio"; a repo-scoped write whose RepositoryId has no workgroup binding
///     falls back to "studio". ReadAllNodesAsync aggregates across two bound repo rooms.
///  2. Migration: pre-6.3 repo-scoped state written into the "studio" engine map is copied into
///     the right repo room on that room's first touch — one-shot (marker-guarded, restart-proof),
///     old "studio" rows left intact (AP-5).
///  3. Two hosts, two rooms (extends RoomReplicationTests' 6.1b scenario): peer B, bound to R1
///     only, receives R1's room traffic but never R2's; a goal spanning R1+R2 is two work units
///     in two repo rooms plus one workgroup goal node; and a repositories-map registration made
///     on host A appears in peer B's directory listing while both are connected (closing 6.2's
///     silently-dropped-workgroup-writes gap end-to-end).
///  4. Pinned cross-repo reference (D3, STUDIO_ROOM_SCHEMA.md (c)): resolves via repo room →
///     generation node → TreeHash → CAS walk → blob on a peer whose registered repo path does
///     not even exist on disk (no clone, no materialization), both via IPinnedReferenceResolver
///     directly and over POST /studio/references/resolve.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class RoomPerRepoTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-room-per-repo-{Guid.NewGuid():N}");

    public void Dispose()
    {
        // See NodalMergeStudioNodeStoreEngineTests' Dispose for why ClearAllPools is required on
        // Windows before deleting the temp directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private WebApplication BuildApp(string name, Action<IWebHostBuilder>? configureWebHost = null)
    {
        var root = Path.Combine(_tempRoot, name);

        return StudioWebApplication.Build(
            [],
            configureWebHost: configureWebHost,
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(root, "nodes.db"),
                ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(root, "blobs"),
                ["Workspace:RootPath"] = Path.Combine(root, "workspace"),
            }));
    }

    // Registers a local path and guarantees a workgroup binding: the first registration in a fresh
    // workspace auto-mints (degraded hints, zero candidates — the preferred-id continuity path);
    // later ones come back PendingDisambiguation (degraded hints, >=1 candidate) and are resolved
    // "register-new", the same one-time-user-prompt flow D2 specifies.
    private static async Task<RepositoryV1> RegisterBoundRepoAsync(IServiceProvider services, string path, string label)
    {
        var registry = services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await registry.RegisterAsync(path, label);
        if (repo.WorkgroupRepoId is null)
        {
            repo = await registry.ResolveDisambiguationAsync(repo.RepositoryId, chosenRepoId: null)
                ?? throw new InvalidOperationException("disambiguation resolution returned null");
        }

        Assert.NotNull(repo.WorkgroupRepoId);
        return repo;
    }

    [Fact]
    public async Task Repo_scoped_write_lands_in_the_repo_room_and_global_write_stays_in_studio()
    {
        await using var app = BuildApp("routing");
        var store = app.Services.GetRequiredService<IStudioNodeStore>();
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();

        var repo = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "routing-repoA"), "repoA");
        var repoRoomId = $"repo/{repo.WorkgroupRepoId}";

        // Repo-scoped write via the 6.3 routing overload.
        var wuPayload = JsonSerializer.Serialize(new { WorkUnitId = "WU-route", RepositoryId = repo.RepositoryId, Status = "Draft" });
        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-route", wuPayload, repo.RepositoryId);

        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-route");
        Assert.NotNull(TryReadEngineMapValue(bridge, repoRoomId, "studio", key));
        Assert.Null(TryReadEngineMapValue(bridge, "studio", "studio", key));

        // Room-agnostic read still finds it (bound-room fan-out).
        var readBack = await store.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-route");
        Assert.NotNull(readBack);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(wuPayload), JsonNode.Parse(readBack)));

        // Workspace-global kind stays in "studio".
        await store.WriteNodeAsync(StudioNodeKind.RuntimeSettingsV1, "settings", "{\"theme\":\"dark\"}");
        var settingsKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.RuntimeSettingsV1, "settings");
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", settingsKey));
        Assert.Null(TryReadEngineMapValue(bridge, repoRoomId, "studio", settingsKey));

        // A repo-scoped write whose RepositoryId never resolves to a binding falls back to "studio"
        // — a write is never blocked or lost on an absent workgroup binding.
        await store.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1, "WU-unbound",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-unbound", RepositoryId = "repo-never-registered" }),
            "repo-never-registered");
        var unboundKey = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-unbound");
        Assert.NotNull(TryReadEngineMapValue(bridge, "studio", "studio", unboundKey));
    }

    [Fact]
    public async Task ReadAllNodesAsync_aggregates_across_two_bound_repo_rooms_and_studio()
    {
        await using var app = BuildApp("aggregate");
        var store = app.Services.GetRequiredService<IStudioNodeStore>();

        var repoA = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "agg-repoA"), "repoA");
        var repoB = await RegisterBoundRepoAsync(app.Services, Path.Combine(_tempRoot, "agg-repoB"), "repoB");
        Assert.NotEqual(repoA.WorkgroupRepoId, repoB.WorkgroupRepoId);

        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-a",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-a", RepositoryId = repoA.RepositoryId }), repoA.RepositoryId);
        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-b",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-b", RepositoryId = repoB.RepositoryId }), repoB.RepositoryId);
        // No repositoryId — stays in "studio" (e.g. a workspace-only work unit with no repo).
        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-local",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-local" }));

        var all = await store.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
        var ids = all.Select(e => e.EntityId).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("WU-a", ids);
        Assert.Contains("WU-b", ids);
        Assert.Contains("WU-local", ids);
        Assert.Equal(3, all.Count);
    }

    [Fact]
    public async Task Pre63_studio_room_state_migrates_into_the_repo_room_once_with_old_rows_intact()
    {
        var repoPath = Path.Combine(_tempRoot, "mig-repoA");
        string workgroupRepoId;
        string localRepoId;

        await using (var app1 = BuildApp("migration"))
        {
            var store1 = app1.Services.GetRequiredService<IStudioNodeStore>();
            var bridge1 = app1.Services.GetRequiredService<IRuntimeCommandBridge>();

            var repo = await RegisterBoundRepoAsync(app1.Services, repoPath, "repoA");
            workgroupRepoId = repo.WorkgroupRepoId!;
            localRepoId = repo.RepositoryId;

            // Simulate a pre-6.3 writer: the 3-arg overload always writes to the "studio" room —
            // exactly where every repo-scoped entry lived before this slice.
            var payload = JsonSerializer.Serialize(new { WorkUnitId = "WU-mig", RepositoryId = localRepoId, Status = "Draft" });
            await store1.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig", payload);

            var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-mig");
            var repoRoomId = $"repo/{workgroupRepoId}";
            Assert.NotNull(TryReadEngineMapValue(bridge1, "studio", "studio", key));

            // First touch of the repo room (a room-agnostic read is enough) triggers the one-shot
            // per-room migration.
            var readBack = await store1.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig");
            Assert.NotNull(readBack);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(readBack)));

            Assert.NotNull(TryReadEngineMapValue(bridge1, repoRoomId, "studio", key)); // migrated in
            Assert.NotNull(TryReadEngineMapValue(bridge1, "studio", "studio", key));   // old row intact (AP-5)

            // Post-migration writes route to the repo room and win over the frozen studio copy.
            var updated = JsonSerializer.Serialize(new { WorkUnitId = "WU-mig", RepositoryId = localRepoId, Status = "Completed" });
            await store1.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig", updated, localRepoId);
            var readUpdated = await store1.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig");
            Assert.NotNull(readUpdated);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(updated), JsonNode.Parse(readUpdated)));
        }

        // Restart: migration must not re-run (marker is durable) and must not clobber the
        // post-migration update with the stale studio copy; exactly one WU-mig entity survives.
        await using (var app2 = BuildApp("migration"))
        {
            var store2 = app2.Services.GetRequiredService<IStudioNodeStore>();

            var readBack = await store2.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-mig");
            Assert.NotNull(readBack);
            using var doc = JsonDocument.Parse(readBack);
            Assert.Equal("Completed", doc.RootElement.GetProperty("Status").GetString());

            var all = await store2.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
            Assert.Single(all, e => e.EntityId == "WU-mig");
        }
    }

    [Fact(Timeout = 120_000)]
    public async Task Two_hosts_two_rooms_goal_spanning_two_repos_and_workgroup_replication()
    {
        await using var hostA = BuildApp("bidir-hostA", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await hostA.StartAsync();

        var boundAddress = hostA.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUriA = boundAddress.Replace("http://", "ws://", StringComparison.Ordinal);

        // Two bound repos on A, then the D3 shape: one goal, two work units in two repo rooms, one
        // workgroup goal node.
        var repoR1 = await RegisterBoundRepoAsync(hostA.Services, Path.Combine(_tempRoot, "bidir-r1"), "r1");
        var repoR2 = await RegisterBoundRepoAsync(hostA.Services, Path.Combine(_tempRoot, "bidir-r2"), "r2");

        var storeA = hostA.Services.GetRequiredService<IStudioNodeStore>();
        await storeA.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-span-r1",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-span-r1", RepositoryId = repoR1.RepositoryId, Goal = "cross-repo goal, R1 half" }),
            repoR1.RepositoryId);
        await storeA.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-span-r2",
            JsonSerializer.Serialize(new { WorkUnitId = "WU-span-r2", RepositoryId = repoR2.RepositoryId, Goal = "cross-repo goal, R2 half" }),
            repoR2.RepositoryId);
        await hostA.Services.GetRequiredService<IWorkgroupGoalDirectory>().RecordAsync("GOAL-span",
        [
            new WorkgroupGoalRepoWorkUnit(repoR1.WorkgroupRepoId!, "WU-span-r1"),
            new WorkgroupGoalRepoWorkUnit(repoR2.WorkgroupRepoId!, "WU-span-r2"),
        ]);

        // On A: the two work units live in two different repo rooms, neither in "studio".
        var bridgeA = hostA.Services.GetRequiredService<IRuntimeCommandBridge>();
        var keyR1 = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-span-r1");
        var keyR2 = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-span-r2");
        Assert.NotNull(TryReadEngineMapValue(bridgeA, $"repo/{repoR1.WorkgroupRepoId}", "studio", keyR1));
        Assert.NotNull(TryReadEngineMapValue(bridgeA, $"repo/{repoR2.WorkgroupRepoId}", "studio", keyR2));
        Assert.Null(TryReadEngineMapValue(bridgeA, "studio", "studio", keyR1));
        Assert.Null(TryReadEngineMapValue(bridgeA, "studio", "studio", keyR2));
        Assert.NotNull(TryReadEngineMapValue(bridgeA, "workgroup", "goals", "GOAL-span"));

        // Peer B connects to A.
        var rootB = Path.Combine(_tempRoot, "bidir-hostB");
        var hostB = StudioWebApplication.BuildPeer(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = Path.Combine(rootB, "nodes.db"),
                ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(rootB, "blobs"),
                ["Workspace:RootPath"] = Path.Combine(rootB, "workspace"),
                ["Peer:RoomId"] = "studio",
                ["Peer:HostUri"] = wsUriA,
                ["Peer:PeerId"] = "peer-b",
            }));

        try
        {
            await hostB.StartAsync();

            // (Acceptance: workgroup replication end-to-end) — registrations made on A appear in
            // B's directory listing while both are connected. R1/R2 were registered before B
            // connected (covered by catch-up); a third registration made *while* connected covers
            // the live-broadcast half below.
            var directoryB = hostB.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
            await PollUntilAsync(async () =>
            {
                var entries = await directoryB.ListAsync();
                return entries.Any(e => e.RepoId == repoR1.WorkgroupRepoId)
                    && entries.Any(e => e.RepoId == repoR2.WorkgroupRepoId);
            });

            var repoR3 = await RegisterBoundRepoAsync(hostA.Services, Path.Combine(_tempRoot, "bidir-r3"), "r3");
            await PollUntilAsync(async () =>
                (await directoryB.ListAsync()).Any(e => e.RepoId == repoR3.WorkgroupRepoId));

            // B binds to R1 ONLY (D2's one-time disambiguation flow: degraded local hints against
            // the replicated candidates, human picks R1).
            var registryB = hostB.Services.GetRequiredService<IRepositoryRegistryService>();
            var localB = await registryB.RegisterAsync(Path.Combine(rootB, "clone-of-r1"), "r1-on-b");
            Assert.Null(localB.WorkgroupRepoId);
            Assert.NotNull(localB.PendingDisambiguation);
            localB = await registryB.ResolveDisambiguationAsync(localB.RepositoryId, repoR1.WorkgroupRepoId);
            Assert.Equal(repoR1.WorkgroupRepoId, localB!.WorkgroupRepoId);

            // B's membership loop joins repo/R1 within its reconcile interval; hello/catch-up then
            // delivers WU-span-r1. B is NOT bound to R2, never joins its room, and must never see
            // WU-span-r2.
            var storeB = hostB.Services.GetRequiredService<IStudioNodeStore>();
            await PollUntilAsync(async () =>
                await storeB.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-span-r1") is not null);

            Assert.Null(await storeB.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-span-r2"));
            var bridgeB = hostB.Services.GetRequiredService<IRuntimeCommandBridge>();
            Assert.Null(TryReadEngineMapValue(bridgeB, $"repo/{repoR2.WorkgroupRepoId}", "studio", keyR2));

            // The workgroup goal node replicated too — B sees the cross-repo coordination record.
            var goalOnB = await hostB.Services.GetRequiredService<IWorkgroupGoalDirectory>().GetAsync("GOAL-span");
            Assert.NotNull(goalOnB);
            Assert.Equal(2, goalOnB!.RepoWorkUnits.Count);
            Assert.Contains(goalOnB.RepoWorkUnits, r => r.RepoId == repoR1.WorkgroupRepoId && r.WorkUnitId == "WU-span-r1");
            Assert.Contains(goalOnB.RepoWorkUnits, r => r.RepoId == repoR2.WorkgroupRepoId && r.WorkUnitId == "WU-span-r2");
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task Pinned_reference_resolves_with_no_local_clone_of_the_referenced_repo()
    {
        await using var app = BuildApp("pinned", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await app.StartAsync();

        // The registered path deliberately does not exist — no clone, no materialization. The repo
        // room + CAS are the only local state the resolution is allowed to touch.
        var phantomPath = Path.Combine(_tempRoot, "pinned-phantom-repo");
        Assert.False(Directory.Exists(phantomPath));
        var repo = await RegisterBoundRepoAsync(app.Services, phantomPath, "phantom");
        Assert.False(Directory.Exists(phantomPath));

        // Seed CAS: file blob + tree object.
        var content = "export const answer = 42;\n";
        var contentBytes = Encoding.UTF8.GetBytes(content);
        var fileHash = BlobHasher.ComputeHash(contentBytes);
        var blobStore = app.Services.GetRequiredService<IBlobStoreProvider>();
        await blobStore.PutBlobAsync(fileHash, contentBytes, "text/plain");

        var treeResolver = app.Services.GetRequiredService<ISnapshotTreeResolver>();
        var treeHash = await treeResolver.WriteTreeAsync(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["src/lib/util.ts"] = fileHash,
        });

        // Seed the generation node into the repo room (what replication would have delivered).
        var snapshot = new RepositorySnapshot(
            SnapshotId: "gen-pin-1",
            RepositoryId: repo.RepositoryId,
            TreeHash: treeHash,
            Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow,
            TreeEntries: null,
            TreeFormat: "cas-tree");
        var store = app.Services.GetRequiredService<IStudioNodeStore>();
        await store.WriteNodeAsync(
            StudioNodeKind.RepositorySnapshotV1, snapshot.SnapshotId,
            JsonSerializer.Serialize(snapshot), repo.RepositoryId);

        // Resolve through the service — full frozen chain: repo room -> generation -> TreeHash ->
        // CAS walk -> blob.
        var resolver = app.Services.GetRequiredService<IPinnedReferenceResolver>();
        var bytes = await resolver.ResolveAsync(new PinnedReference(repo.WorkgroupRepoId!, "gen-pin-1", "src/lib/util.ts"));
        Assert.NotNull(bytes);
        Assert.Equal(content, Encoding.UTF8.GetString(bytes!));

        // Unknown generation / path miss both come back null, never throw.
        Assert.Null(await resolver.ResolveAsync(new PinnedReference(repo.WorkgroupRepoId!, "gen-nope", "src/lib/util.ts")));
        Assert.Null(await resolver.ResolveAsync(new PinnedReference(repo.WorkgroupRepoId!, "gen-pin-1", "src/lib/missing.ts")));

        // And over the REST surface.
        var address = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        using var http = new HttpClient { BaseAddress = new Uri(address) };

        var ok = await http.PostAsJsonAsync("/studio/references/resolve",
            new { repoId = repo.WorkgroupRepoId, generationId = "gen-pin-1", path = "src/lib/util.ts" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        using var okDoc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync());
        Assert.Equal(content, Encoding.UTF8.GetString(
            Convert.FromBase64String(okDoc.RootElement.GetProperty("contentBase64").GetString()!)));
        Assert.Equal(contentBytes.Length, okDoc.RootElement.GetProperty("sizeBytes").GetInt32());

        var notFound = await http.PostAsJsonAsync("/studio/references/resolve",
            new { repoId = repo.WorkgroupRepoId, generationId = "gen-nope", path = "src/lib/util.ts" });
        Assert.Equal(HttpStatusCode.NotFound, notFound.StatusCode);

        var badRequest = await http.PostAsJsonAsync("/studio/references/resolve",
            new { repoId = "", generationId = "", path = "" });
        Assert.Equal(HttpStatusCode.BadRequest, badRequest.StatusCode);
    }

    private static JsonNode? TryReadEngineMapValue(IRuntimeCommandBridge bridge, string roomId, string @namespace, string key)
    {
        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { MapGet = new { @namespace, key } }
        }));

        // Non-Ok includes "room doesn't exist on this peer" — exactly the "never joined that room"
        // case some assertions here rely on, so it maps to null rather than an assert failure.
        if (response.Status != AsStatus.Ok)
            return null;

        using var doc = JsonDocument.Parse(response.EventsJson);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var evt in doc.RootElement.EnumerateArray())
        {
            if (!evt.TryGetProperty("MapValueRead", out var read))
                continue;
            if (!read.TryGetProperty("value", out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            return JsonNode.Parse(value.GetRawText());
        }

        return null;
    }

    private static async Task PollUntilAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(45));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(200);
        }

        throw new TimeoutException("Condition never satisfied within the timeout.");
    }
}
