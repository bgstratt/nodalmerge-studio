using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;
using IHostApplicationLifetime = Microsoft.Extensions.Hosting.IHostApplicationLifetime;

namespace NodalMerge.Studio.Host;

/// <summary>
/// Maintains outbound WebSocket connections to a nodalmerge host, presenting this process as a
/// named peer — one connection per joined room (see the class comment below for why one socket
/// can't multiplex rooms). When HostUri is null the client is a no-op for the upstream half — the
/// process runs standalone with no upstream room presence (it may still serve downstream peers on
/// its own /ws/{room} endpoint, see NotifyLocalWriteAsync).
///
/// Slice 6.1b (plans/cas-distribution-and-storage.md Phase 6) rewrote this from a one-way
/// "hello with an empty frontier, then log-and-discard inbound packs" stub into a real
/// bidirectional replication client (single room, "studio").
///
/// Slice 6.3 (room-per-repo, D1) makes it multi-room. **Wire verdict**: one WebSocket connection
/// cannot serve more than one room — both the .NET host (`app.Map("/ws/{roomId}", ...)`,
/// `WebApplicationExtensions.cs`) and the Rust server (`ws_handler.rs`'s `handler` extracts
/// `room_id` from the URL path once via `Path(room_id)` and never re-reads it per message; `hello`
/// carries a `room` field but it is not used to switch rooms mid-connection) tie a socket to
/// exactly one room for its whole lifetime. So "one socket, N rooms" is not an option on either
/// server implementation — this class instead owns one <see cref="RoomConnection"/> (one
/// WebSocket, one reconnect loop, one outbound queue) per joined room, managed by this single
/// hosted service. Rooms joined: the workgroup room (`RoomOptions.EffectiveWorkgroupRoomId`,
/// config key Room:Workgroup, default "workgroup" — slice 6.4 replaced the hardcoded literal), and
/// every repo room this peer has bound (<see cref="BoundRepoRooms"/>) — reconciled on an owned
/// background loop (<see cref="MembershipLoopAsync"/>) so a repo bound while already connected
/// joins its room without a restart (D1's membership rule + the slice's own "join/leave follows
/// binding" note). Leave-on-unbind is out of scope — there is no "unbind a repo" operation
/// anywhere in this codebase today, so a joined room is never proactively dropped.
///
/// Slice 7.3 (plans/cas-distribution-and-storage.md Phase 7) — this peer's OWN room
/// (`HeadlessPeerOptions.RoomId`, still literally "studio") is deliberately EXCLUDED from the set
/// above. Pre-7.3 it was joined upstream too — a pre-6.3 transition artifact from when "studio"
/// was the only room this class knew about — which meant every peer connected to the same server
/// joined the exact same literal room name for what is actually peer-private local state
/// (settings, profiles, scheduler, registry bindings w/ local paths, gc runs, execution events):
/// every peer's own private rows collided/LWW-raced against every other peer's the moment
/// replication caught up (see `RepositoryRegistryService.RefreshAsync`'s own comment for the
/// concrete bug this caused, and `ReconcileMembershipAsync`/`NotifyLocalWriteAsync`'s own comments
/// for the fix). The room stays fully functional as a LOCAL engine room — every read/write/
/// persistence path through it is unchanged — it is simply never a wire-visible room to any other
/// peer or server, in either direction.
///
///   - Inbound (per room connection): both the post-hello catch-up and any live "pack" message
///     (same wire type, "pack" — the server never actually sends a "catch-up-pack" type despite
///     that being this class's old assumption; see ws_handler.rs's assemble_catchup_pack_envelope,
///     kind "pack") are applied via ImportPack against the local engine bridge for THAT room,
///     persisted durably (PersistInboundPackAsync), and replayed into that room's live map
///     (IStudioNodeStoreReplicationSink, now roomId-aware — see that interface's own comment) so
///     IStudioNodeStore/IWorkgroupRepositoryDirectory reads on this peer reflect the change without
///     a restart.
///   - Outbound (per room connection): NodalMergeStudioNodeStore/WorkgroupRepositoryDirectory (and
///     the legacy migration) call this class via IStudioReplicationOutbound after every successful
///     local write, handing over the room id and the just-promoted sync-graph node id. This class
///     fetches exactly that node's bytes (HostCommand::MstDone{ids:[nodeIdHex]} — a precise
///     inclusion-based fetch, not a derived "delta"; see PromoteLatestCheckpointToGraph's comment
///     for why a known-ids-tracking delta isn't viable from the .NET side) and pushes it upstream
///     on that room's own background loop, and separately broadcasts it to any peer directly
///     connected to this host's own /ws/{room} endpoint via RuntimeRoomBroker — replacing
///     StudioCrdtSyncBackgroundService's retired 30 s tick. Slice 6.3 fixes the gap 6.2 left open:
///     NotifyLocalWriteAsync previously ignored any roomId other than options.RoomId (silently
///     dropping workgroup-room upstream pushes) — it now routes to whichever room's connection
///     matches, opportunistically creating one if the membership loop hasn't reconciled it yet.
///   - Frontier (per room connection): hello declares that room's real current frontier
///     (HostCommand::GetFrontier), not an empty array. If the server's "welcome" response reports
///     our declared frontier as "missing" (it doesn't recognize our current state — e.g. a fresh
///     process whose in-memory pending queue was lost), this class falls back to pushing its entire
///     local pack once (HostCommand::RequestServerPack{known_ids:[]}) to guarantee convergence;
///     this is the one "full pack" fallback path, used only for that recovery case, never for
///     steady-state writes.
/// </summary>
public sealed class RoomPeerClient(
    HeadlessPeerOptions options,
    RoomOptions roomOptions,
    WorkspaceOptions workspaceOptions,
    IServiceProvider services,
    IHostApplicationLifetime appLifetime,
    ILogger<RoomPeerClient> logger) : IHostedService, IAsyncDisposable, IStudioReplicationOutbound
{
    // roomId -> connection. Populated by ReconcileMembershipAsync; never shrunk (see class comment
    // — no unbind operation exists to trigger a leave). Also grown opportunistically by
    // NotifyLocalWriteAsync if a write races the membership loop's next tick.
    private readonly ConcurrentDictionary<string, RoomConnection> _connections = new(StringComparer.Ordinal);

    // Slice 6.5 Part 1 — each joined room owns its own RoomConnection with its own independent
    // receive loop (this class's own comment: "one socket, one room, one reconnect loop"), so this
    // peer can have several ApplyInboundPackForRoomAsync calls in flight concurrently — one per
    // room — all sharing the SAME underlying engine bridge/dag-persistence collaborators, which
    // never had any cross-room serialization before this slice. Serializing it here is a defensive
    // hardening this slice adds while investigating a real regression the cache-refresh addition
    // below caused (see RehydratableRefreshCoordinator and RepositoryRegistryService.RefreshAsync
    // for the actual root cause and fix — a data-model issue, not a concurrency one) — kept because
    // it closes a genuine, independently-worth-having gap (concurrent inbound-pack application
    // across rooms was always able to race against itself) even though it turned out not to be
    // this particular bug's cause.
    private readonly SemaphoreSlim _inboundApplyGate = new(1, 1);

    private static readonly TimeSpan MembershipReconcileInterval = TimeSpan.FromSeconds(5);

    private CancellationTokenSource? _lifetimeCts;
    private Task? _membershipLoop;
    private string? _peerId;

    // Primary-constructor captures re-exposed as members so the nested RoomConnection class can
    // reach them through its `owner` reference (C# primary-ctor parameters are only in scope for
    // the declaring class's own bodies, not nested types).
    private ILogger<RoomPeerClient> Logger => logger;
    private HeadlessPeerOptions Options => options;
    private IHostApplicationLifetime AppLifetime => appLifetime;

    // Lazily resolved via the service provider rather than taken as ordinary constructor
    // parameters: RoomPeerClient is always registered as an IHostedService (both Build() and
    // BuildPeer()), so the generic host constructs it unconditionally at startup even when
    // HostUri is never configured. Touching IRuntimeCommandBridge eagerly would load the native
    // engine DLL on every standalone/test host — the same reasoning IStudioGraphPromoter's own
    // deferred-resolution comment documents elsewhere in this codebase.
    private IRuntimeCommandBridge? _bridge;
    private IRuntimeCommandBridge? Bridge => _bridge ??= services.GetService<IRuntimeCommandBridge>();

    private RuntimeDagPersistenceService? _dagPersistence;
    private RuntimeDagPersistenceService? DagPersistence => _dagPersistence ??= services.GetService<RuntimeDagPersistenceService>();

    private IStudioNodeStoreReplicationSink? _replicationSink;
    private IStudioNodeStoreReplicationSink? ReplicationSink => _replicationSink ??= services.GetService<IStudioNodeStoreReplicationSink>();

    // Slice 6.5 Part 1 — the cache-refresh half of inbound pack application (see
    // IStudioCacheRefreshCoordinator's own doc comment). Same lazy-optional-collaborator shape as
    // every other deferred resolution on this class.
    private IStudioCacheRefreshCoordinator? _cacheRefreshCoordinator;
    private IStudioCacheRefreshCoordinator? CacheRefreshCoordinator => _cacheRefreshCoordinator ??= services.GetService<IStudioCacheRefreshCoordinator>();

    private RuntimeRoomBroker? _roomBroker;
    private RuntimeRoomBroker? RoomBroker => _roomBroker ??= services.GetService<RuntimeRoomBroker>();

    // Slice 6.3 — source of "which repo rooms has this peer bound" (BoundRepoRooms). Same lazy
    // resolution reasoning as the collaborators above; also breaks a potential DI ordering
    // surprise since IRepositoryRegistryService pulls in IStudioNodeStore, which itself resolves
    // this exact RoomPeerClient singleton as IStudioReplicationOutbound.
    private IRepositoryRegistryService? _repositoryRegistry;
    private IRepositoryRegistryService? RepositoryRegistry => _repositoryRegistry ??= services.GetService<IRepositoryRegistryService>();

    public bool IsConnected => _connections.Values.Any(c => c.IsConnected);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.HostUri))
        {
            logger.LogInformation("[RoomPeerClient] No HostUri configured — running standalone (no upstream room presence)");
            return Task.CompletedTask;
        }

        _lifetimeCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _peerId = ResolveOrCreatePeerId();
        _membershipLoop = Task.Run(() => MembershipLoopAsync(_lifetimeCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_lifetimeCts is not null)
            await _lifetimeCts.CancelAsync();

        if (_membershipLoop is not null)
        {
            try { await _membershipLoop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }

        foreach (var connection in _connections.Values)
            await connection.StopAsync().ConfigureAwait(false);

        // Cleared (not merely stopped) so a subsequent StartAsync — e.g. the two-host integration
        // test's kill/reconnect scenario — rebuilds fresh connections against a fresh _lifetimeCts
        // rather than trying to reuse ones whose owning CancellationTokenSource is now cancelled.
        _connections.Clear();
    }

    // Slice 6.3 — joins "workgroup" + every bound repo room, and re-reconciles periodically so a
    // repo bound after startup joins its room without a restart. Polling (rather than an event/
    // callback from RepositoryRegistryService) is the simplest correct thing: that service has no
    // "repo just got bound" notification today, and reconciliation itself is cheap (an in-memory
    // ConcurrentDictionary scan, per BoundRepoRooms). Slice 7.3 removed this peer's own room
    // (options.RoomId) from the joined set entirely — see the class comment's 7.3 section.
    private async Task MembershipLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReconcileMembershipAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[RoomPeerClient] membership reconcile failed — will retry next interval");
            }

            await Task.Delay(MembershipReconcileInterval, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private async Task ReconcileMembershipAsync(CancellationToken ct)
    {
        // Slice 7.3 — options.RoomId (this peer's own room, still literally "studio") is
        // deliberately NOT in this set; see the class comment's 7.3 section and
        // NotifyLocalWriteAsync's own comment for the matching downstream-broadcast half of the
        // same fix.
        var target = new HashSet<string>(StringComparer.Ordinal) { roomOptions.EffectiveWorkgroupRoomId };

        var registry = RepositoryRegistry;
        if (registry is not null)
        {
            foreach (var repoRoomId in await BoundRepoRooms.GetBoundRepoRoomIdsAsync(registry, ct).ConfigureAwait(false))
                target.Add(repoRoomId);
        }

        foreach (var roomId in target)
            EnsureConnection(roomId);
    }

    // Idempotent: GetOrAdd only constructs+starts a connection the first time a given roomId is
    // seen by this peer, exactly the "join without restart" behavior the membership rule needs.
    private RoomConnection EnsureConnection(string roomId)
    {
        var lifetimeToken = _lifetimeCts?.Token ?? CancellationToken.None;
        return _connections.GetOrAdd(roomId, id =>
        {
            var connection = new RoomConnection(id, this);
            connection.Start(lifetimeToken);
            return connection;
        });
    }

    // IStudioReplicationOutbound — called by NodalMergeStudioNodeStore/WorkgroupRepositoryDirectory
    // after every successful local write, whether or not an upstream connection exists for that
    // room. Enqueuing is synchronous and never touches the network directly, keeping the write's
    // hot path free of I/O latency; the actual send happens on that room's own background loop.
    public Task NotifyLocalWriteAsync(string roomId, string nodeIdHex, CancellationToken cancellationToken = default)
    {
        // Slice 7.3 — the peer's own room (options.RoomId, still literally "studio") is
        // peer-private local state; it is never joined or pushed upstream (see
        // ReconcileMembershipAsync), and for the identical collision reason it must never be
        // broadcast downstream either. In the embedded-server topology (StudioWebApplication.
        // Build()), this host's own /ws/{roomId} endpoint is served by the SAME process that also
        // owns this local "studio" state — a second peer connecting directly to this host's own
        // /ws/studio endpoint would receive this host's private settings/profile/scheduler/etc.
        // writes exactly as if it had joined a shared room: the identical collision the upstream-
        // membership fix above closes, just mirrored on the downstream side of the same process
        // (a headless BuildPeer() instance normally has no HTTP/WS server at all, but nothing stops
        // one from being added later, and RuntimeWebSocketLoopRunner's server-side /ws/{roomId}
        // handling is shared code either way — this guard belongs at the one call site both
        // topologies share, not duplicated per-host-kind). Both halves below are skipped
        // unconditionally for the private room; "studio" stays fully functional as a LOCAL engine
        // room — only replication, in either direction, is severed.
        if (string.Equals(roomId, options.RoomId, StringComparison.Ordinal))
            return Task.CompletedTask;

        // Downstream: broadcast to any peer directly connected to THIS host's own /ws/{roomId}
        // endpoint. Unconditional regardless of upstream HostUri config — a standalone-upstream
        // embedded host may still serve downstream peers. Best-effort/fire-and-forget: a failure
        // here must never fail the write that triggered it (already durable by the time this
        // runs), and RuntimeRoomBroker/IRuntimeCommandBridge access is synchronous+cheap once
        // resolved, so this doesn't need its own owned background loop.
        _ = BroadcastLocallyBestEffortAsync(roomId, nodeIdHex, cancellationToken);

        // Upstream: only worth queuing when an upstream is actually configured — otherwise this
        // would grow unboundedly for the lifetime of a genuinely standalone process that never
        // connects anywhere. Slice 6.3: previously this only accepted roomId == options.RoomId
        // (silently dropping workgroup/repo-room writes — the exact gap 6.2's own note flagged);
        // now any room is accepted, opportunistically creating its connection if the membership
        // loop (StartAsync-driven) hasn't reconciled it yet — e.g. a repo bound and written to in
        // the same instant a fresh connection would still be spinning up.
        if (!string.IsNullOrWhiteSpace(options.HostUri) && _lifetimeCts is not null)
        {
            EnsureConnection(roomId).EnqueueOutbound(nodeIdHex);
        }

        return Task.CompletedTask;
    }

    private async Task BroadcastLocallyBestEffortAsync(string roomId, string nodeIdHex, CancellationToken cancellationToken)
    {
        var roomBroker = RoomBroker;
        var bridge = Bridge;
        if (roomBroker is null || bridge is null)
            return;

        try
        {
            var packJson = BuildPackEnvelopeForIds(bridge, roomId, [nodeIdHex]);
            if (packJson is not null)
                await roomBroker.BroadcastAsync(roomId, packJson, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RoomPeerClient] Local downstream broadcast failed room={Room} node={NodeId}", roomId, nodeIdHex);
        }
    }

    // Back-compat entry point for the default/local room (options.RoomId) — the shape
    // Integration.Tests already drives directly against a fresh RoomPeerClient with no live
    // WebSocket connection. Multi-room callers (each RoomConnection's own receive loop) call
    // ApplyInboundPackForRoomAsync directly with their own room id instead.
    internal Task HandleInboundPackAsync(string json, CancellationToken ct) =>
        ApplyInboundPackForRoomAsync(options.RoomId, json, ct);

    // Inbound apply: ImportPack into roomId's local sync graph, persist durably, then replay
    // canonical resolution into that room's live map so IStudioNodeStore/IWorkgroupRepositoryDirectory
    // reads reflect it. Deliberately does NOT go through WriteNodeAsync / IStudioReplicationOutbound
    // at any point — that is the structural loop-prevention mechanism (see class comment and
    // IStudioReplicationOutbound's doc comment): an inbound-applied node can never re-enter the
    // outbound path and echo back upstream, because it never touches the per-entity write path.
    // Internal (not private) so Integration.Tests can drive it directly against a real engine-
    // backed store pair without a WebSocket connection — see NodalMerge.Studio.Host.csproj's
    // InternalsVisibleTo.
    internal async Task ApplyInboundPackForRoomAsync(string roomId, string json, CancellationToken ct)
    {
        // See _inboundApplyGate's own doc comment — serializes this against every OTHER room's
        // concurrent inbound-pack application on this same peer, not just against itself.
        await _inboundApplyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ApplyInboundPackForRoomCoreAsync(roomId, json, ct).ConfigureAwait(false);
        }
        finally
        {
            _inboundApplyGate.Release();
        }
    }

    private async Task ApplyInboundPackForRoomCoreAsync(string roomId, string json, CancellationToken ct)
    {
        string? nodesB64;
        try
        {
            using var doc = JsonDocument.Parse(json);
            nodesB64 = doc.RootElement.TryGetProperty("nodes", out var nodesEl) && nodesEl.ValueKind == JsonValueKind.String
                ? nodesEl.GetString()
                : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[RoomPeerClient] Malformed pack payload room={Room} — skipping", roomId);
            return;
        }

        if (string.IsNullOrWhiteSpace(nodesB64))
            return;

        var bridge = Bridge;
        var dagPersistence = DagPersistence;
        if (bridge is null || dagPersistence is null)
        {
            logger.LogWarning("[RoomPeerClient] Inbound pack received room={Room} but the engine bridge/persistence service is unavailable — dropped", roomId);
            return;
        }

        // Defensive: ImportPack requires the room to already exist engine-side (RoomNotFound
        // otherwise). RunLoopAsync already hydrates/ensures the room before connecting, so in the
        // normal running-process flow this is a cheap no-op (HydrateRoomIfNeededAsync caches per
        // room), but this method must be self-sufficient for any other caller too (e.g. direct test
        // invocation against a fresh store that has never read or written anything yet).
        await dagPersistence.HydrateRoomIfNeededAsync(roomId, ct).ConfigureAwait(false);

        var importStatus = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { ImportPack = new { nodes_b64 = nodesB64 } }
        })).Status;

        if (importStatus != AsStatus.Ok)
        {
            logger.LogWarning("[RoomPeerClient] ImportPack failed room={Room} status={Status}", roomId, importStatus);
            return;
        }

        await dagPersistence.PersistInboundPackAsync(roomId, nodesB64, ct).ConfigureAwait(false);

        var replicationSink = ReplicationSink;
        if (replicationSink is not null)
        {
            // Bridges the room_maps/sync-graph split (see NodalMergeStudioNodeStore's class
            // comment and IStudioNodeStoreReplicationSink's doc comment). Makes the just-imported
            // pack visible to a *future* IStudioNodeStore read; the cache-refresh step below is
            // what makes it visible to a service's *already-populated* in-memory dictionary
            // without waiting for one.
            try
            {
                await replicationSink.RehydrateLiveMapFromCanonicalResolutionAsync(roomId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[RoomPeerClient] Live-map replay after inbound pack failed room={Room}", roomId);
            }
        }
        else
        {
            logger.LogDebug(
                "[RoomPeerClient] No IStudioNodeStoreReplicationSink registered — inbound pack applied to the sync graph only room={Room}", roomId);
        }

        // Slice 6.5 Part 1 — re-pull every IRehydratable service's in-memory cache so this peer's
        // own REST/UI reads (which never go through IStudioNodeStore directly — see
        // IStudioCacheRefreshCoordinator's own doc comment) reflect the just-applied inbound pack
        // without a restart. Awaited inline (not deferred to a background loop) — a real two-process
        // smoke run (docs/guides/multi-user-smoke.md) confirmed this works correctly end to end.
        var cacheRefreshCoordinator = CacheRefreshCoordinator;
        if (cacheRefreshCoordinator is not null)
        {
            try
            {
                await cacheRefreshCoordinator.RefreshAfterInboundPackAsync(roomId, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[RoomPeerClient] In-memory cache refresh after inbound pack failed room={Room}", roomId);
            }
        }
    }

    // Precise inclusion-based fetch: HostCommand::MstDone{ids} packs exactly the requested node
    // ids (graph.get_nodes(&requested) in engine.rs) — a true minimal delta requiring no "known
    // ids" bookkeeping at all, unlike RequestServerPack's known_ids-based exclusion filter (see
    // PromoteLatestCheckpointToGraph's comment for why that's not viable to track correctly from
    // the .NET side). Reused for both the local downstream broadcast and the upstream push.
    private static string? BuildPackEnvelopeForIds(IRuntimeCommandBridge? bridge, string roomId, IReadOnlyList<string> nodeIdsHex)
    {
        if (bridge is null || nodeIdsHex.Count == 0)
            return null;

        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { MstDone = new { ids = nodeIdsHex } }
        }));
        if (response.Status != AsStatus.Ok)
            return null;

        return ExtractServerPackPreparedEnvelope(response.EventsJson, roomId);
    }

    // Fallback path (see class comment): the entire local pack, used only when the server has
    // told us it doesn't recognize our current frontier.
    private static string? BuildFullPackEnvelope(IRuntimeCommandBridge? bridge, string roomId)
    {
        if (bridge is null)
            return null;

        var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = roomId,
            command = new { RequestServerPack = new { known_ids = Array.Empty<string>() } }
        }));
        if (response.Status != AsStatus.Ok)
            return null;

        return ExtractServerPackPreparedEnvelope(response.EventsJson, roomId);
    }

    private static string? ExtractServerPackPreparedEnvelope(string eventsJson, string roomId)
    {
        using var eventsDoc = JsonDocument.Parse(eventsJson);
        if (eventsDoc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var evt in eventsDoc.RootElement.EnumerateArray())
        {
            if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("ServerPackPrepared", out var serverPack))
                continue;
            if (!serverPack.TryGetProperty("nodes_b64", out var nodesB64Node) || nodesB64Node.ValueKind != JsonValueKind.String)
                continue;

            var nodesB64 = nodesB64Node.GetString();
            if (string.IsNullOrWhiteSpace(nodesB64))
                continue;

            return JsonSerializer.Serialize(new { type = "pack", room = roomId, nodes = nodesB64 });
        }

        return null;
    }

    private string ResolveOrCreatePeerId()
    {
        if (!string.IsNullOrWhiteSpace(options.PeerId))
            return options.PeerId;

        var dir = string.IsNullOrWhiteSpace(workspaceOptions.RootPath)
            ? Path.GetTempPath()
            : workspaceOptions.RootPath;

        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ".peer-id");

        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (!string.IsNullOrWhiteSpace(existing))
            {
                logger.LogInformation("[RoomPeerClient] Using persisted peer_id={PeerId}", existing);
                return existing;
            }
        }

        var peerId = Guid.NewGuid().ToString("N");
        File.WriteAllText(path, peerId);
        logger.LogInformation("[RoomPeerClient] Generated new peer_id={PeerId} persisted to {Path}", peerId, path);
        return peerId;
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        // Idempotency guard: RoomPeerClient is registered under three service types resolving to
        // the same singleton instance (itself, IHostedService, IStudioReplicationOutbound), and
        // the generic host's container disposes each *registration*, not each unique instance — so
        // without this guard, DisposeAsync would run once per registration and the second
        // _lifetimeCts.CancelAsync() would throw ObjectDisposedException against the already-disposed CTS.
        if (_disposed)
            return;
        _disposed = true;

        if (_lifetimeCts is not null)
        {
            await _lifetimeCts.CancelAsync();
            _lifetimeCts.Dispose();
        }

        foreach (var connection in _connections.Values)
            await connection.StopAsync().ConfigureAwait(false);
    }

    // Test hook only (internal — see NodalMerge.Studio.Host.csproj's InternalsVisibleTo). Sum
    // across every joined room's connection — 0 when none exist, matching the pre-6.3 single-room
    // semantics for tests that never call StartAsync (no connections are ever created).
    internal int PendingOutboundCountForTests => _connections.Values.Sum(c => c.PendingCount);

    // Test hook only (internal — see NodalMerge.Studio.Host.csproj's InternalsVisibleTo). Slice
    // 7.3 — lets a test assert the upstream membership SET directly (has this peer ever joined a
    // connection for this room id) rather than inferring it indirectly from replication timing,
    // which is exactly the fast/deterministic check the "studio" room must now always fail.
    internal bool HasJoinedRoomForTests(string roomId) => _connections.ContainsKey(roomId);

    private readonly record struct OutboundItem(string? NodeIdHex, bool IsFullPackFallback)
    {
        public static OutboundItem ForNode(string nodeIdHex) => new(nodeIdHex, false);
        public static OutboundItem FullPackFallback() => new(null, true);
    }

    // One WebSocket connection, one room — see the class comment's wire verdict for why a single
    // connection can't serve more than one room. Owns its own reconnect-with-backoff loop, outbound
    // queue, and declared-frontier bookkeeping; delegates every engine-bridge/persistence/
    // replication concern back to the owning RoomPeerClient so that logic lives in exactly one
    // place regardless of how many rooms are joined.
    private sealed class RoomConnection(string roomId, RoomPeerClient owner)
    {
        public string RoomId { get; } = roomId;
        public bool IsConnected { get; private set; }

        private CancellationTokenSource? _cts;
        private Task? _runLoop;

        // Not SingleReader: PendingCount relies on ChannelReader.Count, which the
        // SingleConsumerUnboundedChannel variant doesn't support. There is only ever one real
        // reader (OutboundLoopAsync) in practice regardless.
        private readonly Channel<OutboundItem> _pending = Channel.CreateUnbounded<OutboundItem>();

        // What this room last declared as its frontier in "hello", so HandleWelcome can tell
        // whether the server's "missing" list means "doesn't recognize our current tip" (triggers
        // the full-pack fallback) rather than routine catch-up bookkeeping. Per-connection: each
        // room has its own frontier, so this must not be shared across rooms.
        private string[] _lastDeclaredFrontier = [];

        public int PendingCount => _pending.Reader.Count;

        public void Start(CancellationToken parentCt)
        {
            _cts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
            _runLoop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        }

        public async Task StopAsync()
        {
            if (_cts is not null)
            {
                try { await _cts.CancelAsync(); }
                catch (ObjectDisposedException) { }
            }

            if (_runLoop is not null)
            {
                try { await _runLoop; }
                catch (OperationCanceledException) { }
                catch (WebSocketException) { }
            }
        }

        public void EnqueueOutbound(string nodeIdHex) => _pending.Writer.TryWrite(OutboundItem.ForNode(nodeIdHex));

        private async Task RunLoopAsync(CancellationToken ct)
        {
            var delayMs = 1000;

            while (!ct.IsCancellationRequested)
            {
                using var ws = new ClientWebSocket();
                try
                {
                    // Ensure the local engine room exists (and is hydrated from any persisted
                    // history) before computing the frontier we declare in hello — GetFrontier
                    // fails on a room the engine doesn't know about yet, which is otherwise a real
                    // race against the owning store's own lazy EnsureInitializedAsync.
                    var dagPersistence = owner.DagPersistence;
                    if (dagPersistence is not null)
                        await dagPersistence.HydrateRoomIfNeededAsync(RoomId, ct).ConfigureAwait(false);

                    var uri = BuildWebSocketUri();
                    var peerId = owner._peerId ?? owner.ResolveOrCreatePeerId();
                    owner.Logger.LogInformation("[RoomPeerClient] Connecting to {Uri} room={Room} as peer_id={PeerId} peer_type={PeerType}",
                        uri, RoomId, peerId, owner.Options.PeerType);

                    await ws.ConnectAsync(uri, ct);
                    IsConnected = true;
                    delayMs = 1000;

                    await SendHelloAsync(ws, peerId, ct);

                    // Receive and outbound-drain run concurrently for the lifetime of this
                    // connection. ClientWebSocket supports one concurrent reader and one concurrent
                    // writer, so this is safe as long as nothing else calls SendAsync/ReceiveAsync
                    // on `ws` — both loops below respect that split.
                    using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var receiveTask = ReceiveLoopAsync(ws, peerId, connectionCts.Token);
                    var outboundTask = OutboundLoopAsync(ws, connectionCts.Token);
                    try
                    {
                        await Task.WhenAny(receiveTask, outboundTask).ConfigureAwait(false);
                    }
                    finally
                    {
                        await connectionCts.CancelAsync();
                        await SafeAwaitAsync(receiveTask).ConfigureAwait(false);
                        await SafeAwaitAsync(outboundTask).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    owner.Logger.LogWarning(ex, "[RoomPeerClient] Connection lost room={Room} — reconnecting in {DelayMs}ms", RoomId, delayMs);
                }
                finally
                {
                    IsConnected = false;
                    if (ws.State == WebSocketState.Open)
                    {
                        try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "shutting down", CancellationToken.None); }
                        catch { /* best effort */ }
                    }
                }

                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(delayMs, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                    delayMs = Math.Min(delayMs * 2, 30_000);
                }
            }

            owner.Logger.LogInformation("[RoomPeerClient] Disconnected room={Room}", RoomId);
        }

        private static async Task SafeAwaitAsync(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task SendHelloAsync(ClientWebSocket ws, string peerId, CancellationToken ct)
        {
            var frontier = TryGetLocalFrontierHex();
            _lastDeclaredFrontier = frontier;

            var hello = JsonSerializer.Serialize(new
            {
                type = "hello",
                room = RoomId,
                pubkey = peerId,
                peer_id = peerId,
                peer_type = owner.Options.PeerType,
                frontier
            });

            var bytes = Encoding.UTF8.GetBytes(hello);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
            owner.Logger.LogDebug("[RoomPeerClient] Sent hello room={Room} frontier_count={FrontierCount}", RoomId, frontier.Length);
        }

        // HostCommand::GetFrontier reads the local sync graph's current tip node ids — this is the
        // "frontier" field the wire protocol actually consumes (parse_client_frontier_node_ids reads
        // hello["frontier"]; confirmed against engine/host-core/src/engine.rs and the JS SDK's own
        // hello construction via frontier_hex_json(), not the module doc comment in ws_handler.rs,
        // which is stale — no server code path ever sends a literal "catch-up-pack" type; catch-up
        // always arrives as an ordinary "pack" message, handled in ApplyInboundPackForRoomAsync).
        private string[] TryGetLocalFrontierHex()
        {
            var bridge = owner.Bridge;
            if (bridge is null)
                return [];

            try
            {
                var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
                {
                    room_id = RoomId,
                    command = "GetFrontier"
                }));
                if (response.Status != AsStatus.Ok)
                    return [];

                using var doc = JsonDocument.Parse(response.EventsJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Array)
                    return [];

                foreach (var evt in doc.RootElement.EnumerateArray())
                {
                    if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("FrontierQueried", out var queried))
                        continue;
                    if (!queried.TryGetProperty("frontier_heads_hex", out var heads) || heads.ValueKind != JsonValueKind.Array)
                        continue;

                    return heads.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToArray();
                }
            }
            catch (Exception ex)
            {
                owner.Logger.LogDebug(ex, "[RoomPeerClient] GetFrontier failed room={Room} — declaring empty frontier", RoomId);
            }

            return [];
        }

        private async Task ReceiveLoopAsync(ClientWebSocket ws, string peerId, CancellationToken ct)
        {
            var buffer = new byte[64 * 1024];

            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                using var messageBuffer = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(buffer, ct);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        owner.Logger.LogInformation("[RoomPeerClient] Server closed the connection room={Room} peer_id={PeerId}", RoomId, peerId);
                        return;
                    }

                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0)
                        messageBuffer.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text || messageBuffer.Length == 0)
                    continue;

                var json = Encoding.UTF8.GetString(messageBuffer.GetBuffer(), 0, (int)messageBuffer.Length);

                string? msgType = null;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    doc.RootElement.TryGetProperty("type", out var typeProp);
                    msgType = typeProp.GetString();
                }
                catch (Exception ex) { owner.Logger.LogDebug(ex, "[RoomPeerClient] Malformed JSON message room={Room} — skipping", RoomId); }

                switch (msgType)
                {
                    case "peer-joined":
                        owner.Logger.LogInformation("[RoomPeerClient] peer-joined broadcast received room={Room}", RoomId);
                        break;
                    case "peer-left":
                        owner.Logger.LogInformation("[RoomPeerClient] peer-left broadcast received room={Room}", RoomId);
                        break;
                    case "welcome":
                        HandleWelcome(json);
                        break;
                    case "pack":
                        // Both the post-hello catch-up pack and any live broadcast arrive with this
                        // same type — see the class comment's wire-reality note.
                        //
                        // Isolate a pack-apply failure from the CONNECTION lifecycle. This await is
                        // the receive loop's body: an exception escaping it faults the receive task,
                        // which tears the socket down (RunLoopAsync's Task.WhenAny) and reconnects
                        // straight back into the SAME catch-up pack — an infinite churn that blocks
                        // ALL inbound replication for the room while outbound keeps flowing (separate
                        // loop). A single un-appliable pack (e.g. a large/bloated repo-room pack, or
                        // one whose ImportPack/PersistInboundPack step throws) must not kill the
                        // connection: log it and keep receiving so live writes and later packs still
                        // land. Cancellation still propagates (shutdown/reconnect are intentional).
                        try
                        {
                            await owner.ApplyInboundPackForRoomAsync(RoomId, json, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            owner.Logger.LogWarning(ex,
                                "[RoomPeerClient] Applying inbound pack failed room={Room} — connection kept alive, this pack skipped", RoomId);
                        }
                        break;
                    case "participant.stop":
                        try
                        {
                            using var doc2 = JsonDocument.Parse(json);
                            if (doc2.RootElement.TryGetProperty("peer_id", out var pidProp)
                                && pidProp.GetString() == peerId)
                            {
                                owner.Logger.LogInformation("[RoomPeerClient] Received stop signal room={Room} — requesting application shutdown", RoomId);
                                owner.AppLifetime.StopApplication();
                            }
                        }
                        catch (Exception ex) { owner.Logger.LogDebug(ex, "[RoomPeerClient] Malformed participant.stop payload room={Room} — skipping", RoomId); }
                        break;
                    default:
                        owner.Logger.LogDebug("[RoomPeerClient] Received message room={Room} type={Type}", RoomId, msgType ?? "(unknown)");
                        break;
                }
            }
        }

        // Reconnect convergence: if the server's welcome reports (in "missing") any id we ourselves
        // just declared as our frontier, it means the server doesn't recognize our current local tip
        // at all — the in-memory pending queue from a prior connection was lost (e.g. process
        // restart), or this is a first-ever connect. Recover by queuing a one-shot full local pack
        // (RequestServerPack{known_ids:[]}); this is the "full-pack-with-server-side-dedup" fallback
        // explicitly permitted for recovery, never used for steady-state per-write pushes.
        private void HandleWelcome(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("missing", out var missingEl) || missingEl.ValueKind != JsonValueKind.Array)
                    return;

                if (_lastDeclaredFrontier.Length == 0)
                    return;

                var missing = missingEl.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .ToHashSet(StringComparer.Ordinal);

                if (_lastDeclaredFrontier.Any(missing.Contains))
                {
                    owner.Logger.LogInformation(
                        "[RoomPeerClient] Server does not recognize our declared frontier room={Room} — queuing a full local pack for convergence", RoomId);
                    _pending.Writer.TryWrite(OutboundItem.FullPackFallback());
                }
            }
            catch (Exception ex)
            {
                owner.Logger.LogDebug(ex, "[RoomPeerClient] Malformed welcome payload room={Room} — skipping missing-frontier check", RoomId);
            }
        }

        // Owned background send loop: drains `_pending`, coalescing everything immediately available
        // into one batch per send (so a burst of local writes doesn't become a send-per-write storm),
        // and pushes the resulting pack upstream. Started/stopped strictly within this connection's
        // lifetime — never runs unattended past a StopAsync/dispose.
        private async Task OutboundLoopAsync(ClientWebSocket ws, CancellationToken ct)
        {
            while (await _pending.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                var items = new List<OutboundItem>();
                while (_pending.Reader.TryRead(out var item))
                    items.Add(item);

                if (items.Count == 0)
                    continue;

                var bridge = owner.Bridge;
                string? packJson;
                try
                {
                    packJson = items.Any(i => i.IsFullPackFallback)
                        ? BuildFullPackEnvelope(bridge, RoomId)
                        : BuildPackEnvelopeForIds(bridge, RoomId, items
                            .Where(i => i.NodeIdHex is not null)
                            .Select(i => i.NodeIdHex!)
                            .Distinct(StringComparer.Ordinal)
                            .ToArray());
                }
                catch (Exception ex)
                {
                    owner.Logger.LogWarning(ex, "[RoomPeerClient] Failed to build outbound pack room={Room} — requeuing", RoomId);
                    foreach (var item in items) _pending.Writer.TryWrite(item);
                    continue;
                }

                if (packJson is null)
                {
                    // Nothing to send (bridge unavailable, or the ids no longer resolve to anything —
                    // e.g. already superseded by a subsequent full-pack fallback in the same batch).
                    continue;
                }

                try
                {
                    var bytes = Encoding.UTF8.GetBytes(packJson);
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
                    owner.Logger.LogDebug(
                        "[RoomPeerClient] Pushed outbound pack room={Room} node_count={NodeCount} full_pack_fallback={FullPackFallback}",
                        RoomId, items.Count, items.Any(i => i.IsFullPackFallback));
                }
                catch
                {
                    // Send failed (connection is going down) — requeue so RunLoopAsync's reconnect
                    // picks this batch back up on the next connection instead of silently dropping it.
                    foreach (var item in items) _pending.Writer.TryWrite(item);
                    throw;
                }
            }
        }

        private Uri BuildWebSocketUri()
        {
            var base_ = owner.Options.HostUri!.TrimEnd('/');
            var room = Uri.EscapeDataString(RoomId);
            return new Uri($"{base_}/ws/{room}");
        }
    }
}
