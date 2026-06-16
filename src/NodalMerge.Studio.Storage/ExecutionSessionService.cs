using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class ExecutionSessionService : IExecutionSessionService
{
    private readonly ConcurrentDictionary<string, ExecutionSession> _sessions = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IExecutionEventStream _events;

    public ExecutionSessionService(IStudioNodeStore nodeStore, IExecutionEventStream events)
    {
        _nodeStore = nodeStore;
        _events    = events;
    }

    public async Task<ExecutionSession> CreateAsync(
        string rootWorkUnitId,
        string modelConfigJson,
        IReadOnlyList<string> profileIds,
        string? parentSessionId = null,
        string? parentEventId = null,
        CancellationToken ct = default)
    {
        var session = new ExecutionSession(
            SessionId: $"SES-{Guid.NewGuid():N}",
            RootWorkUnitId: rootWorkUnitId,
            Status: ExecutionSessionStatus.Active,
            ParentSessionId: parentSessionId,
            ParentEventId: parentEventId,
            StartedAt: DateTimeOffset.UtcNow,
            PausedAt: null,
            CompletedAt: null,
            ModelConfigSnapshotJson: string.IsNullOrEmpty(modelConfigJson) ? "{}" : modelConfigJson,
            ProfileIdSet: profileIds);

        _sessions[session.SessionId] = session;
        await Persist(session, ct).ConfigureAwait(false);

        await _events.AppendAsync(
            session.SessionId,
            workUnitId: null,
            ExecutionEventKind.SessionStarted,
            new SessionStartedPayload(session.SessionId, profileIds, session.ModelConfigSnapshotJson),
            ct: ct).ConfigureAwait(false);

        return session;
    }

    public Task<ExecutionSession?> GetAsync(string sessionId, CancellationToken ct = default)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return Task.FromResult(session);
    }

    public Task<IReadOnlyList<ExecutionSession>> ListAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ExecutionSession> result = _sessions.Values
            .OrderByDescending(s => s.StartedAt)
            .ToList();
        return Task.FromResult(result);
    }

    public async Task SetStatusAsync(string sessionId, ExecutionSessionStatus status, CancellationToken ct = default)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            throw new KeyNotFoundException($"Session '{sessionId}' was not found.");

        var updated = session with
        {
            Status = status,
            PausedAt = status == ExecutionSessionStatus.Paused ? DateTimeOffset.UtcNow : session.PausedAt,
            CompletedAt = status is ExecutionSessionStatus.Completed or ExecutionSessionStatus.Abandoned
                ? DateTimeOffset.UtcNow
                : session.CompletedAt,
        };
        _sessions[sessionId] = updated;
        await Persist(updated, ct).ConfigureAwait(false);

        var kind = status switch
        {
            ExecutionSessionStatus.Paused => ExecutionEventKind.SessionPaused,
            ExecutionSessionStatus.Active => ExecutionEventKind.SessionResumed,
            _ => (ExecutionEventKind?)null,
        };
        if (kind.HasValue)
        {
            object payload = kind.Value == ExecutionEventKind.SessionPaused
                ? new SessionPausedPayload(sessionId)
                : new SessionResumedPayload(sessionId);
            await _events.AppendAsync(sessionId, null, kind.Value, payload, ct: ct).ConfigureAwait(false);
        }
    }

    private Task Persist(ExecutionSession session, CancellationToken ct) =>
        _nodeStore.WriteNodeAsync(
            StudioNodeKind.ExecutionSessionV1,
            session.SessionId,
            JsonSerializer.Serialize(session),
            ct);
}
