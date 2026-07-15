using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// plans/cas-distribution-and-storage.md Phase 2 slice 2.4 — the prefetch half of scoped fetch.
// MaterializationEngine already bounds *how many* blobs a checkout touches (FileScope-pruned
// resolve, ISnapshotTreeResolver); this bounds *when* they land in the local cache: pulling a work
// unit's declared FileScope in ahead of the materialize call that actually needs it, so a peer that
// drops offline mid-goal already has what it needs (harness plan's "bound offline exposure" stance,
// docs/delegated-storage-gc.md's grace-window reasoning applies the same way here).
//
// Deliberately best-effort: a miss for one hash (remote origin down, blob never pushed) doesn't
// fail the whole prefetch — this is an optimization, not a correctness requirement. Materialize-time
// resolution is still the source of truth for what a checkout actually needs.
internal sealed class WorkUnitPrefetchService(
    IRepositorySnapshotService snapshots,
    ISnapshotTreeResolver treeResolver,
    IBlobStoreProvider? blobStore) : IBlobPrefetchService
{
    // Matches MaterializationEngine's WorkspaceOptions.MaterializerConcurrency default — prefetch
    // is the same kind of bulk CAS-fetch workload, just triggered ahead of need instead of at
    // checkout time, so the same bound is a reasonable default rather than a new knob to wire.
    private const int Concurrency = 4;

    public async Task<int> PrefetchScopeAsync(
        string repositoryId, IReadOnlyList<string>? fileScope, CancellationToken ct = default)
    {
        if (fileScope is null || fileScope.Count == 0 || blobStore is null) return 0;

        var snapshot = await snapshots.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);
        if (snapshot is null) return 0;

        var entries = await treeResolver.ResolveTreeAsync(snapshot, fileScope, ct).ConfigureAwait(false);
        if (entries is null || entries.Count == 0) return 0;

        var semaphore = new SemaphoreSlim(Concurrency, Concurrency);
        var fetched = 0;
        var tasks = new List<Task>(entries.Count);

        foreach (var blobId in entries.Values)
        {
            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    // Through a chained provider, a Found result here means the bytes are now
                    // warm in the local cache (write-through-on-fetch) — the bytes themselves are
                    // of no interest to a prefetch, only the side effect is.
                    var result = await blobStore.TryGetBlobAsync(blobId, ct).ConfigureAwait(false);
                    if (result.Found) Interlocked.Increment(ref fetched);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return fetched;
    }
}
