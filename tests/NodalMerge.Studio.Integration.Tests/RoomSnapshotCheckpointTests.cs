using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// plans/room-snapshot-checkpoint-redesign.md — regression guard for the integration-checkpoint model.
//
// The bug: every studio entity write re-serialized the ENTIRE room's DAG — all mutation HISTORY — to
// a full snapshot (host <= 0.2.4), so churn was quadratic: a 5 KB repo + one goal produced a 205 MB DB
// (191 snapshots, 0 -> 5 MB each; 8k+ op-history nodes). The fix (host 0.2.5): a write now persists
// only the promoted checkpoint node, whose size tracks the room's LIVE MAP (current entities), not the
// unbounded mutation history; full-room snapshots are minted only at integration checkpoints (merge to
// main) and the disconnect flush.
//
// The load-bearing property this guards: churning one entity (the pattern that blew up — every status
// transition was a new history node) must NOT grow the persisted pack, because the live map still has
// exactly one entry. (Persisting N *distinct* entities does grow with N — that's the working-set size,
// inherent and bounded, not the history explosion this fixed.)
//
// Runs the real StudioWebApplication (production storage — AddNodalMergeStorage) against a temp SQLite
// db and reads accepted_nodes directly, the same harness style as WorkgroupRepositoryDirectoryEngineTests.
// The delta-persist path (EngineRoomMap.PersistAndReplicateAsync ->
// RuntimeDagPersistenceService.PersistPromotedNodeDeltaAsync) is shared by every room, so exercising the
// "studio" room proves it for the repo rooms too.
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class RoomSnapshotCheckpointTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-snapshot-checkpoint-{Guid.NewGuid():N}");
    private readonly string _dbPath;

    public RoomSnapshotCheckpointTests()
    {
        _dbPath = Path.Combine(_tempRoot, "nodes.db");
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private WebApplication BuildApp() =>
        StudioWebApplication.Build(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = _dbPath,
                ["NodalMerge:Storage:FileBlobs:RootPath"] = Path.Combine(_tempRoot, "blobs"),
                ["Workspace:RootPath"] = Path.Combine(_tempRoot, "workspace"),
            }));

    [Fact]
    public async Task Churning_one_entity_does_not_grow_the_persisted_pack_with_history()
    {
        const int updates = 48;

        await using (var app = BuildApp())
        {
            var store = app.Services.GetRequiredService<IStudioNodeStore>();
            for (var i = 0; i < updates; i += 1)
            {
                // Churn a SINGLE entity — the pattern that blew up the old code: every update was a
                // new op node, and the per-write full-DAG snapshot grew with that mutation history.
                // The live map still holds exactly one entry, so the checkpoint pack must stay bounded.
                await store.WriteNodeAsync(
                    StudioNodeKind.WorkUnitV1,
                    "WU-churn",
                    JsonSerializer.Serialize(new { status = "Draft", revision = i }));
            }
        }

        // App is closed; drop pooled handles so the read connection sees a settled file.
        SqliteConnection.ClearAllPools();

        var packSizes = ReadStudioRoomPackSizesInOrder();

        // One pack persisted per update (plus possibly a few bootstrap writes) — NOT one full-DAG
        // snapshot per update coalesced away.
        Assert.True(
            packSizes.Count >= updates,
            $"expected at least {updates} persisted packs, got {packSizes.Count}");

        // The load-bearing guarantee: with one live entry, persistence must NOT grow with the number
        // of updates. The old full-DAG-snapshot-per-write grew ~linearly with mutation history — the
        // 48th snapshot would be ~48x the 1st — so the last-quarter average would dwarf the first.
        // The checkpoint-node approach keeps this ratio ~1.
        var quarter = updates / 4;
        var earlyAvg = packSizes.Take(quarter).Average();
        var lateAvg = packSizes.Skip(packSizes.Count - quarter).Take(quarter).Average();

        Assert.True(earlyAvg > 0, "no persisted pack payload found");
        Assert.True(
            lateAvg < earlyAvg * 2,
            $"persisted pack is growing with mutation history (full-DAG-snapshot regression): "
            + $"early-quarter avg {earlyAvg:F0}B, late-quarter avg {lateAvg:F0}B — churning one entity "
            + $"must not accumulate history into the per-write payload.");
    }

    // Pack payloads for the local "studio" room, in insertion order. Column names per the persisted
    // accepted_nodes schema (room_id, payload, payload_kind); payload_kind is always "pack".
    private List<long> ReadStudioRoomPackSizesInOrder()
    {
        var sizes = new List<long>();
        using var conn = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT length(payload) FROM accepted_nodes "
            + "WHERE room_id = 'studio' AND payload_kind = 'pack' "
            + "ORDER BY accepted_at_utc, rowid";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            sizes.Add(reader.GetInt64(0));
        return sizes;
    }
}
