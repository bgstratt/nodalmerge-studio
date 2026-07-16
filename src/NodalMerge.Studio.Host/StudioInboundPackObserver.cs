using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

/// <summary>
/// Slice 7.1b (plans/cas-distribution-and-storage.md Phase 7) — the Studio-domain half of the
/// server-role inbound-pack bridge nodalmerge's 7.1 makes possible
/// (<see cref="IInboundPackObserver"/>, NodalMerge.Host.Abstractions.Providers, package 0.2.2).
///
/// Closes the 6.5 embedded-topology gap MultiUserMilestoneTests' class comment used to document
/// verbatim: when THIS host plays the room-SERVER role (a peer connects to its own /ws/{room}
/// endpoint, rather than this host connecting outward via RoomPeerClient), a genuinely
/// peer-authored inbound "pack" was previously handled entirely by RuntimeWebSocketLoopRunner
/// (nodalmerge repo) — engine-level ImportPack + persist + rebroadcast only. Nothing on that path
/// ever replayed the pack into the engine's live "room_maps" state or refreshed any
/// IRehydratable's in-memory cache, so this host's OWN service layer (IMergeService.GetAsync,
/// etc.) never observed the write — only RoomPeerClient's client-role receive loop
/// (ApplyInboundPackForRoomAsync) did that bridging, and a host never runs that loop against its
/// own room (it has no Room:HostUri pointed at itself).
///
/// This class is that missing bridge, registered as IInboundPackObserver so
/// RuntimeWebSocketLoopRunner invokes it after every genuinely-inbound pack on the server-side WS
/// path (never for this host's own outbound/rebroadcast traffic — see that interface's own doc
/// comment). By the time it fires, ImportPack + durable persistence have ALREADY happened (7.1's
/// own contract, unconditional) — this does sink-replay + cache-refresh only, exactly mirroring
/// the second half of RoomPeerClient.ApplyInboundPackForRoomCoreAsync (the client-role
/// equivalent for a host connecting outward), never re-importing anything itself.
///
/// No private-room ("studio") special-casing, by design: post-7.3, nothing should ever push an
/// inbound pack for "studio" through this server-side path at all — RoomPeerClient no longer
/// joins or pushes it upstream, and no other production client speaks this wire protocol against
/// it. The room name carries no meaning to this class either way; that decision already lives
/// entirely in RoomPeerClient's own membership/push logic, made BEFORE a pack ever reaches the
/// wire. If one somehow arrives anyway (a stray raw WebSocket client, or a pre-7.3 peer talking to
/// a since-upgraded server), the safest behavior is to still replay + refresh rather than skip:
/// the pack has already been imported and durably persisted into that room's engine state by the
/// WS loop by the time this observer runs, unconditionally on room identity (7.1's contract) —
/// refusing to replay would just leave that room's live maps/caches silently inconsistent with
/// its own already-persisted sync graph, a strictly worse outcome than a harmless replay. Adding a
/// skip here would also mean hardcoding the very "studio" literal 7.3 already treats as the one
/// thing to stop routing upstream a second time, in a second place, for no safety benefit.
/// </summary>
public sealed class StudioInboundPackObserver(
    IServiceProvider services,
    ILogger<StudioInboundPackObserver> logger) : IInboundPackObserver
{
    // Same lazy-optional-collaborator shape RoomPeerClient uses for these same two interfaces
    // (see that class's own comment) — this class is constructed unconditionally once registered
    // (RuntimeWebSocketLoopRunner resolves IEnumerable<IInboundPackObserver> at startup on every
    // host), so deferring resolution avoids any assumption about DI registration order.
    private IStudioNodeStoreReplicationSink? _replicationSink;
    private IStudioNodeStoreReplicationSink? ReplicationSink =>
        _replicationSink ??= services.GetService<IStudioNodeStoreReplicationSink>();

    private IStudioCacheRefreshCoordinator? _cacheRefreshCoordinator;
    private IStudioCacheRefreshCoordinator? CacheRefreshCoordinator =>
        _cacheRefreshCoordinator ??= services.GetService<IStudioCacheRefreshCoordinator>();

    public async ValueTask OnInboundPackAppliedAsync(string roomId, string nodesB64, CancellationToken cancellationToken = default)
    {
        // nodesB64 itself is unused here: RuntimeWebSocketLoopRunner has already imported it into
        // the engine's sync graph and durably persisted it by the time this fires (7.1's
        // contract) — this class only needs to know WHICH room to replay/refresh, not the pack
        // bytes themselves. Each step below is independently try/caught (mirroring
        // RoomPeerClient.ApplyInboundPackForRoomCoreAsync's own granularity) so a failure in one
        // never masks or blocks the other; RuntimeWebSocketLoopRunner also wraps every observer
        // call in its own try/catch (belt-and-suspenders, not relied on here).
        var replicationSink = ReplicationSink;
        if (replicationSink is not null)
        {
            try
            {
                await replicationSink.RehydrateLiveMapFromCanonicalResolutionAsync(roomId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[StudioInboundPackObserver] Live-map replay after server-role inbound pack failed room={Room}", roomId);
            }
        }
        else
        {
            logger.LogDebug(
                "[StudioInboundPackObserver] No IStudioNodeStoreReplicationSink registered — server-role inbound pack applied to the sync graph only room={Room}", roomId);
        }

        var cacheRefreshCoordinator = CacheRefreshCoordinator;
        if (cacheRefreshCoordinator is not null)
        {
            try
            {
                await cacheRefreshCoordinator.RefreshAfterInboundPackAsync(roomId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[StudioInboundPackObserver] In-memory cache refresh after server-role inbound pack failed room={Room}", roomId);
            }
        }
    }
}
