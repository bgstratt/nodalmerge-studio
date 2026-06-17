using Microsoft.Extensions.Hosting;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 0a — runs once at startup, before any other hosted service (registered first in
// AddNodalMergeStorage/AddInMemoryStorage, ahead of AddStudioAgentRuntime's scheduler poll
// loop), and rebuilds every IRehydratable service's in-memory dictionary from whatever was
// already durably written via IStudioNodeStore.WriteNodeAsync. Without this, a Host restart
// would lose every work unit / proposal / session / dead-letter / decision-log entry even
// though the underlying node store kept them.
public sealed class StudioStateRehydrationService(IEnumerable<IRehydratable> rehydratables) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var rehydratable in rehydratables)
        {
            await rehydratable.RehydrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
