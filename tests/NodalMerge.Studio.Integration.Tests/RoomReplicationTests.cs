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
using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 6.1b (plans/cas-distribution-and-storage.md Phase 6) — RoomPeerClient becomes a real
/// bidirectional replication client instead of a "hello with an empty frontier, then log-and-
/// discard" stub. These tests cover:
///
///  1. The outbound seam: NodalMergeStudioNodeStore.WriteNodeAsync notifies IStudioReplicationOutbound
///     with the just-promoted node id; the default (standalone) registration never throws.
///  2. The inbound seam in isolation: RoomPeerClient.HandleInboundPackAsync (ImportPack + persist +
///     live-map replay) applied against a pack produced by a completely separate engine-backed
///     host makes that write visible via IStudioNodeStore on the receiving side, and — the echo-
///     suppression guarantee — never enqueues anything onto the receiver's own outbound queue
///     (inbound apply structurally never re-enters the per-write path).
///  3. The acceptance-bar integration scenario: two real StudioWebApplication instances (the
///     preferred topology per the plan — the .NET host serves the same /ws/{room} protocol the
///     Rust server does, so no Rust binary is needed) exchanging packs over a real WebSocket
///     connection, converging without a restart, and re-converging after a kill/reconnect of the
///     downstream peer.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class RoomReplicationTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-room-replication-{Guid.NewGuid():N}");

    public void Dispose()
    {
        // See NodalMergeStudioNodeStoreEngineTests' Dispose for why ClearAllPools is required on
        // Windows before deleting the temp directory.
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private WebApplication BuildApp(
        string name,
        Action<IServiceCollection>? configureServices = null,
        Action<IWebHostBuilder>? configureWebHost = null,
        string? hostUri = null)
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
                ["Peer:RoomId"] = "studio",
                ["Peer:HostUri"] = hostUri ?? "",
            }),
            configureServices: configureServices);
    }

    [Fact]
    public async Task WriteNodeAsync_notifies_the_outbound_seam_with_the_promoted_node_id()
    {
        var recorded = new List<(string RoomId, string NodeIdHex)>();
        await using var app = BuildApp("outbound-fires", services =>
        {
            services.AddSingleton<IStudioReplicationOutbound>(new RecordingOutbound(recorded));
        });

        var store = app.Services.GetRequiredService<IStudioNodeStore>();
        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-outbound", JsonSerializer.Serialize(new { n = 1 }));

        // At least one notification for this write is expected (the very first write on a fresh
        // workspace also runs the one-shot legacy migration, which promotes+notifies once itself
        // even with zero legacy rows — both notifications are legitimate, not a double-fire bug).
        Assert.NotEmpty(recorded);
        Assert.All(recorded, call =>
        {
            Assert.Equal("studio", call.RoomId);
            Assert.False(string.IsNullOrWhiteSpace(call.NodeIdHex));
        });
    }

    [Fact]
    public async Task WriteNodeAsync_never_throws_against_the_default_standalone_outbound_registration()
    {
        // Build()'s default registration is RoomPeerClient with HostUri unset (standalone) — the
        // write path must complete normally regardless (skip-when-standalone).
        await using var app = BuildApp("outbound-standalone-noop");
        var store = app.Services.GetRequiredService<IStudioNodeStore>();

        await store.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-standalone", JsonSerializer.Serialize(new { n = 1 }));

        var readBack = await store.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-standalone");
        Assert.NotNull(readBack);
    }

    [Fact]
    public async Task Inbound_pack_apply_updates_the_live_map_without_enqueueing_an_outbound_echo()
    {
        await using var appSource = BuildApp("inbound-source");
        await using var appTarget = BuildApp("inbound-target");

        var storeSource = appSource.Services.GetRequiredService<IStudioNodeStore>();
        var bridgeSource = appSource.Services.GetRequiredService<IRuntimeCommandBridge>();

        var payload = JsonSerializer.Serialize(new { status = "InProgress", n = 42 });
        await storeSource.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-inbound", payload);

        var nodesB64 = ExtractFullPackNodesB64(bridgeSource, "studio");
        Assert.False(string.IsNullOrWhiteSpace(nodesB64));

        await using var peerTarget = new RoomPeerClient(
            new HeadlessPeerOptions { RoomId = "studio" },
            appTarget.Services.GetRequiredService<WorkspaceOptions>(),
            appTarget.Services,
            appTarget.Services.GetRequiredService<IHostApplicationLifetime>(),
            NullLogger<RoomPeerClient>.Instance);

        var packJson = JsonSerializer.Serialize(new { type = "pack", room = "studio", nodes = nodesB64 });
        await peerTarget.HandleInboundPackAsync(packJson, CancellationToken.None);

        var storeTarget = appTarget.Services.GetRequiredService<IStudioNodeStore>();
        var readBack = await storeTarget.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-inbound");
        Assert.NotNull(readBack);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload), JsonNode.Parse(readBack)));

        // Echo suppression: applying an inbound pack must never enqueue anything for upstream
        // re-send — HandleInboundPackAsync never touches NotifyLocalWriteAsync/_pending at all.
        Assert.Equal(0, peerTarget.PendingOutboundCountForTests);
    }

    [Fact(Timeout = 60_000)]
    public async Task Two_hosts_replicate_over_a_real_room_peer_connection_and_reconverge_after_reconnect()
    {
        await using var hostA = BuildApp("bidir-hostA", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await hostA.StartAsync();

        var boundAddress = hostA.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUriA = boundAddress.Replace("http://", "ws://", StringComparison.Ordinal);

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

            var storeA = hostA.Services.GetRequiredService<IStudioNodeStore>();
            var storeB = hostB.Services.GetRequiredService<IStudioNodeStore>();

            var payload1 = JsonSerializer.Serialize(new { status = "InProgress", n = 1 });
            await storeA.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-bidir", payload1);

            var readBack1 = await PollUntilAsync(
                () => storeB.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-bidir"),
                json => json is not null && JsonNode.DeepEquals(JsonNode.Parse(payload1), JsonNode.Parse(json)));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload1), JsonNode.Parse(readBack1)));

            // Kill/reconnect B mid-stream: stop just the RoomPeerClient hosted service (simulating
            // a dropped connection), write more on A while B is fully disconnected, then restart
            // it. Reconnect's hello -> welcome -> catch-up round-trip must converge B without
            // relying on the in-memory pending queue (a real process restart would lose that too).
            var roomPeerClientB = hostB.Services.GetRequiredService<RoomPeerClient>();
            await roomPeerClientB.StopAsync(CancellationToken.None);

            var payload2 = JsonSerializer.Serialize(new { status = "Completed", n = 2 });
            await storeA.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-bidir", payload2);

            await roomPeerClientB.StartAsync(CancellationToken.None);

            var readBack2 = await PollUntilAsync(
                () => storeB.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-bidir"),
                json => json is not null && JsonNode.DeepEquals(JsonNode.Parse(payload2), JsonNode.Parse(json)));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(payload2), JsonNode.Parse(readBack2)));
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    private static string? ExtractFullPackNodesB64(IRuntimeCommandBridge bridge, string roomId)
    {
        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { RequestServerPack = new { known_ids = Array.Empty<string>() } }
        }));
        Assert.Equal(AsStatus.Ok, response.Status);

        using var eventsDoc = JsonDocument.Parse(response.EventsJson);
        foreach (var evt in eventsDoc.RootElement.EnumerateArray())
        {
            if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("ServerPackPrepared", out var serverPack))
                continue;
            if (serverPack.TryGetProperty("nodes_b64", out var nodesB64Node) && nodesB64Node.ValueKind == JsonValueKind.String)
                return nodesB64Node.GetString();
        }

        return null;
    }

    private static async Task<string> PollUntilAsync(
        Func<Task<string?>> read,
        Func<string?, bool> predicate,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        string? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            last = await read();
            if (predicate(last))
                return last!;
            await Task.Delay(100);
        }

        throw new TimeoutException($"Condition never satisfied within the timeout; last observed value: {last ?? "(null)"}");
    }

    private sealed class RecordingOutbound(List<(string RoomId, string NodeIdHex)> recorded) : IStudioReplicationOutbound
    {
        public Task NotifyLocalWriteAsync(string roomId, string nodeIdHex, CancellationToken cancellationToken = default)
        {
            recorded.Add((roomId, nodeIdHex));
            return Task.CompletedTask;
        }
    }
}
