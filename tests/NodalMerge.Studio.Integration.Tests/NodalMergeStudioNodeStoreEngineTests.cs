using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 6.1a — NodalMergeStudioNodeStore now rides the embedded engine's MapSet/MapGet/MapAll
/// instead of writing AcceptedNodeRecords directly to INodeStoreProvider. These tests build a real
/// StudioWebApplication against a temp SQLite db + temp workspace root (mirroring
/// ProductionStorageIntegrationTests' pattern — the production-storage path, i.e.
/// AddNodalMergeStorage(), which is what StudioWebApplication.Build([]) uses by default), and cover:
///
///  1. Envelope-shape conformance against docs/STUDIO_ROOM_SCHEMA.md's frozen vectors
///     (studio-room-schema-vectors.v1.json), for both the ordinary work-unit case and the
///     entity-id-with-"/" case (base/MP-1).
///  2. Round-trip write/read semantics, including latest-write-wins per entity.
///  3. One-shot legacy-row migration: seed a row via the pre-6.1a node-ID scheme directly through
///     INodeStoreProvider, confirm the new store both falls back to it and imports it into the
///     engine map, and confirm a second run doesn't re-run migration or duplicate entries.
///  4. Restart durability: write, dispose/recreate the host against the same DB paths, read back.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class NodalMergeStudioNodeStoreEngineTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-nodestore-engine-{Guid.NewGuid():N}");

    public void Dispose()
    {
        // See ProductionStorageIntegrationTests' Dispose for why ClearAllPools is required on
        // Windows before deleting the temp directory (pooled native SQLite connections keep the
        // file handle open otherwise).
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
    public async Task WriteNodeAsync_produces_the_frozen_work_unit_envelope_shape()
    {
        await using var app = BuildApp();
        var store = app.Services.GetRequiredService<IStudioNodeStore>();
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();

        var payload = JsonSerializer.Serialize(new
        {
            workUnitId = "WU-42",
            goalId = "GOAL-7",
            repositoryId = "repo-3fa85f6457174562b3fc2c963f66afa6",
            branchName = "wu-42-add-retry",
            status = "InProgress",
            fileScope = new[] { "src/lib/retry.ts", "src/lib/retry.test.ts" }
        });

        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-42", payload);

        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-42");
        Assert.Equal("studio/work-unit/v1/WU-42", key);

        var actual = ReadEngineMapValue(bridge, "studio", key);
        Assert.NotNull(actual);

        var expected = LoadVectorValue("studio-map-work-unit");
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            "engine map value for the work-unit write did not match the frozen vector envelope shape");
    }

    [Fact]
    public async Task WriteNodeAsync_produces_the_frozen_envelope_shape_when_entity_id_contains_a_slash()
    {
        await using var app = BuildApp();
        var store = app.Services.GetRequiredService<IStudioNodeStore>();
        var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();

        var payload = JsonSerializer.Serialize(new
        {
            proposalId = "MP-1",
            sourceBranch = "wu-1-fix-flake",
            targetBranch = "main",
            goal = "Fix the SqliteConnection pool race in Integration.Tests",
            status = "Draft",
            treeHash = "4b825dc642cb6eb9a060e54bf8d69288fbee4904ab00000000000000000000"
        });

        await store.WriteNodeAsync(StudioNodeKind.MergeProposalV1, "base/MP-1", payload);

        var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.MergeProposalV1, "base/MP-1");
        Assert.Equal("studio/merge-proposal/v1/base/MP-1", key);

        var actual = ReadEngineMapValue(bridge, "studio", key);
        Assert.NotNull(actual);

        var expected = LoadVectorValue("studio-map-entity-id-with-slash");
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            "engine map value for the base/MP-1 write did not match the frozen vector envelope shape");
    }

    [Fact]
    public async Task Write_then_read_round_trips_and_latest_write_wins_per_entity()
    {
        await using var app = BuildApp();
        var store = app.Services.GetRequiredService<IStudioNodeStore>();

        var first = JsonSerializer.Serialize(new { status = "Draft", n = 1 });
        var second = JsonSerializer.Serialize(new { status = "InProgress", n = 2 });

        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-rt", first);
        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-rt", second);

        var readBack = await store.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-rt");
        Assert.NotNull(readBack);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(second), JsonNode.Parse(readBack)));

        var all = await store.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
        var entry = Assert.Single(all, e => e.EntityId == "WU-rt");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(second), JsonNode.Parse(entry.PayloadJson)));
    }

    [Fact]
    public async Task Legacy_rows_are_migrated_once_and_never_duplicated_on_a_second_run()
    {
        var sqliteDbPath = Path.Combine(_tempRoot, "nodes.db");

        await using (var app = BuildApp())
        {
            // Seed a legacy-scheme row directly through INodeStoreProvider, bypassing
            // IStudioNodeStore entirely — exactly the shape the pre-6.1a NodalMergeStudioNodeStore
            // wrote (payload kind "studio", node id "studio:{kind}:{entityId}:{ticksD20}"), and
            // exactly what 6.1a's one-shot migration must import.
            var legacyNodeStore = app.Services.GetRequiredService<INodeStoreProvider>();
            var legacyPayloadJson = JsonSerializer.Serialize(new { status = "Draft", n = 1 });
            var legacyNodeId =
                $"studio:{StudioNodeKind.WorkUnitV1}:WU-legacy:{DateTimeOffset.UtcNow.Ticks:D20}";

            await legacyNodeStore.PersistAcceptedNodesAsync(
                NodalMergeStudioNodeStore.StudioRoomId,
                [
                    new AcceptedNodeRecord(
                        legacyNodeId,
                        Encoding.UTF8.GetBytes(legacyPayloadJson),
                        PayloadKind: "studio",
                        AcceptedAtUtc: DateTimeOffset.UtcNow)
                ]);

            var store = app.Services.GetRequiredService<IStudioNodeStore>();

            // First read triggers hydrate + the one-shot migration.
            var readBack = await store.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-legacy");
            Assert.NotNull(readBack);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(legacyPayloadJson), JsonNode.Parse(readBack)));

            // Confirm it actually landed in the engine map, not just the legacy fallback path.
            var bridge = app.Services.GetRequiredService<IRuntimeCommandBridge>();
            var key = NodalMergeStudioNodeStore.BuildKey(StudioNodeKind.WorkUnitV1, "WU-legacy");
            var mapValue = ReadEngineMapValue(bridge, "studio", key);
            Assert.NotNull(mapValue);
            Assert.Equal(StudioNodeKind.WorkUnitV1, mapValue!["kind"]!.GetValue<string>());

            var all = await store.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
            Assert.Single(all, e => e.EntityId == "WU-legacy");
        }

        // Second run against the same DB: migration must not re-run/duplicate — same read, same
        // single entry, and the legacy row is still untouched (AP-5: legacy rows are never
        // rewritten or deleted).
        await using (var app2 = BuildApp())
        {
            var store2 = app2.Services.GetRequiredService<IStudioNodeStore>();

            var readBack2 = await store2.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-legacy");
            Assert.NotNull(readBack2);
            Assert.True(JsonNode.DeepEquals(
                JsonNode.Parse("""{"status":"Draft","n":1}"""), JsonNode.Parse(readBack2)));

            var all2 = await store2.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
            Assert.Single(all2, e => e.EntityId == "WU-legacy");

            var legacyNodeStore2 = app2.Services.GetRequiredService<INodeStoreProvider>();
            var snapshot = await legacyNodeStore2.LoadRoomSnapshotAsync(NodalMergeStudioNodeStore.StudioRoomId);
            Assert.NotNull(snapshot);
            Assert.Contains(
                snapshot!.Nodes,
                n => n.PayloadKind == "studio" && n.NodeIdHex.StartsWith(
                    $"studio:{StudioNodeKind.WorkUnitV1}:WU-legacy:", StringComparison.Ordinal));
        }

        Assert.True(File.Exists(sqliteDbPath));
    }

    [Fact]
    public async Task Restart_rehydrates_engine_backed_studio_state_from_persisted_packs()
    {
        var payload = JsonSerializer.Serialize(new { status = "InProgress", n = 7 });

        await using (var app1 = BuildApp())
        {
            var store1 = app1.Services.GetRequiredService<IStudioNodeStore>();
            await store1.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-restart", payload);
        }

        await using (var app2 = BuildApp())
        {
            var store2 = app2.Services.GetRequiredService<IStudioNodeStore>();

            var readBack = await store2.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-restart");
            Assert.NotNull(readBack);
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(readBack)));

            var all = await store2.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1);
            Assert.Single(all, e => e.EntityId == "WU-restart");
        }
    }

    private static JsonNode? ReadEngineMapValue(IRuntimeCommandBridge bridge, string @namespace, string key)
    {
        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = NodalMergeStudioNodeStore.StudioRoomId,
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

    private static JsonNode? LoadVectorValue(string id)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "TestData", "studio-room-schema-vectors.v1.json");
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));

        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            if (string.Equals(v.GetProperty("id").GetString(), id, StringComparison.Ordinal))
                return JsonNode.Parse(v.GetProperty("value").GetRawText());
        }

        throw new InvalidOperationException($"vector '{id}' not found in studio-room-schema-vectors.v1.json");
    }
}
