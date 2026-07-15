using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

// plans/cas-distribution-and-storage.md Phase 2 slice 2.3 — one-shot startup healing pass: waits a
// bit for the host to finish spinning up, then runs the CAS reconcile sweep once so any blobs
// written before a remote origin was configured (or during a prior outage) get pushed. Not a
// recurring timer (unlike StudioCrdtSyncBackgroundService's 30s promotion loop) — a fresh write
// already rides the local->remote chain (slice 2.2), so an ongoing periodic sweep isn't needed to
// stay caught up; only a one-time catch-up for pre-existing/outage-era gaps.
//
// Uses IServiceProvider instead of directly injecting IRemoteBlobPushTarget/ICasReconcileService
// so that neither is touched (and no chained-remote HTTP client/provider is constructed) unless a
// remote origin is actually configured — same reasoning as StudioCrdtSyncBackgroundService's own
// lazy IStudioGraphPromoter resolution.
internal sealed class CasReconcileBackgroundService(
    IServiceProvider services,
    ILogger<CasReconcileBackgroundService> logger
) : BackgroundService
{
    private static readonly TimeSpan BaseDelay = TimeSpan.FromSeconds(30);
    private static readonly Random Jitter = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Only act when a remote blob push target is actually configured — most Studio hosts
            // run with no remote origin at all (File/WsOnly providers), and this must be a true
            // no-op for them rather than resolving ICasReconcileService's other dependencies
            // (IBlobStoreProvider, IWorkspaceCacheManager) for no reason.
            var remote = services.GetService(typeof(IRemoteBlobPushTarget)) as IRemoteBlobPushTarget;
            if (remote is null) return;

            var jitterMs = Jitter.Next(0, 15_000);
            await Task.Delay(BaseDelay + TimeSpan.FromMilliseconds(jitterMs), stoppingToken).ConfigureAwait(false);

            var reconcile = services.GetRequiredService<ICasReconcileService>();
            var result = await reconcile.ReconcileAsync(stoppingToken).ConfigureAwait(false);
            logger.LogInformation(
                "CAS reconcile startup sweep complete: scanned={Scanned} alreadyPresent={AlreadyPresent} pushed={Pushed} failed={Failed} missingLocally={MissingLocally}",
                result.Scanned, result.AlreadyPresent, result.Pushed, result.Failed, result.MissingLocally);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutting down before the delay elapsed — nothing to do.
        }
        catch (Exception ex)
        {
            // Never crash the host over a failed healing pass — it can be retried on the next
            // restart, and a manual POST /studio/cas/reconcile is always available meanwhile.
            logger.LogWarning(ex, "CAS reconcile startup sweep failed.");
        }
    }
}
