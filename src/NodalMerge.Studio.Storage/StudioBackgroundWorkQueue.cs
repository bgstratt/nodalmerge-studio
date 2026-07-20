using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// plans/vision-punchlist-remediation.md (shutdown contract) — see IStudioBackgroundWork.
//
// Several operations deliberately defer durable follow-up work so the caller never pays its latency:
// the goal-final-snapshot stamp and the integration checkpoint after a merge apply, the studio
// checkpoint promotion when a work unit reaches a terminal status, and the orphaned-workspace sweep
// at startup. Each of those used `_ = Task.Run(..., CancellationToken.None)`, which kept the latency
// property but made shutdown non-deterministic: nothing could await the task and no token could
// cancel it, so a host could dispose — or a process exit — with a checkpoint half written. That is
// the very durability boundary the merge-apply checkpoint exists to establish.
//
// This queue keeps Enqueue non-blocking while making shutdown deterministic. The pump is awaited and
// drained both on IHostedService.StopAsync AND on container disposal — the latter matters because a
// host disposed without an explicit StopAsync (common in tests, and reachable in Studio Desktop's
// in-process restart) would otherwise never drain. A bounded drain timeout stops one wedged item
// from hanging shutdown forever.
public sealed class StudioBackgroundWorkQueue : IStudioBackgroundWork, IHostedService, IAsyncDisposable
{
    private readonly record struct WorkItem(string Name, Func<CancellationToken, Task> Work);

    private readonly Channel<WorkItem> _channel =
        Channel.CreateUnbounded<WorkItem>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _hardStop = new();
    private readonly ILogger<StudioBackgroundWorkQueue>? _logger;
    private readonly TimeSpan _drainTimeout;
    private readonly object _gate = new();

    private Task? _pump;
    private int _pending;
    private TaskCompletionSource _idle = CompletedIdle();
    private bool _shutdown;
    private bool _disposed;

    public StudioBackgroundWorkQueue(
        ILogger<StudioBackgroundWorkQueue>? logger = null,
        TimeSpan? drainTimeout = null)
    {
        _logger = logger;
        _drainTimeout = drainTimeout ?? TimeSpan.FromSeconds(30);
    }

    private static TaskCompletionSource CompletedIdle()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.SetResult();
        return tcs;
    }

    public void Enqueue(string name, Func<CancellationToken, Task> work)
    {
        ArgumentNullException.ThrowIfNull(work);

        lock (_gate)
        {
            // Once the drain has begun it has already decided what set of work it is waiting for;
            // accepting more would make shutdown unbounded. Dropping is safe for every current
            // caller — all of them are recoverable follow-ups (a skipped checkpoint just means the
            // next hydrate replays a longer delta chain), never the primary write.
            if (_shutdown)
            {
                _logger?.LogDebug("[BackgroundWork] Dropped '{Name}' — queue is draining.", name);
                return;
            }

            if (_pending++ == 0)
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // Started lazily rather than in StartAsync so the queue works in hosts whose hosted
            // services were never started (much of the test suite) — those still drain via
            // DisposeAsync.
            _pump ??= Task.Run(() => PumpAsync(_hardStop.Token), CancellationToken.None);
        }

        if (!_channel.Writer.TryWrite(new WorkItem(name, work)))
            CompleteOne();
    }

    public async Task<bool> WaitForDrainAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        Task idle;
        lock (_gate)
            idle = _idle.Task;

        try
        {
            await idle.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private async Task PumpAsync(CancellationToken hardStop)
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(hardStop).ConfigureAwait(false))
            {
                try
                {
                    await item.Work(hardStop).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (hardStop.IsCancellationRequested)
                {
                    _logger?.LogDebug("[BackgroundWork] '{Name}' cancelled by hard stop.", item.Name);
                }
                catch (Exception ex)
                {
                    // Observed and logged rather than rethrown. This is the same failure surface the
                    // old fire-and-forget calls had, except those surfaced as unobserved task
                    // exceptions on a finalizer thread with no context.
                    _logger?.LogWarning(ex, "[BackgroundWork] '{Name}' failed.", item.Name);
                }
                finally
                {
                    CompleteOne();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Hard stop during a read — remaining items are abandoned by design.
        }
    }

    private void CompleteOne()
    {
        lock (_gate)
        {
            if (--_pending == 0)
                _idle.TrySetResult();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => DrainAsync();

    public async ValueTask DisposeAsync()
    {
        await DrainAsync().ConfigureAwait(false);

        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }

        _hardStop.Dispose();
    }

    // Idempotent and safe to call from both StopAsync and DisposeAsync (the queue is registered
    // under several service types, and the generic host disposes each registration).
    private async Task DrainAsync()
    {
        Task? pump;
        lock (_gate)
        {
            _shutdown = true;
            _channel.Writer.TryComplete();
            pump = _pump;
        }

        if (pump is null)
            return;

        try
        {
            await pump.WaitAsync(_drainTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger?.LogWarning(
                "[BackgroundWork] Drain exceeded {Seconds}s — cancelling in-flight work. Some deferred " +
                "durable work may not have completed.",
                _drainTimeout.TotalSeconds);

            lock (_gate)
            {
                if (_disposed)
                    return;
            }

            try
            {
                await _hardStop.CancelAsync().ConfigureAwait(false);
                await pump.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[BackgroundWork] Pump faulted during hard stop.");
            }
        }
    }
}
