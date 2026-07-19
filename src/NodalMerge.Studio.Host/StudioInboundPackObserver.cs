using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

/// <summary>
/// Tuning knobs for <see cref="StudioInboundPackObserver"/> — slice 6.5
/// (plans/blob-cas-remediation.md). Optional: DI never needs to register this (the observer's
/// constructor defaults it), it exists so tests can shrink the timeout below wall-clock scale.
/// </summary>
public sealed class StudioInboundPackObserverOptions
{
    /// <summary>
    /// Budget for ONE refresh cycle (live-map replay + IRehydratable sweep for one room). The
    /// observer owns this bound itself rather than trusting whatever token the caller passes —
    /// post-6.5 the nodalmerge runner invokes observers off the receive loop with its own
    /// timeout, but this class must stay safe even under a caller that passes
    /// <see cref="CancellationToken.None"/> (the pre-6.5 wedge, one layer down: a hung store
    /// read must never pin a refresh loop open forever).
    /// </summary>
    public TimeSpan RefreshTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

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
/// Slice 6.5 (plans/blob-cas-remediation.md) reshaped HOW that work runs, without changing WHAT
/// it does. nodalmerge's half of 6.5 moved observer invocation off the WS receive loop onto a
/// per-connection worker, which means this class is now called from an arbitrary background
/// context, sequentially per connection but potentially concurrently across connections, and its
/// caller's token is a dispatcher timeout rather than the connection's lifetime. Accordingly:
///
/// - <b>Coalescing.</b> A refresh cycle here reads the FULL persisted store, not the pack — the
///   pack bytes are ignored entirely (see OnInboundPackAppliedAsync), the sink replay is
///   "namespace-inferring and idempotent … safe to re-run at any time" (its own contract in
///   ServiceContracts.cs) and the IRehydratable sweep re-pulls each service's whole cache the
///   same way. So N packs for a room need at most the refresh running when the burst started
///   plus ONE trailing refresh started after the last pack arrived — coalescing loses nothing,
///   because the final cycle observes everything every earlier pack persisted. Implemented as a
///   per-room dirty/running flag pair: notifications mark the room dirty and return immediately;
///   one background loop per room drains the dirty flag, re-running until no pack arrived
///   mid-cycle.
/// - <b>Own cancellation/timeout.</b> Each cycle runs under this class's own linked token
///   (dispose + <see cref="StudioInboundPackObserverOptions.RefreshTimeout"/>), never the
///   caller's — a cancelled/closed WS connection must not yank a half-done refresh, and a hung
///   store read must not pin the loop open forever. A cancelled cycle is safe to abandon
///   mid-flight: both callees are idempotent full re-reads (nothing partial is ever "committed"
///   that the next cycle wouldn't overwrite), the coordinator releases its gate in a finally, and
///   the loop's running flag is likewise cleared in a finally — the next pack simply starts a
///   fresh cycle.
/// - <b>Idempotent + reentrant.</b> Concurrent calls (multiple WS connections into the same or
///   different rooms) fold into the same per-room flags; rooms never block each other.
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
    ILogger<StudioInboundPackObserver> logger,
    StudioInboundPackObserverOptions? options = null) : IInboundPackObserver, IAsyncDisposable
{
    private readonly StudioInboundPackObserverOptions _options = options ?? new StudioInboundPackObserverOptions();

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

    private sealed class RoomRefreshState
    {
        // 1 = at least one pack arrived since the current (or last) cycle began reading.
        public int Dirty;
        // 1 = a refresh loop is active for this room. Cleared in the loop's finally; the
        // lost-wakeup re-check there is what makes clearing + a concurrent Dirty set safe.
        public int Running;
        // The active loop task, tracked so DisposeAsync can await an orderly stop.
        public volatile Task? LoopTask;
    }

    private readonly ConcurrentDictionary<string, RoomRefreshState> _rooms = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _disposeCts = new();

    public ValueTask OnInboundPackAppliedAsync(string roomId, string nodesB64, CancellationToken cancellationToken = default)
    {
        // nodesB64 itself is unused here: RuntimeWebSocketLoopRunner has already imported it into
        // the engine's sync graph and durably persisted it by the time this fires (7.1's
        // contract) — this class only needs to know WHICH room to replay/refresh, not the pack
        // bytes themselves. That is also exactly why marking dirty (instead of queueing the pack)
        // is lossless: the trailing refresh cycle reads the full store, which already contains
        // every pack that set the flag.
        //
        // The caller's token is deliberately not threaded into the refresh work — see the class
        // comment ("Own cancellation/timeout"). Returning before the refresh completes is the
        // point: the runner's dispatch worker must never sit behind a full store re-read per pack.
        if (_disposeCts.IsCancellationRequested)
        {
            return ValueTask.CompletedTask;
        }

        var state = _rooms.GetOrAdd(roomId, static _ => new RoomRefreshState());
        Interlocked.Exchange(ref state.Dirty, 1);
        TryStartRefreshLoop(roomId, state);
        return ValueTask.CompletedTask;
    }

    private void TryStartRefreshLoop(string roomId, RoomRefreshState state)
    {
        if (Interlocked.CompareExchange(ref state.Running, 1, 0) != 0)
        {
            // A loop is already active; it will observe the Dirty flag (either in its while
            // condition or in its finally's lost-wakeup re-check) and run the trailing cycle.
            return;
        }

        state.LoopTask = Task.Run(() => RunRefreshLoopAsync(roomId, state));
    }

    private async Task RunRefreshLoopAsync(string roomId, RoomRefreshState state)
    {
        try
        {
            // Drain-until-clean: clearing Dirty BEFORE the cycle means a pack landing mid-cycle
            // re-marks it and earns exactly one trailing cycle — the coalescing contract.
            while (!_disposeCts.IsCancellationRequested
                   && Interlocked.Exchange(ref state.Dirty, 0) == 1)
            {
                using var cycleCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);
                cycleCts.CancelAfter(_options.RefreshTimeout);
                await RefreshRoomOnceAsync(roomId, cycleCts.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref state.Running, 0);
            // Lost-wakeup guard: a notification may have set Dirty after our while saw it clear
            // but before Running was released above — that caller's TryStartRefreshLoop saw
            // Running==1 and did nothing, so it is on us to re-arm.
            if (Volatile.Read(ref state.Dirty) == 1 && !_disposeCts.IsCancellationRequested)
            {
                TryStartRefreshLoop(roomId, state);
            }
        }
    }

    private async Task RefreshRoomOnceAsync(string roomId, CancellationToken cancellationToken)
    {
        // Each step below is independently try/caught (mirroring
        // RoomPeerClient.ApplyInboundPackForRoomCoreAsync's own granularity) so a failure in one
        // never masks or blocks the other. Cancellation (dispose or RefreshTimeout) is logged at
        // a lower severity than a genuine failure: an abandoned cycle costs only freshness, and
        // only until the next pack.
        var replicationSink = ReplicationSink;
        if (replicationSink is not null)
        {
            try
            {
                await replicationSink.RehydrateLiveMapFromCanonicalResolutionAsync(roomId, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "[StudioInboundPackObserver] Live-map replay cancelled (refresh timeout or dispose) room={Room} — the next inbound pack re-runs it", roomId);
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
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    "[StudioInboundPackObserver] In-memory cache refresh cancelled (refresh timeout or dispose) room={Room} — the next inbound pack re-runs it", roomId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[StudioInboundPackObserver] In-memory cache refresh after server-role inbound pack failed room={Room}", roomId);
            }
        }
    }

    /// <summary>
    /// Stops accepting new work and cancels any in-flight refresh cycles, then waits (bounded)
    /// for the per-room loops to acknowledge. Registered as a singleton, so the host's container
    /// disposal calls this on shutdown.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!_disposeCts.IsCancellationRequested)
        {
            _disposeCts.Cancel();
        }

        var loops = _rooms.Values
            .Select(state => state.LoopTask)
            .Where(task => task is not null)
            .Select(task => task!)
            .ToArray();
        if (loops.Length > 0)
        {
            try
            {
                // Every await in a loop observes _disposeCts, so this bound is generous; it
                // exists only so shutdown can never hang behind a bug here.
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                logger.LogWarning("[StudioInboundPackObserver] Refresh loops did not stop within the dispose bound — abandoned");
            }
            catch (Exception)
            {
                // Loop bodies isolate their own failures; nothing propagating here should block
                // shutdown.
            }
        }

        _disposeCts.Dispose();
    }
}
