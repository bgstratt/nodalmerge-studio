using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 6.5 (plans/blob-cas-remediation.md) — <see cref="StudioInboundPackObserver"/>'s
/// off-loop resilience contract. nodalmerge's half of 6.5 moved observer invocation off the WS
/// receive loop, so this class must now: coalesce a burst of N packs into far fewer than N full
/// refreshes (the refresh reads the full store and ignores the pack bytes, so coalescing is
/// lossless — pre-6.5 a burst of N did N full replay+refresh rounds, inline on the receive
/// loop); bound a hung refresh with its OWN timeout even when the caller passes
/// <see cref="CancellationToken.None"/>; survive a mid-refresh cancellation without wedging its
/// per-room coalescing state (the next pack must start a fresh cycle); and stop promptly on
/// dispose. Also pins replay-before-refresh order within a cycle (mirroring
/// RoomPeerClient.ApplyInboundPackForRoomCoreAsync) and per-room independence.
///
/// Deliberately NOT [Trait("Category", "Integration")]: everything here runs against in-memory
/// fakes of the two collaborator interfaces — no native bridge, no SQLite — so it belongs in the
/// unit lane CI actually executes unconditionally (ci.yml's integration lane is gated on
/// NODALMERGE_NATIVE_AVAILABLE).
/// </summary>
public class StudioInboundPackObserverTests
{
    private const string Room = "repo/room-observer-tests";

    [Fact]
    public async Task Burst_of_packs_coalesces_into_at_most_two_refresh_cycles()
    {
        var sink = new ScriptableReplicationSink { BlockOnGate = true };
        var coordinator = new RecordingRefreshCoordinator();
        await using var observer = CreateObserver(sink, coordinator);

        // Pack 1 starts a refresh cycle that blocks inside the sink...
        var firstCall = observer.OnInboundPackAppliedAsync(Room, "cGFjaw==").AsTask();
        await sink.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // ...seven more packs arrive while it is in flight. Pre-6.5 each of these was one more
        // full replay+refresh round; post-6.5 they fold into ONE trailing cycle.
        var burst = Enumerable.Range(0, 7)
            .Select(_ => observer.OnInboundPackAppliedAsync(Room, "cGFjaw==").AsTask())
            .ToArray();

        sink.Gate.TrySetResult();
        await Task.WhenAll(burst.Prepend(firstCall)).WaitAsync(TimeSpan.FromSeconds(10));

        // Quiesce: the in-flight cycle plus its single trailing cycle.
        await WaitUntilAsync(() => coordinator.CompletedCount >= 2, TimeSpan.FromSeconds(5));
        await Task.Delay(250);

        Assert.Equal(2, sink.CallCount);
        Assert.Equal(2, coordinator.CompletedCount);
    }

