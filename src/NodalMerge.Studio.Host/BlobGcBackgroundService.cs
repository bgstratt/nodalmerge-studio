using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Host;

// plans/cas-distribution-and-storage.md Phase 5 slice 5.2 — recurring local blob GC. Off by
// default (BlobGcOptions.GcIntervalMinutes = 0, checked once at startup — the loop exits
// immediately rather than ticking forever against a disabled config, so ExecuteAsync returning
// early here is deliberate, not a bug). When enabled, runs IBlobGcService.RunAsync on its own
// owned loop honoring the configured BlobGcMode (never an override — an explicit mode override is
// the operator-triggered POST /studio/cache/gc's job, not the unattended scheduled run's).
//
// Deliberately a BackgroundService (not a hand-rolled Task.Run + CancellationTokenSource pair):
// BackgroundService.StopAsync already awaits the running ExecuteAsync task up to the host's
// shutdown timeout, which is exactly the "owned loop, clean stop" discipline this repo's flake
// history (nodalmerge_studio_integration_test_flake_fix — SqliteConnection pool races and
// unstopped fire-and-forget orchestrator loops) says background loops must have. PeriodicTimer
// (rather than a Task.Delay-in-a-while-loop) ties every wait directly to stoppingToken so
// cancellation during a delay unwinds promptly instead of finishing the wait first.
internal sealed class BlobGcBackgroundService(
    IBlobGcService gc,
    BlobGcOptions gcOptions,
    ILogger<BlobGcBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (gcOptions.GcIntervalMinutes <= 0)
            return; // disabled by config — the default

        var interval = TimeSpan.FromMinutes(gcOptions.GcIntervalMinutes);
        using var timer = new PeriodicTimer(interval);

        // First tick fires after one full interval, not immediately — a freshly-started host
        // shouldn't race its own startup sync/import work with a GC pass over data still being
        // written.
        while (await WaitForNextTickAsync(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                var record = await gc.RunAsync(modeOverride: null, stoppingToken).ConfigureAwait(false);
                logger.LogInformation(
                    "Scheduled blob GC run {RunId} complete: mode={Mode} scanned={Scanned} marked={Marked} deleted={Deleted}",
                    record.RunId, record.Mode, record.Scanned, record.Marked, record.Deleted);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Never crash the host over a failed scheduled sweep — same stance as
                // CasReconcileBackgroundService's startup pass. A manual POST /studio/cache/gc
                // stays available meanwhile, and both success and failure are already recorded in
                // the run ledger by IBlobGcService itself (fail-closed aborts still write a
                // Success=false row before rethrowing).
                logger.LogWarning(ex, "Scheduled blob GC run failed.");
            }
        }
    }

    // Small indirection so a host shutdown mid-wait (OperationCanceledException from the timer
    // itself, not from a run in flight) exits the loop cleanly instead of surfacing as an
    // unhandled-exception log from ExecuteAsync — BackgroundService already treats a cancelled
    // ExecuteAsync as normal shutdown, but this keeps the loop body free of a try/catch that would
    // otherwise wrap the tick-wait too.
    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
