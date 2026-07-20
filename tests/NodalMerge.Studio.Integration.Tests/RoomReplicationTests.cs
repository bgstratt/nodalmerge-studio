using System.Net.WebSockets;
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
using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Studio.Contracts.Domain;
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
///
/// Slice 7.3 (plans/cas-distribution-and-storage.md Phase 7) — the peer's own room
/// (HeadlessPeerOptions.RoomId, still literally "studio") stops being joined/pushed upstream at
/// all (see RoomPeerClient's own 7.3 comments): it is peer-private local state, and the pre-6.3
/// upstream join was a transition artifact that made every peer connected to the same server
/// collide on that literal room name. Test (3) above therefore migrated its replication vehicle
/// from the "studio" room to the workgroup room (IWorkgroupRepositoryDirectory — the one
/// workgroup-room consumer actually wired into RoomReplicationDispatcher's live-refresh path;
/// see that test's own comment) — same real pack-exchange/reconnect mechanics, no peer-private
/// state involved. Tests (1)/(2) above still use
/// "studio" deliberately: they exercise the LOCAL write/inbound-apply mechanics directly (the
/// outbound-notify seam, and a hand-built RoomPeerClient's HandleInboundPackAsync with no live
/// socket at all), which is exactly what "studio" stays fully functional for post-7.3 — neither
/// test ever joins a room or crosses a wire between two peers, so 7.3 doesn't change what they
/// prove. Three more tests below cover 7.3's own acceptance bar directly: no cross-peer
/// "studio"-room collision (upstream), no downstream broadcast to a raw peer on this host's own
/// /ws/studio endpoint, and a proof that RepositoryRegistryService.RefreshAsync's restored live
/// refresh (also this slice) never absorbs a connected peer's own repository row.
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
            appTarget.Services.GetRequiredService<RoomOptions>(),
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

            // Slice 7.3 — migrated off the "studio" room (no longer joined/pushed upstream at
            // all) onto the workgroup room instead, via IWorkgroupRepositoryDirectory — the one
            // workgroup-room consumer actually wired into RoomReplicationDispatcher's live-refresh
            // path (it has its own ReplayCanonicalResolutionIntoLiveMapAsync hook the dispatcher
            // calls on every inbound "workgroup" pack; IWorkgroupGoalDirectory has no such hook —
            // a separate, pre-existing gap discovered while building this migration, out of this
            // slice's scope to fix, so not used here). Same real pack-exchange/reconnect mechanics
            // "studio" used to exercise, and the exact vehicle RoomPerRepoTests' own bidir test
            // already proves reliable for two-host convergence.
            var directoryA = hostA.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
            var directoryB = hostB.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();

            await directoryA.RegisterAsync("before-kill", RepositoryIdentityHints.Empty, preferredRepoId: "repo-bidir-before");

            await PollUntilTrueAsync(async () =>
                (await directoryB.ListAsync()).Any(e => e.RepoId == "repo-bidir-before"));

            // Kill/reconnect B mid-stream: stop just the RoomPeerClient hosted service (simulating
            // a dropped connection), write more on A while B is fully disconnected, then restart
            // it. Reconnect's hello -> welcome -> catch-up round-trip must converge B without
            // relying on the in-memory pending queue (a real process restart would lose that too).
            var roomPeerClientB = hostB.Services.GetRequiredService<RoomPeerClient>();
            await roomPeerClientB.StopAsync(CancellationToken.None);

            await directoryA.RegisterAsync("after-reconnect", RepositoryIdentityHints.Empty, preferredRepoId: "repo-bidir-after");

            await roomPeerClientB.StartAsync(CancellationToken.None);

            await PollUntilTrueAsync(async () =>
                (await directoryB.ListAsync()).Any(e => e.RepoId == "repo-bidir-after"));
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    /// <summary>
    /// Slice 7.3 acceptance bar, upstream half: two peers connected to the same server (hostA
    /// doubles as the server, same simplification RoomPerRepoTests/this class's own bidir test
    /// already make) never share or LWW-collide any "studio"-room state. Checked two ways: the
    /// membership SET directly (fast, deterministic — RoomPeerClient never even opens a
    /// connection for "studio"), and behaviorally (each peer's own runtime-settings write stays
    /// exactly its own, even after several membership-reconcile intervals have elapsed).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Peer_never_joins_or_replicates_the_studio_room_upstream()
    {
        await using var hostA = BuildApp("s73-upstream-hostA", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await hostA.StartAsync();

        var boundAddress = hostA.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUriA = boundAddress.Replace("http://", "ws://", StringComparison.Ordinal);

        var rootB = Path.Combine(_tempRoot, "s73-upstream-hostB");
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
            var roomPeerClientB = hostB.Services.GetRequiredService<RoomPeerClient>();

            // Membership set: "workgroup" is always joined; "studio" never is. Fast/deterministic
            // — no replication timing involved, this is the actual _connections dictionary.
            await PollUntilTrueAsync(() => Task.FromResult(roomPeerClientB.HasJoinedRoomForTests("workgroup")));
            Assert.False(roomPeerClientB.HasJoinedRoomForTests("studio"));

            // Behavioral: RuntimeSettingsV1 is a workspace-global kind that always lives in
            // "studio" — each peer's own write must stay exactly its own even after several
            // membership-reconcile intervals (5s each) have elapsed, proving this isn't merely
            // "hasn't propagated yet" but genuinely never will.
            var storeA = hostA.Services.GetRequiredService<IStudioNodeStore>();
            var storeB = hostB.Services.GetRequiredService<IStudioNodeStore>();

            await storeA.WriteNodeAsync(StudioNodeKind.RuntimeSettingsV1, "settings", "{\"owner\":\"A\"}");
            await storeB.WriteNodeAsync(StudioNodeKind.RuntimeSettingsV1, "settings", "{\"owner\":\"B\"}");

            await Task.Delay(TimeSpan.FromSeconds(7));

            var onA = await storeA.ReadNodeAsync(StudioNodeKind.RuntimeSettingsV1, "settings");
            var onB = await storeB.ReadNodeAsync(StudioNodeKind.RuntimeSettingsV1, "settings");
            Assert.Contains("\"A\"", onA);
            Assert.Contains("\"B\"", onB); // never overwritten/collided by A's value, or vice versa
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    /// <summary>
    /// Slice 7.3 acceptance bar, downstream half — the reasoning half this slice had to think
    /// through explicitly: in the embedded-server topology (StudioWebApplication.Build()), this
    /// host's own /ws/{roomId} endpoint is served by the SAME process that owns "studio"'s local
    /// state. A downstream peer connecting directly to THIS host's own /ws/studio endpoint (not
    /// via RoomPeerClient at all — any raw WebSocket client speaking the same wire protocol) would
    /// be the identical collision the upstream-membership fix addresses, just mirrored: it would
    /// receive this host's own private settings/profile/etc. writes as ordinary room broadcasts.
    /// Proven directly against the real WS endpoint with a raw client, not merely by inspecting
    /// RoomPeerClient's own membership set (which a downstream connection never goes through).
    /// </summary>
    [Fact(Timeout = 30_000)]
    public async Task Local_studio_room_write_is_never_broadcast_downstream_to_a_raw_peer_on_this_hosts_own_ws_endpoint()
    {
        await using var hostA = BuildApp("s73-downstream-hostA", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await hostA.StartAsync();

        var boundAddress = hostA.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUri = new Uri(boundAddress.Replace("http://", "ws://", StringComparison.Ordinal) + "/ws/studio");

        var store = hostA.Services.GetRequiredService<IStudioNodeStore>();

        // Force the store's lazy EnsureInitializedAsync (and the one-shot legacy migration it
        // runs, which itself notifies NotifyLocalWriteAsync once even against a brand-new
        // workspace — see this class's own outbound test) to settle BEFORE the raw socket below
        // connects, so that unrelated notify can't race the assertion below.
        await store.WriteNodeAsync(StudioNodeKind.RuntimeSettingsV1, "warmup", "{}");

        using var raw = new ClientWebSocket();
        await raw.ConnectAsync(wsUri, CancellationToken.None);
        var hello = JsonSerializer.Serialize(new
        {
            type = "hello",
            room = "studio",
            pubkey = "raw-downstream",
            peer_id = "raw-downstream",
            peer_type = "raw",
            frontier = Array.Empty<string>()
        });
        await raw.SendAsync(Encoding.UTF8.GetBytes(hello), WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);

        // Drain the hello response's catch-up burst (RuntimeWebSocketLoopRunner sends "welcome"
        // then a catch-up "pack" for this one hello frame — but rather than hardcode an exact
        // count, which is fragile against the server's own message shape, this keeps racing a
        // single outstanding (never-cancelled) receive against a short timer and re-arming it as
        // long as messages keep arriving quickly. Never cancels an in-flight ClientWebSocket.
        // ReceiveAsync — that aborts the WHOLE socket (real .NET behavior) — so a "timed out"
        // receive is simply left pending and reused directly as the live-check receive below,
        // rather than started fresh (ClientWebSocket allows only one outstanding receive at a time).
        var pending = ReceiveOneAsync(raw);
        while (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromMilliseconds(750))) == pending)
        {
            await pending; // consume the drained message
            pending = ReceiveOneAsync(raw); // re-arm
        }

        // The write this test actually cares about: made AFTER the raw peer is fully caught up
        // (no message arrived within the drain window above). If NotifyLocalWriteAsync's
        // downstream broadcast still fired for "studio", this would arrive as a live "pack"
        // message on `pending` (already armed) well within the window below.
        await store.WriteNodeAsync(StudioNodeKind.RuntimeSettingsV1, "live-write", "{\"theme\":\"dark\"}");

        var readBack = await store.ReadNodeAsync(StudioNodeKind.RuntimeSettingsV1, "live-write");
        Assert.NotNull(readBack); // the write itself still succeeds locally — only broadcast is suppressed

        var liveCompleted = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(3)));
        var liveMessage = liveCompleted == pending ? await pending : null;
        Assert.Null(liveMessage);

        // Passing this assertion means `pending` is still outstanding, and it cannot be cancelled
        // (see ReceiveOneAsync — cancelling an in-flight ClientWebSocket receive aborts the whole
        // socket). It will therefore fault when the host tears the connection down at the end of the
        // test. Observe that fault deliberately: left alone it becomes an unobserved task exception,
        // which the detector reports as a leak — correctly, but this one is intentional, and noise
        // in that report is exactly what would train us to stop reading it.
        ObserveExpectedFault(pending);
    }

    // Marks a deliberately-abandoned task's exception as observed. Only for tasks a test knowingly
    // walks away from — never as a way to quiet a leak that has not been understood.
    private static void ObserveExpectedFault(Task task) =>
        _ = task.ContinueWith(
            t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    /// <summary>
    /// Slice 7.3's other product decision: with the "studio"-room collision source gone,
    /// RepositoryRegistryService.RefreshAsync is restored to the default live refresh (see that
    /// class's own updated comment for why 6.5 Part 1's original no-op is now unnecessary). This
    /// proves the restore is actually safe: two peers, each with their own unrelated repository
    /// registration, connected via a SHARED repo room (so a live cache refresh genuinely fires) —
    /// neither peer's registry cache ever absorbs the other's row, because "studio" (where
    /// RepositoryV1 rows live) never replicates between them at all anymore.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task RepositoryRegistryService_RefreshAsync_restored_after_73_never_absorbs_a_connected_peers_own_repository_row()
    {
        await using var hostA = BuildApp("s73-registry-hostA", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await hostA.StartAsync();

        var boundAddress = hostA.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var wsUriA = boundAddress.Replace("http://", "ws://", StringComparison.Ordinal);

        var registryA = hostA.Services.GetRequiredService<IRepositoryRegistryService>();
        var repoR1 = await registryA.RegisterAsync(Path.Combine(_tempRoot, "s73-registry-r1"), "r1");
        if (repoR1.WorkgroupRepoId is null)
            repoR1 = await registryA.ResolveDisambiguationAsync(repoR1.RepositoryId, chosenRepoId: null)
                ?? throw new InvalidOperationException("disambiguation resolution returned null");

        var rootB = Path.Combine(_tempRoot, "s73-registry-hostB");
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
            var registryB = hostB.Services.GetRequiredService<IRepositoryRegistryService>();

            // B registers its OWN, unrelated repository — a distinct local candidate, auto-minted
            // to a distinct workgroup id (no shared identity hints with R1).
            var repoOwnB = await registryB.RegisterAsync(Path.Combine(rootB, "s73-registry-own"), "own-on-b");
            if (repoOwnB.WorkgroupRepoId is null)
                repoOwnB = await registryB.ResolveDisambiguationAsync(repoOwnB.RepositoryId, chosenRepoId: null)
                    ?? throw new InvalidOperationException("disambiguation resolution returned null");

            // B also binds a SEPARATE local candidate to the SAME repo A registered (R1) — the
            // ordinary D2 disambiguation flow — so B ends up joining repo/R1's room too, giving
            // this test a real repo-room pack to trigger a live cache refresh with.
            var directoryB = hostB.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();
            await PollUntilTrueAsync(async () => (await directoryB.ListAsync()).Any(e => e.RepoId == repoR1.WorkgroupRepoId));

            var localR1OnB = await registryB.RegisterAsync(Path.Combine(rootB, "s73-registry-clone-of-r1"), "r1-on-b");
            Assert.Null(localR1OnB.WorkgroupRepoId);
            Assert.NotNull(localR1OnB.PendingDisambiguation);
            localR1OnB = await registryB.ResolveDisambiguationAsync(localR1OnB.RepositoryId, repoR1.WorkgroupRepoId);
            Assert.Equal(repoR1.WorkgroupRepoId, localR1OnB!.WorkgroupRepoId);

            // A repo-room write on A — B's membership loop already joined repo/R1 above, and the
            // resulting inbound pack fires RefreshAfterInboundPackAsync("repo/R1"), which (per
            // RehydratableRefreshCoordinator) runs every undeclared IRehydratable, including
            // RepositoryRegistryService — the live refresh this test proves is safe.
            var storeA = hostA.Services.GetRequiredService<IStudioNodeStore>();
            await storeA.WriteNodeAsync(StudioNodeKind.WorkUnitV1, "WU-s73-registry",
                JsonSerializer.Serialize(new { WorkUnitId = "WU-s73-registry", RepositoryId = repoR1.RepositoryId }),
                repoR1.RepositoryId);

            var storeB = hostB.Services.GetRequiredService<IStudioNodeStore>();
            await PollUntilTrueAsync(async () =>
                await storeB.ReadNodeAsync(StudioNodeKind.WorkUnitV1, "WU-s73-registry") is not null);

            // B's registry contains its own two local candidates, and NEVER A's own local
            // RepositoryId for R1 — proving the restored live refresh only ever re-absorbs THIS
            // peer's own "studio" rows, never a connected peer's.
            var onB = await registryB.ListAsync();
            Assert.Contains(onB, r => r.RepositoryId == repoOwnB.RepositoryId);
            Assert.Contains(onB, r => r.RepositoryId == localR1OnB.RepositoryId);
            Assert.DoesNotContain(onB, r => r.RepositoryId == repoR1.RepositoryId);
        }
        finally
        {
            await hostB.StopAsync();
            hostB.Dispose();
        }
    }

    /// <summary>
    /// #1 goal replication (plans/repo-identity-convergence.md) — the end-to-end proof of the fix
    /// for "two peers on one repo each see only their own goal": a GoalV1 created on peer A, bound
    /// to a repo B has also joined, must surface through B's OWN IGoalNodeService (not just the raw
    /// node store) — i.e. it replicated into the shared repo room AND the inbound-pack refresh
    /// coordinator re-read GoalNodeService's cache. Before #1, GoalV1 lived in the peer-private
    /// "studio" room and never crossed, so this assertion could never hold.
    /// </summary>
    [Fact(Timeout = 60_000)]
    public async Task A_goal_created_on_one_peer_appears_in_another_peers_goal_service_on_the_same_repo()
    {
        await using var hostA = BuildApp("goalrepl-hostA", configureWebHost: wh => wh.UseUrls("http://127.0.0.1:0"));
        await hostA.StartAsync();

        var wsUriA = hostA.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.First()
            .Replace("http://", "ws://", StringComparison.Ordinal);

        var registryA = hostA.Services.GetRequiredService<IRepositoryRegistryService>();
        var repoR1 = await registryA.RegisterAsync(Path.Combine(_tempRoot, "goalrepl-r1"), "r1");
        if (repoR1.WorkgroupRepoId is null)
            repoR1 = await registryA.ResolveDisambiguationAsync(repoR1.RepositoryId, chosenRepoId: null)
                ?? throw new InvalidOperationException("disambiguation resolution returned null");

        var rootB = Path.Combine(_tempRoot, "goalrepl-hostB");
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
            var registryB = hostB.Services.GetRequiredService<IRepositoryRegistryService>();
            var directoryB = hostB.Services.GetRequiredService<IWorkgroupRepositoryDirectory>();

            // B binds its own clone of R1 (D2 disambiguation) → joins repo/{R1} so it receives the
            // goal pack. Wait for A's repo entry to replicate first.
            await PollUntilTrueAsync(async () => (await directoryB.ListAsync()).Any(e => e.RepoId == repoR1.WorkgroupRepoId));
            var localR1OnB = await registryB.RegisterAsync(Path.Combine(rootB, "goalrepl-clone-of-r1"), "r1-on-b");
            if (localR1OnB.WorkgroupRepoId is null)
                localR1OnB = await registryB.ResolveDisambiguationAsync(localR1OnB.RepositoryId, repoR1.WorkgroupRepoId)
                    ?? throw new InvalidOperationException("disambiguation resolution returned null");
            Assert.Equal(repoR1.WorkgroupRepoId, localR1OnB!.WorkgroupRepoId);

            // A creates a goal bound to R1.
            var goalNodesA = hostA.Services.GetRequiredService<IGoalNodeService>();
            await goalNodesA.RecordAsync(new GoalNode(
                GoalId: "G-repl", Goal: "peer A goal", WorkUnitId: "G-repl", BranchId: "b",
                Status: GoalStatus.Exploring, CreatedAt: DateTimeOffset.UtcNow, UpdatedAt: DateTimeOffset.UtcNow,
                Owner: "peer-a", RepositoryId: repoR1.RepositoryId));

            // B's OWN goal service surfaces A's goal — the exact cross-peer visibility that was broken.
            var goalNodesB = hostB.Services.GetRequiredService<IGoalNodeService>();
            await PollUntilTrueAsync(async () => (await goalNodesB.ListAsync()).Any(g => g.GoalId == "G-repl"));
            var onB = await goalNodesB.ListAsync();
            Assert.Contains(onB, g => g.GoalId == "G-repl" && g.Goal == "peer A goal");
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

    private static async Task PollUntilTrueAsync(Func<Task<bool>> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow + (timeout ?? TimeSpan.FromSeconds(20));
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException("Condition never satisfied within the timeout.");
    }

    // Slice 7.3's downstream-broadcast test only — reads exactly one full WebSocket text message,
    // unbounded (no cancellation token). Deliberately never cancellation-bounded: cancelling an
    // in-flight ClientWebSocket.ReceiveAsync aborts the whole socket (real .NET behavior), which
    // would make the socket unusable for whatever the caller does next.
    private static async Task<string> ReceiveOneAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var messageBuffer = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Socket closed while a message was expected.");
            messageBuffer.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        return Encoding.UTF8.GetString(messageBuffer.ToArray());
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
