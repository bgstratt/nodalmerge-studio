using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 12 — replaces the old hard, static FileScope write-time block with a real, enforced
// exclusive lease per file path, merge-gated: a holder keeps its lease until its MergeProposal
// touching that path is actually applied, not merely proposed. Modeled on IntentGraphService's
// storage/rehydrate shape (not an extension of it — IntentGraphService's advisory "record and
// query overlaps" semantics don't fit an exclusive-grant-with-FIFO-queue model). Compare-and-swap
// via ConcurrentDictionary.TryUpdate, same idiom WorkSchedulerService already uses for its own
// per-key state transitions, rather than a manual lock around an async write.
public sealed class FileLeaseService : IFileLeaseService, IRehydratable
{
    private readonly ConcurrentDictionary<string, FileLeaseState> _leases = new();
    private readonly IStudioNodeStore _nodeStore;

    public FileLeaseService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<(bool Granted, string? HolderWorkUnitId)> TryAcquireOrEnqueueAsync(
        string workUnitId, string path, CancellationToken ct = default)
    {
        var key = Normalize(path);

        while (true)
        {
            if (!_leases.TryGetValue(key, out var current))
            {
                var granted = new FileLeaseState(key, workUnitId, []);
                if (!_leases.TryAdd(key, granted))
                    continue; // someone else just inserted — retry against the new state

                await PersistAsync(granted, ct).ConfigureAwait(false);
                return (true, workUnitId);
            }

            if (current.HolderWorkUnitId == workUnitId)
                return (true, workUnitId); // already the holder — idempotent

            if (current.WaitQueue.Contains(workUnitId))
                return (false, current.HolderWorkUnitId); // already queued — idempotent

            var queued = current with { WaitQueue = [.. current.WaitQueue, workUnitId] };
            if (!_leases.TryUpdate(key, queued, current))
                continue; // lost the race — retry against the latest state

            await PersistAsync(queued, ct).ConfigureAwait(false);
            return (false, current.HolderWorkUnitId);
        }
    }

    public async Task<string?> ReleaseAndAdvanceAsync(string path, CancellationToken ct = default)
    {
        var key = Normalize(path);

        while (true)
        {
            if (!_leases.TryGetValue(key, out var current))
                return null;

            if (current.WaitQueue.Count == 0)
            {
                if (!_leases.TryRemove(key, out _))
                    continue;

                await _nodeStore.WriteNodeAsync(StudioNodeKind.FileLeaseV1, key, "{\"removed\":true}", ct)
                    .ConfigureAwait(false);
                return null;
            }

            var nextHolder = current.WaitQueue[0];
            var advanced = current with { HolderWorkUnitId = nextHolder, WaitQueue = [.. current.WaitQueue.Skip(1)] };
            if (!_leases.TryUpdate(key, advanced, current))
                continue;

            await PersistAsync(advanced, ct).ConfigureAwait(false);
            return nextHolder;
        }
    }

    public async Task<IReadOnlyList<string>> ForceReleaseAllForWorkUnitAsync(string workUnitId, CancellationToken ct = default)
    {
        // A failed/dead-lettered/manually-stopped holder forfeits every lease it held (advancing
        // each queue, with no content to forward since nothing was ever merged), and is dropped
        // from any queue it was waiting in — it will never resume to claim that spot.
        var promoted = new List<string>();
        foreach (var key in _leases.Keys.ToList())
        {
            while (_leases.TryGetValue(key, out var current))
            {
                if (current.HolderWorkUnitId == workUnitId)
                {
                    var nextHolder = await ReleaseAndAdvanceAsync(key, ct).ConfigureAwait(false);
                    if (nextHolder is not null)
                        promoted.Add(nextHolder);
                    break;
                }

                if (!current.WaitQueue.Contains(workUnitId))
                    break;

                var purged = current with { WaitQueue = [.. current.WaitQueue.Where(w => w != workUnitId)] };
                if (!_leases.TryUpdate(key, purged, current))
                    continue;

                await PersistAsync(purged, ct).ConfigureAwait(false);
                break;
            }
        }

        return promoted;
    }

    public Task<IReadOnlyList<FileLeaseInfo>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<FileLeaseInfo> snapshot = _leases.Values
            .Select(s => new FileLeaseInfo(s.Path, s.HolderWorkUnitId, s.WaitQueue))
            .ToList();
        return Task.FromResult(snapshot);
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.FileLeaseV1, ct).ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var state = JsonSerializer.Deserialize<FileLeaseState>(payloadJson);
            // ReleaseAndAdvanceAsync's empty-queue path overwrites a cleared lease with a
            // {"removed":true} tombstone — deserializes to a state with a null Path, the signal
            // to skip rather than resurrect it here.
            if (state?.Path is null)
                continue;

            _leases.TryAdd(entityId, state);
        }
    }

    private Task PersistAsync(FileLeaseState state, CancellationToken ct) =>
        _nodeStore.WriteNodeAsync(StudioNodeKind.FileLeaseV1, state.Path, JsonSerializer.Serialize(state), ct);

    private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();

    private sealed record FileLeaseState(string Path, string? HolderWorkUnitId, IReadOnlyList<string> WaitQueue);
}
