using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// plans/cas-distribution-and-storage.md Phase 2 slice 2.3 — see ICasReconcileService's own doc
// comment for the healing-path rationale. Lives in Storage (not Core) because it depends on
// IRemoteBlobPushTarget, which only NodalMerge.Host.Abstractions-referencing projects can see.
public sealed class CasReconcileService(
    IWorkspaceCacheManager cacheManager,
    IBlobStoreProvider blobStore,
    ILogger<CasReconcileService> logger,
    IRemoteBlobPushTarget? remote = null) : ICasReconcileService
{
    // Matches WorkUnitPrefetchService/MaterializationEngine's own bulk-CAS-fetch concurrency bound
    // — this is the same kind of bounded parallel CAS traffic, just pushing instead of fetching.
    private const int Concurrency = 4;

    public async Task<CasReconcileResult> ReconcileAsync(CancellationToken ct = default)
    {
        if (remote is null)
        {
            logger.LogDebug("Skipping CAS reconcile sweep — no remote blob push target is configured.");
            return new CasReconcileResult(0, 0, 0, 0, 0);
        }

        IReadOnlySet<string> live;
        try
        {
            live = await cacheManager.GetLiveBlobHashesAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // Same fail-closed contract GetLiveBlobHashesAsync documents for the GC sweep: an
            // incomplete live set must never drive a reconcile pass either (it would look like an
            // under-approximation of what the origin needs). Log here for the background-service
            // caller's benefit, then let it propagate — the REST endpoint caller (see
            // /studio/cas/reconcile) is responsible for catching and reporting, same convention as
            // /studio/cache/gc.
            logger.LogWarning(ex, "Skipping CAS reconcile sweep — the live blob set could not be computed.");
            throw;
        }

        var alreadyPresent = 0;
        var pushed = 0;
        var failed = 0;
        var missingLocally = 0;

        var semaphore = new SemaphoreSlim(Concurrency, Concurrency);
        var tasks = new List<Task>(live.Count);

        foreach (var hash in live)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    if (await remote.ExistsAsync(hash, ct).ConfigureAwait(false))
                    {
                        Interlocked.Increment(ref alreadyPresent);
                        return;
                    }

                    var local = await blobStore.TryGetBlobAsync(hash, ct).ConfigureAwait(false);
                    if (!local.Found)
                    {
                        Interlocked.Increment(ref missingLocally);
                        logger.LogWarning(
                            "CAS reconcile: blob {Hash} is in the live set but missing from both the remote origin and the local store.",
                            hash);
                        return;
                    }

                    try
                    {
                        await remote.PushAsync(hash, local.Bytes!, local.ContentType, ct).ConfigureAwait(false);
                        Interlocked.Increment(ref pushed);
                    }
                    catch (Exception ex)
                    {
                        // Best-effort sweep — one origin push failing (transient network error,
                        // outage) must not abort the rest of the round.
                        Interlocked.Increment(ref failed);
                        logger.LogWarning(ex, "CAS reconcile: failed to push blob {Hash} to the remote origin.", hash);
                    }
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        var result = new CasReconcileResult(live.Count, alreadyPresent, pushed, failed, missingLocally);
        logger.LogInformation(
            "CAS reconcile sweep complete: scanned={Scanned} alreadyPresent={AlreadyPresent} pushed={Pushed} failed={Failed} missingLocally={MissingLocally}",
            result.Scanned, result.AlreadyPresent, result.Pushed, result.Failed, result.MissingLocally);

        return result;
    }
}
