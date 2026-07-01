using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 14 — derived, on-demand usage metrics computed from the execution event log. Exists to let
// future Phase-12 (file-leasing) coordination decisions be evidence-driven rather than speculative;
// see plans/phase-14-usage-instrumentation-and-read-many.md. No persistence of its own — every query
// re-scans IExecutionEventStream.GetEventsByKindAsync, which is an in-memory O(n) filter today and
// fine for an admin/instrumentation read, not a hot path.
public sealed class WorkspaceUsageMetricsService(IExecutionEventStream events) : IWorkspaceUsageMetricsService
{
    public async Task<IReadOnlyList<FileHitCount>> GetTopFileHitsAsync(
        int topN = 20, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var hits = await events.GetEventsByKindAsync(
            [ExecutionEventKind.WorkspaceSearchExecuted, ExecutionEventKind.WorkspaceReadExecuted],
            since, ct).ConfigureAwait(false);

        var counts = new Dictionary<string, int>();
        foreach (var ev in hits)
        {
            IReadOnlyList<string>? paths = ev.Kind switch
            {
                ExecutionEventKind.WorkspaceSearchExecuted =>
                    JsonSerializer.Deserialize<WorkspaceSearchExecutedPayload>(ev.PayloadJson)?.MatchedPaths,
                ExecutionEventKind.WorkspaceReadExecuted =>
                    JsonSerializer.Deserialize<WorkspaceReadExecutedPayload>(ev.PayloadJson)?.Paths,
                _ => null,
            };

            if (paths is null) continue;
            foreach (var path in paths)
                counts[path] = counts.GetValueOrDefault(path) + 1;
        }

        return counts
            .OrderByDescending(kv => kv.Value)
            .Take(topN)
            .Select(kv => new FileHitCount(kv.Key, kv.Value))
            .ToList();
    }

    public async Task<IReadOnlyList<LeaseContentionHotSpot>> GetLeaseContentionHotSpotsAsync(
        int topN = 20, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var contended = await events.GetEventsByKindAsync(
            [ExecutionEventKind.FileLeaseContended], since, ct).ConfigureAwait(false);

        var byPath = new Dictionary<string, (int Count, HashSet<string> Workers)>();
        foreach (var ev in contended)
        {
            var payload = JsonSerializer.Deserialize<FileLeaseContendedPayload>(ev.PayloadJson);
            if (payload is null) continue;

            if (!byPath.TryGetValue(payload.Path, out var entry))
                entry = (0, []);

            entry.Workers.Add(payload.RequestingWorkUnitId);
            byPath[payload.Path] = (entry.Count + 1, entry.Workers);
        }

        return byPath
            .OrderByDescending(kv => kv.Value.Count)
            .Take(topN)
            .Select(kv => new LeaseContentionHotSpot(kv.Key, kv.Value.Count, kv.Value.Workers.ToList()))
            .ToList();
    }

    public async Task<SearchUsageSummary> GetSearchUsageAsync(
        string? workUnitId = null, DateTimeOffset? since = null, CancellationToken ct = default)
    {
        var searches = await events.GetEventsByKindAsync(
            [ExecutionEventKind.WorkspaceSearchExecuted], since, ct).ConfigureAwait(false);

        if (workUnitId is not null)
            searches = searches.Where(ev => ev.WorkUnitId == workUnitId).ToList();

        var searchCount = 0;
        var totalMatches = 0;
        var truncatedCount = 0;
        foreach (var ev in searches)
        {
            var payload = JsonSerializer.Deserialize<WorkspaceSearchExecutedPayload>(ev.PayloadJson);
            if (payload is null) continue;
            searchCount++;
            totalMatches += payload.MatchCount;
            if (payload.Truncated) truncatedCount++;
        }

        return new SearchUsageSummary(searchCount, totalMatches, truncatedCount);
    }
}
