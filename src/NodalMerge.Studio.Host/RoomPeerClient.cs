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
/// Maintains an outbound WebSocket connection to a nodalmerge host room, presenting this
/// process as a named peer. Handles reconnection with exponential backoff. When HostUri is
/// null the client is a no-op for the upstream half — the process runs standalone with no
/// upstream room presence (it may still serve downstream peers on its own /ws/{room} endpoint,
/// see NotifyLocalWriteAsync).
///
/// Slice 6.1b (plans/cas-distribution-and-storage.md Phase 6) rewrote this from a one-way
/// "hello with an empty frontier, then log-and-discard inbound packs" stub into a real
/// bidirectional replication client:
///   - Inbound: both the post-hello catch-up and any live "pack" message (same wire type, "pack" —
///     the server never actually sends a "catch-up-pack" type despite that being this class's old
///     assumption; see ws_handler.rs's assemble_catchup_pack_envelope, kind "pack") are applied via
///     ImportPack against the local engine bridge, persisted durably (PersistInboundPackAsync), and
///     replayed into the live map (IStudioNodeStoreReplicationSink) so IStudioNodeStore reads on
///     this peer reflect the change without a restart.
///   - Outbound: NodalMergeStudioNodeStore (and the legacy migration) call this class via
///     IStudioReplicationOutbound after every successful local write, handing over the just-
///     promoted sync-graph node id. This class fetches exactly that node's bytes (HostCommand::
///     MstDone{ids:[nodeIdHex]} — a precise inclusion-based fetch, not a derived "delta"; see
///     PromoteLatestCheckpointToGraph's comment for why a known-ids-tracking delta isn't viable
///     from the .NET side) and pushes it upstream on an owned background loop, and separately
///     broadcasts it to any peer directly connected to this host's own /ws/{room} endpoint via
///     RuntimeRoomBroker — replacing StudioCrdtSyncBackgroundService's retired 30 s tick.
///   - Frontier: hello declares this peer's real current frontier (HostCommand::GetFrontier), not
///     an empty array. If the server's "welcome" response reports our declared frontier as
///     "missing" (it doesn't recognize our current state — e.g. a fresh process whose in-memory
///     pending queue was lost), this class falls back to pushing its entire local pack once
///     (HostCommand::RequestServerPack{known_ids:[]}) to guarantee convergence; this is the one
///     "full pack" fallback path, used only for that recovery case, never for steady-state writes.
/// </summary>
public sealed class RoomPeerClient(
    HeadlessPeerOptions options,
    WorkspaceOptions workspaceOptions,
    IServiceProvider services,
    IHostApplicationLifetime appLifetime,
    ILogger<RoomPeerClient> logger) : IHostedService, IAsyncDisposable, IStudioReplicationOutbound
{
    private CancellationTokenSource? _cts;
    private Task? _runLoop;

    // Outbound queue of node ids (or a full-pack-fallback marker) pending an upstream push.
    // Populated by NotifyLocalWriteAsync (called on every local write, connected or not) and by
    // HandleWelcome's fallback trigger; drained by OutboundLoopAsync only while a connection's
    // hello handshake has completed. Survives across reconnects (it's a level-scoped field, not
    // per-connection state) — that's the "flush pending on reconnect" behavior: writes made while
    // disconnected just accumulate here until the next successful connection drains them.
    // Not SingleReader: that variant (SingleConsumerUnboundedChannel) doesn't support
    // ChannelReader.Count, which PendingOutboundCountForTests relies on. There is only ever one
    // real reader (OutboundLoopAsync) in practice regardless.
    private readonly Channel<OutboundItem> _pending = Channel.CreateUnbounded<OutboundItem>();

    // What we last declared as our frontier in "hello", so HandleWelcome can tell whether the
    // server's "missing" list means "doesn't recognize our current tip" (triggers the full-pack
    // fallback) rather than routine catch-up bookkeeping.
    private string[] _lastDeclaredFrontier = [];

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

    private RuntimeRoomBroker? _roomBroker;
    private RuntimeRoomBroker? RoomBroker => _roomBroker ??= services.GetService<RuntimeRoomBroker>();

    public bool IsConnected { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.HostUri))
        {
            logger.LogInformation("[RoomPeerClient] No HostUri configured — running standalone (no upstream room presence)");
            return Task.CompletedTask;
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runLoop = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is not null)
            await _cts.CancelAsync();

        if (_runLoop is not null)
        {
            try { await _runLoop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { }
        }
    }

    // IStudioReplicationOutbound — called by NodalMergeStudioNodeStore after every successful
    // local write, whether or not an upstream connection exists. Enqueuing is synchronous and
    // never touches the network directly, keeping the write's hot path free of I/O latency; the
    // actual send happens on the owned OutboundLoopAsync background loop.
    public Task NotifyLocalWriteAsync(string roomId, string nodeIdHex, CancellationToken cancellationToken = default)
    {
        if (!string.Equals(roomId, options.RoomId, StringComparison.Ordinal))
        {
            logger.LogDebug(
                "[RoomPeerClient] Ignoring write notification for room={Room} — this peer serves room={OwnRoom}",
                roomId, options.RoomId);
            return Task.CompletedTask;
        }

        // Downstream: broadcast to any peer directly connected to THIS host's own /ws/{room}
        // endpoint. Unconditional regardless of upstream HostUri config — a standalone-upstream
        // embedded host may still serve downstream peers. Best-effort/fire-and-forget: a failure
        // here must never fail the write that triggered it (already durable by the time this
        // runs), and RuntimeRoomBroker/IRuntimeCommandBridge access is synchronous+cheap once
        // resolved, so this doesn't need its own owned background loop.
        _ = BroadcastLocallyBestEffortAsync(nodeIdHex, cancellationToken);

        // Upstream: only worth queuing when an upstream is actually configured — otherwise this
        // would grow unboundedly for the lifetime of a genuinely standalone process that never
        // connects anywhere.
        if (!string.IsNullOrWhiteSpace(options.HostUri))
        {
            _pending.Writer.TryWrite(OutboundItem.ForNode(nodeIdHex));
        }

        return Task.CompletedTask;
    }

    private async Task BroadcastLocallyBestEffortAsync(string nodeIdHex, CancellationToken cancellationToken)
    {
        var roomBroker = RoomBroker;
        var bridge = Bridge;
        if (roomBroker is null || bridge is null)
            return;

        try
        {
            var packJson = BuildPackEnvelopeForIds(bridge, options.RoomId, [nodeIdHex]);
            if (packJson is not null)
                await roomBroker.BroadcastAsync(options.RoomId, packJson, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[RoomPeerClient] Local downstream broadcast failed for node={NodeId}", nodeIdHex);
        }
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var peerId = ResolveOrCreatePeerId();
        var delayMs = 1000;

        while (!ct.IsCancellationRequested)
        {
            using var ws = new ClientWebSocket();
            try
            {
                // Ensure the local engine room exists (and is hydrated from any persisted history)
                // before computing the frontier we declare in hello — GetFrontier fails on a room
                // the engine doesn't know about yet, which is otherwise a real race against
                // NodalMergeStudioNodeStore's own lazy EnsureInitializedAsync.
                var dagPersistence = DagPersistence;
                if (dagPersistence is not null)
                    await dagPersistence.HydrateRoomIfNeededAsync(options.RoomId, ct).ConfigureAwait(false);

                var uri = BuildWebSocketUri();
                logger.LogInformation("[RoomPeerClient] Connecting to {Uri} as peer_id={PeerId} peer_type={PeerType}",
                    uri, peerId, options.PeerType);

                await ws.ConnectAsync(uri, ct);
                IsConnected = true;
                delayMs = 1000;

                await SendHelloAsync(ws, peerId, ct);

                // Receive and outbound-drain run concurrently for the lifetime of this connection.
                // ClientWebSocket supports one concurrent reader and one concurrent writer, so this
                // is safe as long as nothing else calls SendAsync/ReceiveAsync on `ws` — both loops
                // below respect that split. Either loop ending (cancellation, closed socket, send
                // failure) tears down this connection; the outer while loop then reconnects with
                // backoff, and OutboundLoopAsync's own send-failure handling requeues anything it
                // hadn't finished sending so no local write is silently dropped.
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
                logger.LogWarning(ex, "[RoomPeerClient] Connection lost — reconnecting in {DelayMs}ms", delayMs);
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

        logger.LogInformation("[RoomPeerClient] Disconnected");
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
            room = options.RoomId,
            pubkey = peerId,
            peer_id = peerId,
            peer_type = options.PeerType,
            frontier
        });

        var bytes = Encoding.UTF8.GetBytes(hello);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct);
        logger.LogDebug("[RoomPeerClient] Sent hello room={Room} frontier_count={FrontierCount}", options.RoomId, frontier.Length);
    }

    // HostCommand::GetFrontier reads the local sync graph's current tip node ids — this is the
    // "frontier" field the wire protocol actually consumes (parse_client_frontier_node_ids reads
    // hello["frontier"]; confirmed against engine/host-core/src/engine.rs and the JS SDK's own
    // hello construction via frontier_hex_json(), not the module doc comment in ws_handler.rs,
    // which is stale — no server code path ever sends a literal "catch-up-pack" type; catch-up
    // always arrives as an ordinary "pack" message, handled in HandleInboundPackAsync below).
    private string[] TryGetLocalFrontierHex()
    {
        var bridge = Bridge;
        if (bridge is null)
            return [];

        try
        {
            var response = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
            {
                room_id = options.RoomId,
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
            logger.LogDebug(ex, "[RoomPeerClient] GetFrontier failed — declaring empty frontier");
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
                    logger.LogInformation("[RoomPeerClient] Server closed the connection peer_id={PeerId}", peerId);
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
            catch (Exception ex) { logger.LogDebug(ex, "[RoomPeerClient] Malformed JSON message — skipping"); }

            switch (msgType)
            {
                case "peer-joined":
                    logger.LogInformation("[RoomPeerClient] peer-joined broadcast received");
                    break;
                case "peer-left":
                    logger.LogInformation("[RoomPeerClient] peer-left broadcast received");
                    break;
                case "welcome":
                    HandleWelcome(json);
                    break;
                case "pack":
                    // Both the post-hello catch-up pack and any live broadcast arrive with this
                    // same type — see the class comment's wire-reality note.
                    await HandleInboundPackAsync(json, ct).ConfigureAwait(false);
                    break;
                case "participant.stop":
                    try
                    {
                        using var doc2 = JsonDocument.Parse(json);
                        if (doc2.RootElement.TryGetProperty("peer_id", out var pidProp)
                            && pidProp.GetString() == peerId)
                        {
                            logger.LogInformation("[RoomPeerClient] Received stop signal — requesting application shutdown");
                            appLifetime.StopApplication();
                        }
                    }
                    catch (Exception ex) { logger.LogDebug(ex, "[RoomPeerClient] Malformed participant.stop payload — skipping"); }
                    break;
                default:
                    logger.LogDebug("[RoomPeerClient] Received message type={Type}", msgType ?? "(unknown)");
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
                logger.LogInformation(
                    "[RoomPeerClient] Server does not recognize our declared frontier — queuing a full local pack for convergence");
                _pending.Writer.TryWrite(OutboundItem.FullPackFallback());
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[RoomPeerClient] Malformed welcome payload — skipping missing-frontier check");
        }
    }

    // Inbound apply: ImportPack into the local sync graph, persist durably, then replay canonical
    // resolution into the live map so IStudioNodeStore reads reflect it. Deliberately does NOT go
    // through NodalMergeStudioNodeStore.WriteNodeAsync / IStudioReplicationOutbound at any point —
    // that is the structural loop-prevention mechanism (see class comment and
    // IStudioReplicationOutbound's doc comment): an inbound-applied node can never re-enter the
    // outbound path and echo back upstream, because it never touches the per-entity write path.
    // Internal (not private) so Integration.Tests can drive it directly against a real engine-
    // backed RoomPeerClient/IStudioNodeStore pair without a WebSocket connection — see
    // NodalMerge.Studio.Host.csproj's InternalsVisibleTo.
    internal async Task HandleInboundPackAsync(string json, CancellationToken ct)
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
            logger.LogDebug(ex, "[RoomPeerClient] Malformed pack payload — skipping");
            return;
        }

        if (string.IsNullOrWhiteSpace(nodesB64))
            return;

        var bridge = Bridge;
        var dagPersistence = DagPersistence;
        if (bridge is null || dagPersistence is null)
        {
            logger.LogWarning("[RoomPeerClient] Inbound pack received but the engine bridge/persistence service is unavailable — dropped");
            return;
        }

        // Defensive: ImportPack requires the room to already exist engine-side (RoomNotFound
        // otherwise). RunLoopAsync already hydrates/ensures the room before connecting, so in the
        // normal running-process flow this is a cheap no-op (HydrateRoomIfNeededAsync caches per
        // room), but HandleInboundPackAsync must be self-sufficient for any other caller too (e.g.
        // direct test invocation against a fresh NodalMergeStudioNodeStore that has never read or
        // written anything yet).
        await dagPersistence.HydrateRoomIfNeededAsync(options.RoomId, ct).ConfigureAwait(false);

        var importStatus = bridge.ProcessJsonCommand(JsonSerializer.Serialize(new
        {
            room_id = options.RoomId,
            command = new { ImportPack = new { nodes_b64 = nodesB64 } }
        })).Status;

        if (importStatus != AsStatus.Ok)
        {
            logger.LogWarning("[RoomPeerClient] ImportPack failed room={Room} status={Status}", options.RoomId, importStatus);
            return;
        }

        await dagPersistence.PersistInboundPackAsync(options.RoomId, nodesB64, ct).ConfigureAwait(false);

        var replicationSink = ReplicationSink;
        if (replicationSink is not null)
        {
            // Bridges the room_maps/sync-graph split (see NodalMergeStudioNodeStore's class
            // comment and IStudioNodeStoreReplicationSink's doc comment). NOTE: any in-memory
            // Studio service with a startup-rehydrated cache will NOT observe this mid-run change
            // — only future IStudioNodeStore reads do. Subscribing those caches to a store-changed
            // notification is out of scope for this slice.
            try
            {
                await replicationSink.RehydrateLiveMapFromCanonicalResolutionAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[RoomPeerClient] Live-map replay after inbound pack failed");
            }
        }
        else
        {
            logger.LogDebug(
                "[RoomPeerClient] No IStudioNodeStoreReplicationSink registered — inbound pack applied to the sync graph only");
        }
    }

    // Owned background send loop: drains `_pending`, coalescing everything immediately available
    // into one batch per send (so a burst of local writes doesn't become a send-per-write storm),
    // and pushes the resulting pack upstream. Started/stopped strictly within this connection's
    // lifetime by RunLoopAsync — never runs unattended past a StopAsync/dispose, satisfying the
    // "owned by the hosted-service lifecycle" requirement.
    private async Task OutboundLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        while (await _pending.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            var items = new List<OutboundItem>();
            while (_pending.Reader.TryRead(out var item))
                items.Add(item);

            if (items.Count == 0)
                continue;

            var bridge = Bridge;
            string? packJson;
            try
            {
                packJson = items.Any(i => i.IsFullPackFallback)
                    ? BuildFullPackEnvelope(bridge, options.RoomId)
                    : BuildPackEnvelopeForIds(bridge, options.RoomId, items
                        .Where(i => i.NodeIdHex is not null)
                        .Select(i => i.NodeIdHex!)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray());
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[RoomPeerClient] Failed to build outbound pack — requeuing");
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
                logger.LogDebug(
                    "[RoomPeerClient] Pushed outbound pack room={Room} node_count={NodeCount} full_pack_fallback={FullPackFallback}",
                    options.RoomId, items.Count, items.Any(i => i.IsFullPackFallback));
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

    private Uri BuildWebSocketUri()
    {
        var base_ = options.HostUri!.TrimEnd('/');
        var room = Uri.EscapeDataString(options.RoomId);
        return new Uri($"{base_}/ws/{room}");
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
        // _cts.CancelAsync() would throw ObjectDisposedException against the already-disposed CTS.
        if (_disposed)
            return;
        _disposed = true;

        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
        }
    }

    // Test hook only (internal — see NodalMerge.Studio.Host.csproj's InternalsVisibleTo). Lets
    // Integration.Tests assert on queue depth without a real WebSocket connection.
    internal int PendingOutboundCountForTests => _pending.Reader.Count;

    private readonly record struct OutboundItem(string? NodeIdHex, bool IsFullPackFallback)
    {
        public static OutboundItem ForNode(string nodeIdHex) => new(nodeIdHex, false);
        public static OutboundItem FullPackFallback() => new(null, true);
    }
}
