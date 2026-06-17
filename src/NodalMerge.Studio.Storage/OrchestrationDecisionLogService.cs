using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class OrchestrationDecisionLogService : IOrchestrationDecisionLogService, IRehydratable
{
    private readonly ConcurrentDictionary<string, OrchestrationEvent> _decisionsById = new();
    // workUnitId → ordered list of eventIds
    private readonly ConcurrentDictionary<string, List<string>> _byWorkUnit = new();
    private readonly Lock _indexLock = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IExecutionEventStream _events;

    public OrchestrationDecisionLogService(IStudioNodeStore nodeStore, IExecutionEventStream events)
    {
        _nodeStore = nodeStore;
        _events    = events;
    }

    public async Task<OrchestrationEvent> RecordAsync(
        string workUnitId,
        string orchestratorAgentId,
        PipelineStage inputStage,
        string inputProjectionSnapshot,
        OrchestrationAction action,
        IReadOnlyList<string> spawnedIds,
        string? reason,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        var ev = new OrchestrationEvent(
            EventId:                $"OE-{Guid.NewGuid():N}",
            WorkUnitId:              workUnitId,
            OrchestratorAgentId:     orchestratorAgentId,
            InputStage:              inputStage,
            InputProjectionSnapshot: inputProjectionSnapshot,
            Action:                  action,
            SpawnedIds:              spawnedIds,
            Reason:                  reason,
            OccurredAt:              DateTimeOffset.UtcNow);

        _decisionsById[ev.EventId] = ev;
        lock (_indexLock)
        {
            if (!_byWorkUnit.TryGetValue(workUnitId, out var list))
                _byWorkUnit[workUnitId] = list = [];
            list.Add(ev.EventId);
        }

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.OrchestrationEventV1,
            ev.EventId,
            JsonSerializer.Serialize(ev),
            ct).ConfigureAwait(false);

        // Mirror into the unified causal stream (10b.2) — this decision log is the queryable-by-
        // workUnitId local store; the stream entry gives it a place in the session-wide timeline.
        if (sessionId is not null)
        {
            await _events.AppendAsync(
                sessionId,
                workUnitId,
                ExecutionEventKind.OrchestrationDecision,
                new OrchestrationDecisionPayload(orchestratorAgentId, action, spawnedIds, reason),
                ct: ct).ConfigureAwait(false);
        }

        return ev;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.OrchestrationEventV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var ev = JsonSerializer.Deserialize<OrchestrationEvent>(payloadJson);
            if (ev is null || !_decisionsById.TryAdd(ev.EventId, ev))
                continue;

            lock (_indexLock)
            {
                if (!_byWorkUnit.TryGetValue(ev.WorkUnitId, out var list))
                    _byWorkUnit[ev.WorkUnitId] = list = [];
                list.Add(ev.EventId);
            }
        }
    }

    public Task<IReadOnlyList<OrchestrationEvent>> GetEventsAsync(string workUnitId, CancellationToken ct = default)
    {
        List<string> ids;
        lock (_indexLock)
            ids = _byWorkUnit.TryGetValue(workUnitId, out var list) ? [.. list] : [];

        var events = ids
            .Select(id => _decisionsById.TryGetValue(id, out var ev) ? ev : null)
            .Where(ev => ev is not null)
            .Cast<OrchestrationEvent>()
            .OrderBy(ev => ev.OccurredAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<OrchestrationEvent>>(events);
    }
}
