using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

// Periodic background promoter: every 30 s it promotes the studio canonical checkpoint to the
// CRDT sync graph and broadcasts the resulting server pack to any live WebSocket peers. Uses
// IServiceProvider instead of directly injecting IStudioGraphPromoter so that the underlying
// FFI bridge (and its native DLL) is not touched until the first tick — this prevents eager
// DLL loading in test environments that never let the timer fire.
internal sealed class StudioCrdtSyncBackgroundService(
    IServiceProvider services,
    ILogger<StudioCrdtSyncBackgroundService> logger
) : BackgroundService
{
    private static readonly TimeSpan PromotionInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(PromotionInterval, stoppingToken).ConfigureAwait(false);
                var promoter = services.GetService(typeof(IStudioGraphPromoter)) as IStudioGraphPromoter;
                if (promoter is not null)
                {
                    await promoter.TryPromoteStudioCheckpointAsync().ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "studio crdt periodic sync error");
            }
        }
    }
}