    [Fact]
    public async Task Hung_refresh_is_bounded_by_the_observers_own_timeout_even_with_token_none()
    {
        var sink = new ScriptableReplicationSink { HangUntilCancelledOnce = true };
        var coordinator = new RecordingRefreshCoordinator();
        await using var observer = CreateObserver(
            sink,
            coordinator,
            new StudioInboundPackObserverOptions { RefreshTimeout = TimeSpan.FromMilliseconds(200) }
        );

        // The caller passes CancellationToken.None — pre-6.5 this call never returned (the sink
        // hang was awaited inline with the caller's token, and None never fires).
        await observer.OnInboundPackAppliedAsync(Room, "cGFjaw==", CancellationToken.None)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));

        // The observer's own RefreshTimeout cancelled the hung sink call.
        await sink.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Recovery: the cancelled cycle must not wedge the room's coalescing state — the next
        // pack starts a fresh cycle that completes normally (the sink only hung once).
        await observer.OnInboundPackAppliedAsync(Room, "cGFjaw==").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.CompletedWithLiveTokenCount >= 1, TimeSpan.FromSeconds(5));
        Assert.True(sink.CallCount >= 2);
    }

    [Fact]
    public async Task Dispose_cancels_an_inflight_refresh_and_completes_promptly()
    {
        var sink = new ScriptableReplicationSink { HangUntilCancelledAlways = true };
        var coordinator = new RecordingRefreshCoordinator();
        var observer = CreateObserver(sink, coordinator);

        _ = observer.OnInboundPackAppliedAsync(Room, "cGFjaw==");
        await sink.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Dispose must reach the in-flight sink call through the observer's own token and return
        // well inside its bound (default RefreshTimeout is 30s — this proves dispose does not
        // wait it out).
        await observer.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sink.CancellationObserved.Task.IsCompleted);

        // Post-dispose notifications are no-ops — no new cycle starts.
        var callsBefore = sink.CallCount;
        await observer.OnInboundPackAppliedAsync(Room, "cGFjaw==").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(250);
        Assert.Equal(callsBefore, sink.CallCount);
    }

    [Fact]
    public async Task Replay_precedes_cache_refresh_within_a_cycle()
    {
        // Order pin: mirrors RoomPeerClient.ApplyInboundPackForRoomCoreAsync — the live-map
        // replay lands before the IRehydratable sweep re-reads it, per cycle.
        var events = new List<string>();
        var sink = new ScriptableReplicationSink { Events = events };
        var coordinator = new RecordingRefreshCoordinator { Events = events };
        await using var observer = CreateObserver(sink, coordinator);

        await observer.OnInboundPackAppliedAsync(Room, "cGFjaw==").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await WaitUntilAsync(() => coordinator.CompletedCount >= 1, TimeSpan.FromSeconds(5));

        lock (events)
        {
            Assert.Equal(["replay", "refresh"], events.Take(2).ToArray());
        }
    }

    [Fact]
    public async Task Rooms_refresh_independently_a_blocked_room_does_not_stall_another()
    {
        var sink = new ScriptableReplicationSink { BlockOnGate = true, GateRoom = "repo/room-blocked" };
        var coordinator = new RecordingRefreshCoordinator();
        await using var observer = CreateObserver(sink, coordinator);

        await observer.OnInboundPackAppliedAsync("repo/room-blocked", "cGFjaw==").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));
        await sink.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await observer.OnInboundPackAppliedAsync("repo/room-free", "cGFjaw==").AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2));

        // The free room's full cycle completes while the blocked room is still stuck in its sink.
        await WaitUntilAsync(
            () => coordinator.CompletedRooms.Contains("repo/room-free"),
            TimeSpan.FromSeconds(5)
        );
        Assert.DoesNotContain("repo/room-blocked", coordinator.CompletedRooms);

        sink.Gate.TrySetResult();
    }

    private static StudioInboundPackObserver CreateObserver(
        IStudioNodeStoreReplicationSink sink,
        IStudioCacheRefreshCoordinator coordinator,
        StudioInboundPackObserverOptions? options = null)
    {
        return new StudioInboundPackObserver(
            new FakeServiceProvider(sink, coordinator),
            NullLogger<StudioInboundPackObserver>.Instance,
            options
        );
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException("condition not reached within the test bound");
            await Task.Delay(25);
        }
    }

    private sealed class FakeServiceProvider(
        IStudioNodeStoreReplicationSink sink,
        IStudioCacheRefreshCoordinator coordinator) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IStudioNodeStoreReplicationSink))
                return sink;
            if (serviceType == typeof(IStudioCacheRefreshCoordinator))
                return coordinator;
            return null;
        }
    }

    private sealed class ScriptableReplicationSink : IStudioNodeStoreReplicationSink
    {
        private int _callCount;
        private int _hangArmed = 1;

        public int CallCount => Volatile.Read(ref _callCount);
        public bool HangUntilCancelledOnce { get; init; }
        public bool HangUntilCancelledAlways { get; init; }
        /// <summary>When true, calls block on <see cref="Gate"/> until the test opens it.</summary>
        public bool BlockOnGate { get; init; }
        /// <summary>When set (with <see cref="BlockOnGate"/>), only this room's calls block.</summary>
        public string? GateRoom { get; init; }
        public List<string>? Events { get; init; }

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Gate { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RehydrateLiveMapFromCanonicalResolutionAsync(string roomId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            if (GateRoom is null || string.Equals(roomId, GateRoom, StringComparison.Ordinal))
                FirstCallStarted.TrySetResult();

            if (HangUntilCancelledAlways
                || (HangUntilCancelledOnce && Interlocked.Exchange(ref _hangArmed, 0) == 1))
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved.TrySetResult();
                    throw;
                }
            }

            if (BlockOnGate && (GateRoom is null || string.Equals(roomId, GateRoom, StringComparison.Ordinal)))
            {
                // Block until the test opens the gate; once opened, later cycles flow freely.
                await Gate.Task.WaitAsync(cancellationToken);
            }

            if (Events is not null)
            {
                lock (Events)
                {
                    Events.Add("replay");
                }
            }
        }
    }

    private sealed class RecordingRefreshCoordinator : IStudioCacheRefreshCoordinator
    {
        private int _completedCount;
        private int _completedWithLiveTokenCount;

        public int CompletedCount => Volatile.Read(ref _completedCount);
        public int CompletedWithLiveTokenCount => Volatile.Read(ref _completedWithLiveTokenCount);
        public List<string> CompletedRooms { get; } = [];
        public List<string>? Events { get; init; }

        public Task RefreshAfterInboundPackAsync(string roomId, CancellationToken cancellationToken = default)
        {
            // Mirror the real coordinator's gate behavior: a cancelled token stops the sweep.
            cancellationToken.ThrowIfCancellationRequested();

            Interlocked.Increment(ref _completedCount);
            Interlocked.Increment(ref _completedWithLiveTokenCount);
            lock (CompletedRooms)
            {
                CompletedRooms.Add(roomId);
            }

            if (Events is not null)
            {
                lock (Events)
                {
                    Events.Add("refresh");
                }
            }

            return Task.CompletedTask;
        }
    }
}
