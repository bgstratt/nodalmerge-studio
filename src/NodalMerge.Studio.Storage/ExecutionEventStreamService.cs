using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class ExecutionEventStreamService : IExecutionEventStream, IRehydratable
{
    private readonly ConcurrentDictionary<string, ExecutionEvent> _events = new();
    // sessionId → ordered list of eventIds
    private readonly ConcurrentDictionary<string, List<string>> _sessionIndex = new();
    private readonly Lock _indexLock = new();
    private readonly IStudioNodeStore _nodeStore;

    public ExecutionEventStreamService(IStudioNodeStore nodeStore) => _nodeStore = nodeStore;

    public async Task<ExecutionEvent> AppendAsync<T>(
        string sessionId,
        string? workUnitId,
        ExecutionEventKind kind,
        T payload,
        string? causedByEventId = null,
        string? eventId = null,
        CancellationToken ct = default)
    {
        var id = eventId ?? $"EVT-{Guid.NewGuid():N}";

        // Idempotency: if eventId already exists return the existing record.
        if (eventId is not null && _events.TryGetValue(id, out var existing))
            return existing;

        var ev = new ExecutionEvent(
            EventId:         id,
            SessionId:       sessionId,
            WorkUnitId:      workUnitId,
            Kind:            kind,
            PayloadJson:     JsonSerializer.Serialize(payload),
            CausedByEventId: causedByEventId,
            OccurredAt:      DateTimeOffset.UtcNow);

        _events[id] = ev;
        IndexEvent(sessionId, id);

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ExecutionEventV1,
            id,
            JsonSerializer.Serialize(ev),
            ct).ConfigureAwait(false);

        return ev;
    }

    public Task<IReadOnlyList<ExecutionEvent>> GetSessionEventsAsync(
        string sessionId,
        DateTimeOffset? since = null,
        CancellationToken ct = default)
    {
        List<string> ids;
        lock (_indexLock)
        {
            ids = _sessionIndex.TryGetValue(sessionId, out var list)
                ? [.. list]
                : [];
        }

        var events = ids
            .Select(id => _events.TryGetValue(id, out var ev) ? ev : null)
            .Where(ev => ev is not null)
            .Cast<ExecutionEvent>()
            .Where(ev => since is null || ev.OccurredAt > since.Value)
            .OrderBy(ev => ev.OccurredAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ExecutionEvent>>(events);
    }

    public Task<ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default)
    {
        _events.TryGetValue(eventId, out var ev);
        return Task.FromResult(ev);
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.ExecutionEventV1, ct).ConfigureAwait(false);

        // Sort by OccurredAt before indexing so _sessionIndex's per-session lists come back in
        // the same chronological order they'd have been appended in during the original run —
        // ReadAllNodesAsync makes no ordering guarantee.
        var events = records
            .Select(r => JsonSerializer.Deserialize<ExecutionEvent>(r.PayloadJson))
            .Where(ev => ev is not null)
            .Cast<ExecutionEvent>()
            .OrderBy(ev => ev.OccurredAt)
            .ToList();

        foreach (var ev in events)
        {
            if (_events.TryAdd(ev.EventId, ev))
                IndexEvent(ev.SessionId, ev.EventId);
        }
    }

    private void IndexEvent(string sessionId, string eventId)
    {
        lock (_indexLock)
        {
            if (!_sessionIndex.TryGetValue(sessionId, out var list))
            {
                list = [];
                _sessionIndex[sessionId] = list;
            }
            list.Add(eventId);
        }
    }
}
