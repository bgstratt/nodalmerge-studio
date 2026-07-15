using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Slice 6.2 — WorkgroupRepositoryDirectory's engine-backed path (the "workgroup" room, "repositories"
// namespace), exercised the same way NodalMergeStudioNodeStoreEngineTests exercises the "studio"
// room: a real StudioWebApplication (production storage — AddNodalMergeStorage) against a temp
// SQLite db, proving EngineRoomMap's generalized bridging (promote-then-persist, replay-after-
// hydrate) works correctly for a second room, not just "studio", and that registrations survive a
// restart.
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class WorkgroupRepositoryDirectoryEngineTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-workgroup-engine-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private WebApplication BuildApp()
    {
        var sqliteDbPath = Path.Combine(_tempRoot, "nodes.db");
        var blobsRootPath = Path.Combine(_tempRoot, "blobs");
        var workspaceRootPath = Path.Combine(_tempRoot, "workspace");

        return StudioWebApplication.Build(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = sqliteDbPath,
                ["NodalMerge:Storage:FileBlobs:RootPath"] = blobsRootPath,
                ["Workspace:RootPath"] = workspaceRootPath,
            }));
    }

    [Fact]
    public async Task RegisterAsync_then_ListAsync_and_MatchAsync_round_trip_against_the_real_engine()
    {
        await using var app = BuildApp();
        var directory = app.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();

        var hints = new RepositoryIdentityHints(["4b825dc642cb6eb9a060e54bf8d69288fbee4904"], ["github.com/acme/repo"]);
        var registered = await directory.RegisterAsync("acme/repo", hints);

        var all = await directory.ListAsync();
        var found = Assert.Single(all);
        Assert.Equal(registered.RepoId, found.RepoId);
        Assert.Equal("acme/repo", found.Label);
        Assert.Equal("repo/" + registered.RepoId, found.RepoRoomId);
        Assert.Equal(hints.RootShas, found.Hints.RootShas);
        Assert.Equal(hints.Remotes, found.Hints.Remotes);

        var match = await directory.MatchAsync(hints);
        var matched = Assert.IsType<RepositoryMatchResult.Matched>(match);
        Assert.Equal(registered.RepoId, matched.Entry.RepoId);
    }

    [Fact]
    public async Task Restart_rehydrates_the_workgroup_repositories_map_from_persisted_packs()
    {
        var hints = new RepositoryIdentityHints(["root-restart"], ["github.com/acme/restart-repo"]);
        string repoId;

        await using (var app1 = BuildApp())
        {
            var directory1 = app1.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
            var registered = await directory1.RegisterAsync("restart-repo", hints);
            repoId = registered.RepoId;
        }

        await using var app2 = BuildApp();
        var directory2 = app2.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();

        var all = await directory2.ListAsync();
        Assert.Single(all, e => e.RepoId == repoId);

        var match = await directory2.MatchAsync(hints);
        var matched = Assert.IsType<RepositoryMatchResult.Matched>(match);
        Assert.Equal(repoId, matched.Entry.RepoId);
    }

    [Fact]
    public async Task Studio_room_and_workgroup_room_state_do_not_cross_contaminate()
    {
        await using var app = BuildApp();

        var studioStore = app.Services.GetRequiredService<IStudioNodeStore>();
        await studioStore.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-1", "{\"status\":\"Draft\"}");

        var directory = app.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
        await directory.RegisterAsync("repo-a", new RepositoryIdentityHints(["root-a"], []));

        var workUnits = await studioStore.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
        Assert.Single(workUnits);

        var repos = await directory.ListAsync();
        Assert.Single(repos);
    }

    // Conformance check against docs/STUDIO_ROOM_SCHEMA.md (b)'s frozen "repositories-map-entry"
    // vector (studio-room-schema-vectors.v1.json, also asserted for generic JSON-shape stability by
    // StudioRoomSchemaVectorTests) — proves WorkgroupRepositoryDirectory's actual engine-map value
    // for a registration matches the frozen shape byte-for-shape (namespace "repositories", key =
    // repoId, {v, label, repoRoomId, hints:{rootShas, remotes}}), the same way
    // NodalMergeStudioNodeStoreEngineTests does for the "studio" map vectors.
    [Fact]
    public async Task RegisterAsync_produces_the_frozen_repositories_map_envelope_shape()
    {
        await using var app = BuildApp();
        var directory = app.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();

        var vector = StudioRoomSchemaVectorTests.LoadVectors().Single(v => v.Id == "repositories-map-entry");
        var repoId = vector.Key!;

        var hints = new RepositoryIdentityHints(
            ["4b825dc642cb6eb9a060e54bf8d69288fbee4904", "da39a3ee5e6b4b0d3255bfef95601890afd80709"],
            ["github.com/acme/nodalmerge-studio"]);

        await directory.RegisterAsync("nodalmerge-studio", hints, preferredRepoId: repoId);

        var actual = ReadEngineMapValue(bridge, "workgroup", "repositories", repoId);
        Assert.NotNull(actual);
        Assert.True(
            JsonNode.DeepEquals(vector.Value, actual),
            "engine map value for the workgroup registration did not match the frozen repositories-map-entry vector");
    }

    private static JsonNode? ReadEngineMapValue(IRuntimeCommandBridge bridge, string roomId, string @namespace, string key)
    {
        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { MapGet = new { @namespace, key } }
        }));

        Assert.Equal(AsStatus.Ok, response.Status);

        using var doc = JsonDocument.Parse(response.EventsJson);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);

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
}
