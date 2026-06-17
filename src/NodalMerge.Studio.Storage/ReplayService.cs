using System.Text.Json;
using System.Text.Json.Serialization;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 13f — replaces StubReplayService. Built entirely on existing query surfaces
// (IArtifactLineageService, IOrchestrationDecisionLogService, IKnownGoodStateService) and 13e's
// now-real snapshot/restore primitive; no new generic node-store range-scanner.
internal sealed class ReplayService(
    IWorkUnitService workUnits,
    IArtifactLineageService artifacts,
    IOrchestrationDecisionLogService decisionLog,
    IKnownGoodStateService knownGood) : IReplayService
{
    // camelCase to match the rest of the Studio Host's JSON surface (ConfigureHttpJsonOptions /
    // ASP.NET Core minimal-API defaults) — these results flow to the same MCP/agent consumers.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private sealed record TimelineEntry(string Kind, string NodeId, string Description, DateTimeOffset OccurredAt);

    public async Task<string> RollbackAsync(string branchId, string knownGoodStateId, CancellationToken cancellationToken = default)
    {
        var candidates = await knownGood.FindKnownGoodAsync(branchId, cancellationToken).ConfigureAwait(false);
        if (!candidates.Any(s => s.StateId == knownGoodStateId))
            return JsonSerializer.Serialize(
                new { error = $"Known-good state '{knownGoodStateId}' does not belong to branch '{branchId}'." },
                JsonOptions);

        var restored = await knownGood.CheckoutKnownGoodAsync(knownGoodStateId, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new { branchId, restored }, JsonOptions);
    }

    public async Task<string> RangeAsync(
        string branchId,
        string? fromNode = null,
        string? toNode = null,
        CancellationToken cancellationToken = default)
    {
        var entries = (await BuildTimelineAsync(branchId, cancellationToken).ConfigureAwait(false))
            .OrderBy(e => e.OccurredAt)
            .ToList();

        if (fromNode is not null)
        {
            var idx = entries.FindIndex(e => e.NodeId == fromNode);
            if (idx >= 0)
                entries = entries.Skip(idx).ToList();
        }

        if (toNode is not null)
        {
            var idx = entries.FindIndex(e => e.NodeId == toNode);
            if (idx >= 0)
                entries = entries.Take(idx + 1).ToList();
        }

        return JsonSerializer.Serialize(new { branchId, entries }, JsonOptions);
    }

    public async Task<string> InspectAsync(string branchId, string? nodeId = null, CancellationToken cancellationToken = default)
    {
        var owningWorkUnitIds = (await workUnits.ListAsync(branchId, cancellationToken).ConfigureAwait(false))
            .Select(w => w.WorkUnitId)
            .ToList();

        if (nodeId is null)
        {
            var states = await knownGood.FindKnownGoodAsync(branchId, cancellationToken).ConfigureAwait(false);
            var timeline = await BuildTimelineAsync(branchId, cancellationToken, owningWorkUnitIds).ConfigureAwait(false);
            var lastEntry = timeline.OrderByDescending(e => e.OccurredAt).FirstOrDefault();

            return JsonSerializer.Serialize(new
            {
                branchId,
                currentKnownGoodStateId = states.FirstOrDefault()?.StateId,
                artifactCount = timeline.Count(e => e.Kind == "artifact"),
                lastEvent = lastEntry,
            }, JsonOptions);
        }

        foreach (var workUnitId in owningWorkUnitIds)
        {
            var artifact = (await artifacts.GetChainAsync(workUnitId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(a => a.ArtifactId == nodeId);
            if (artifact is not null)
                return JsonSerializer.Serialize(new { kind = "artifact", artifact }, JsonOptions);

            var orchestrationEvent = (await decisionLog.GetEventsAsync(workUnitId, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(e => e.EventId == nodeId);
            if (orchestrationEvent is not null)
                return JsonSerializer.Serialize(new { kind = "orchestration-event", orchestrationEvent }, JsonOptions);
        }

        var knownGoodState = (await knownGood.FindKnownGoodAsync(branchId, cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(s => s.StateId == nodeId);
        if (knownGoodState is not null)
            return JsonSerializer.Serialize(new { kind = "known-good-state", knownGoodState }, JsonOptions);

        return JsonSerializer.Serialize(new { error = $"Node '{nodeId}' was not found on branch '{branchId}'." }, JsonOptions);
    }

    private async Task<List<TimelineEntry>> BuildTimelineAsync(
        string branchId, CancellationToken ct, IReadOnlyList<string>? owningWorkUnitIds = null)
    {
        owningWorkUnitIds ??= (await workUnits.ListAsync(branchId, ct).ConfigureAwait(false))
            .Select(w => w.WorkUnitId)
            .ToList();

        var entries = new List<TimelineEntry>();
        foreach (var workUnitId in owningWorkUnitIds)
        {
            foreach (var artifact in await artifacts.GetChainAsync(workUnitId, ct).ConfigureAwait(false))
                entries.Add(new TimelineEntry(
                    "artifact", artifact.ArtifactId,
                    $"{artifact.Type} ({artifact.Status})" + (artifact.Title is null ? "" : $": {artifact.Title}"),
                    artifact.CreatedAt));

            foreach (var ev in await decisionLog.GetEventsAsync(workUnitId, ct).ConfigureAwait(false))
                entries.Add(new TimelineEntry(
                    "orchestration-event", ev.EventId,
                    $"{ev.Action}" + (ev.Reason is null ? "" : $": {ev.Reason}"),
                    ev.OccurredAt));
        }

        return entries;
    }
}
