using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
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
    private readonly IServiceProvider? _serviceProvider;
    private readonly ILogger<FileLeaseService>? _logger;

    // IWorkUnitService is resolved lazily via IServiceProvider (not constructor-injected) for the
    // same reason WorkSchedulerService does it — its implementation depends on
    // IAgentControlService, which depends on IWorkScheduler, which depends on this service's own
    // project; a direct dependency here would risk a circular constructor graph. Optional overall
    // (defaults null) so the many direct `new FileLeaseService(nodeStore)` test constructions
    // don't all need updating — when unset, deadlock resolution still releases the victim's
    // leases (the part that actually matters), it just skips recording the dependsOn edge.
    public FileLeaseService(
        IStudioNodeStore nodeStore,
        IServiceProvider? serviceProvider = null,
        ILogger<FileLeaseService>? logger = null)
    {
        _nodeStore = nodeStore;
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task<(bool Granted, string? HolderWorkUnitId)> TryAcquireOrEnqueueAsync(
        string workUnitId, string path, CancellationToken ct = default)
    {
        var scopeId = await ResolveScopeIdAsync(workUnitId, ct).ConfigureAwait(false);
        var normalized = Normalize(path);
        var key = ComposeKey(scopeId, normalized);

        while (true)
        {
            if (!_leases.TryGetValue(key, out var current))
            {
                var granted = new FileLeaseState(scopeId, normalized, workUnitId, []);
                if (!_leases.TryAdd(key, granted))
                    continue; // someone else just inserted — retry against the new state

                await PersistAsync(key, granted, ct).ConfigureAwait(false);
                return (true, workUnitId);
            }

            if (current.HolderWorkUnitId == workUnitId)
                return (true, workUnitId); // already the holder — idempotent

            if (current.WaitQueue.Contains(workUnitId))
                return (false, current.HolderWorkUnitId); // already queued — idempotent

            // Deadlock check — the only moment a cycle can newly form is right here, the instant
            // before a new wait edge (workUnitId waits for current.HolderWorkUnitId) is added. If
            // the holder can already reach workUnitId by walking existing "X waits for Y" edges
            // (built from every other file's WaitQueue right now), adding this edge would close a
            // cycle: two tasks each waiting on a file the other holds, forever. Resolve it here,
            // synchronously, in the same call that would otherwise hang — no separate background
            // detector needed, since this is the only place a new cycle-closing edge is created.
            if (current.HolderWorkUnitId is { } holderId
                && FindWaitCycle(workUnitId, holderId) is { } cycle)
            {
                await ResolveDeadlockAsync(cycle, ct).ConfigureAwait(false);
                continue; // retry from the top against the post-resolution state
            }

            var queued = current with { WaitQueue = [.. current.WaitQueue, workUnitId] };
            if (!_leases.TryUpdate(key, queued, current))
                continue; // lost the race — retry against the latest state

            await PersistAsync(key, queued, ct).ConfigureAwait(false);
            return (false, current.HolderWorkUnitId);
        }
    }

    // Two work units only contend for the same lease if they share a root goal — an unrelated
    // goal must never block this one just because it happens to touch the same relative file path
    // in the one shared repository (that's what a user running two independent, uncoordinated
    // goals against it owns via the merge/reconciliation flow, not something the scheduler should
    // proactively serialize). Falls back to a single global scope (the pre-scoping behavior) when
    // IWorkUnitService isn't wired — only the direct `new FileLeaseService(nodeStore)` test
    // constructions that don't exercise cross-goal isolation hit that path.
    private async Task<string> ResolveScopeIdAsync(string workUnitId, CancellationToken ct)
    {
        var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
        if (workUnits is null)
            return string.Empty;

        var current = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        if (current is null)
            return string.Empty;

        var root = current;
        while (root.ParentWorkUnitId is { } parentId)
        {
            var parent = await workUnits.GetAsync(parentId, ct).ConfigureAwait(false);
            if (parent is null) break;
            root = parent;
        }

        return root.WorkUnitId;
    }

    private static string ComposeKey(string scopeId, string normalizedPath) => $"{scopeId}::{normalizedPath}";

    // BFS from holderId along existing "waiter waits for holder" edges (one per WaitQueue entry
    // across every current lease). If workUnitId is reachable, returns the path holderId -> ... ->
    // workUnitId — the full cycle that would close once the caller adds the workUnitId -> holderId
    // edge. Null if no such path exists (the common, non-deadlock case).
    private List<string>? FindWaitCycle(string workUnitId, string holderId)
    {
        var waitsFor = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var state in _leases.Values)
        {
            if (state.HolderWorkUnitId is null)
                continue;
            foreach (var waiter in state.WaitQueue)
            {
                if (!waitsFor.TryGetValue(waiter, out var holders))
                    waitsFor[waiter] = holders = [];
                holders.Add(state.HolderWorkUnitId);
            }
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { holderId };
        var queue = new Queue<List<string>>();
        queue.Enqueue([holderId]);

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var last = path[^1];

            if (last == workUnitId)
                return path;

            if (!waitsFor.TryGetValue(last, out var next))
                continue;

            foreach (var candidate in next)
            {
                if (visited.Add(candidate))
                    queue.Enqueue([.. path, candidate]);
            }
        }

        return null;
    }

    // Picks whichever cycle member currently holds the fewest leases as the victim (tie-broken by
    // WorkUnitId for determinism — this must be a pure function of shared state, not e.g. which
    // caller happened to trigger the check, or two racing callers could pick different victims
    // and thrash) — the cheapest one to unwind, since it has the least exclusively claimed. Force-
    // releases its leases (same mechanism a dead-lettered or manually-stopped work unit already
    // uses), then best-effort records what just happened as a real dependsOn edge — same principle
    // as FanOutService.AutoSequenceOverlappingSiblingsAsync's plan-time version of this, applied
    // reactively — so a retry doesn't race the same pair again.
    private async Task ResolveDeadlockAsync(IReadOnlyList<string> cycleMembers, CancellationToken ct)
    {
        var heldCounts = cycleMembers.ToDictionary(
            id => id,
            id => _leases.Values.Count(s => s.HolderWorkUnitId == id),
            StringComparer.Ordinal);
        var victim = cycleMembers
            .OrderBy(id => heldCounts[id])
            .ThenBy(id => id, StringComparer.Ordinal)
            .First();

        _logger?.LogWarning(
            "[FileLease] Deadlock detected among {Members} — releasing {Victim}'s leases to break the cycle.",
            string.Join(" -> ", cycleMembers), victim);

        // Unlike this method's other five callers (cancel/dead-letter/stop/reject/reconcile-
        // supersede), the victim here is NOT terminal — it survives, with a dependsOn edge onto
        // the survivor added below, and is expected to run again once that clears. Two things
        // ForceReleaseAllForWorkUnitAsync does are easy to lose track of for a survivor, unlike
        // those other callers: (1) its own AwaitingFileLease flag, if the victim was also queued
        // as a waiter on some OTHER lease — that purge branch drops it from the WaitQueue with no
        // signal back, and nothing else will ever flip that flag off again (EnqueueAsync's
        // update-in-place preserves it as-is), permanently stranding the victim even after its new
        // dependency is satisfied; (2) any OTHER work unit promoted to holder as a result of
        // releasing the victim's own held leases — every other caller loops over the returned list
        // and clears each promoted waiter's flag, this one previously discarded it outright.
        var promoted = await ForceReleaseAllForWorkUnitAsync(victim, ct).ConfigureAwait(false);
        var scheduler = _serviceProvider?.GetService(typeof(IWorkScheduler)) as IWorkScheduler;
        if (scheduler is not null)
        {
            await scheduler.ClearAwaitingFileLeaseAsync(victim, ct).ConfigureAwait(false);
            foreach (var promotedWorkUnitId in promoted)
                await scheduler.ClearAwaitingFileLeaseAsync(promotedWorkUnitId, ct).ConfigureAwait(false);
        }

        var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
        if (workUnits is null)
            return;

        foreach (var survivor in cycleMembers.Where(id => id != victim))
        {
            if (await WouldCreateDependencyCycleAsync(workUnits, survivor, victim, ct).ConfigureAwait(false))
                continue;

            await workUnits.AddDependencyAsync(victim, survivor, ct).ConfigureAwait(false);
        }
    }

    // True if candidateDependencyId's own DependsOn chain already reaches targetWorkUnitId —
    // meaning "targetWorkUnitId depends on candidateDependencyId" would close a WorkUnit-graph
    // cycle. Mirrors FanOutService.WouldCreateCycleAsync exactly; duplicated rather than shared
    // since the two live in different projects and each is a small, self-contained BFS.
    private static async Task<bool> WouldCreateDependencyCycleAsync(
        IWorkUnitService workUnits, string candidateDependencyId, string targetWorkUnitId, CancellationToken ct)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        queue.Enqueue(candidateDependencyId);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current)) continue;
            if (current == targetWorkUnitId) return true;

            var unit = await workUnits.GetAsync(current, ct).ConfigureAwait(false);
            if (unit is null) continue;
            foreach (var dep in unit.DependsOn)
                queue.Enqueue(dep);
        }

        return false;
    }

    public async Task<string?> ReleaseAndAdvanceAsync(string workUnitId, string path, CancellationToken ct = default)
    {
        var scopeId = await ResolveScopeIdAsync(workUnitId, ct).ConfigureAwait(false);
        return await ReleaseAndAdvanceByKeyAsync(ComposeKey(scopeId, Normalize(path)), ct).ConfigureAwait(false);
    }

    private async Task<string?> ReleaseAndAdvanceByKeyAsync(string key, CancellationToken ct)
    {
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

            await PersistAsync(key, advanced, ct).ConfigureAwait(false);
            return nextHolder;
        }
    }

    public async Task<IReadOnlyList<string>> ForceReleaseAllForWorkUnitAsync(string workUnitId, CancellationToken ct = default)
    {
        // A failed/dead-lettered/manually-stopped holder forfeits every lease it held (advancing
        // each queue, with no content to forward since nothing was ever merged), and is dropped
        // from any queue it was waiting in — it will never resume to claim that spot. Scope-
        // agnostic by construction: a given workUnitId only ever appears in the scope its own
        // ResolveScopeIdAsync computed, so scanning every key's content (not just one scope's) is
        // safe and simpler than resolving the scope again just to narrow the scan.
        var promoted = new List<string>();
        foreach (var key in _leases.Keys.ToList())
        {
            while (_leases.TryGetValue(key, out var current))
            {
                if (current.HolderWorkUnitId == workUnitId)
                {
                    var nextHolder = await ReleaseAndAdvanceByKeyAsync(key, ct).ConfigureAwait(false);
                    if (nextHolder is not null)
                        promoted.Add(nextHolder);
                    break;
                }

                if (!current.WaitQueue.Contains(workUnitId))
                    break;

                var purged = current with { WaitQueue = [.. current.WaitQueue.Where(w => w != workUnitId)] };
                if (!_leases.TryUpdate(key, purged, current))
                    continue;

                await PersistAsync(key, purged, ct).ConfigureAwait(false);
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
            // ReleaseAndAdvanceByKeyAsync's empty-queue path overwrites a cleared lease with a
            // {"removed":true} tombstone — deserializes to a state with a null Path, the signal
            // to skip rather than resurrect it here.
            if (state?.Path is null)
                continue;

            _leases.TryAdd(entityId, state);
        }
    }

    private Task PersistAsync(string key, FileLeaseState state, CancellationToken ct) =>
        _nodeStore.WriteNodeAsync(StudioNodeKind.FileLeaseV1, key, JsonSerializer.Serialize(state), ct);

    private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();

    private sealed record FileLeaseState(string RootScopeId, string Path, string? HolderWorkUnitId, IReadOnlyList<string> WaitQueue);
}
