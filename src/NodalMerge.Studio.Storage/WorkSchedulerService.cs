using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class WorkSchedulerService : IWorkScheduler
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, ScheduledItem> _queue = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IExecutionEventStream _events;

    public WorkSchedulerService(IStudioNodeStore nodeStore, IExecutionEventStream events)
    {
        _nodeStore = nodeStore;
        _events    = events;
    }

    public async Task EnqueueAsync(
        string workUnitId,
        string profileId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        // Idempotent: track whether this is a new enqueue to avoid duplicate events.
        bool isNew = !_queue.ContainsKey(workUnitId);

        _queue.AddOrUpdate(
            workUnitId,
            _ => new ScheduledItem(workUnitId, profileId, taskId, null, null, 0, model, baseUrl, apiKey, provider, sessionId),
            (_, existing) => existing.LeasedBy is not null
                ? existing  // keep leased item unchanged
                : existing with { ProfileId = profileId, TaskId = taskId, Model = model, BaseUrl = baseUrl, ApiKey = apiKey, Provider = provider, SessionId = sessionId });

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.SchedulerV1,
            workUnitId,
            JsonSerializer.Serialize(_queue[workUnitId]),
            ct).ConfigureAwait(false);

        // Only emit the scheduled event on the first enqueue, not on retries.
        if (isNew && sessionId is not null)
        {
            var item = _queue[workUnitId];
            await _events.AppendAsync(
                sessionId,
                workUnitId,
                ExecutionEventKind.WorkUnitScheduled,
                new WorkUnitScheduledPayload(workUnitId, profileId, item.AttemptCount),
                ct: ct).ConfigureAwait(false);
        }
    }

    public async Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Re-acquire: if this agent already holds a valid lease, return it without re-emitting events.
        var reacquired = _queue.Values.FirstOrDefault(i =>
            i.LeasedBy == agentId &&
            i.LeasedAt.HasValue &&
            now - i.LeasedAt.Value < LeaseTimeout);
        if (reacquired is not null)
            return reacquired;

        foreach (var (key, item) in _queue)
        {
            // Skip items with a valid (non-expired) lease.
            if (item.LeasedBy is not null && item.LeasedAt.HasValue &&
                now - item.LeasedAt.Value < LeaseTimeout)
                continue;

            var acquired = item with
            {
                LeasedBy = agentId,
                LeasedAt = now,
                AttemptCount = item.AttemptCount + 1,
            };

            // Compare-and-swap to avoid double-acquisition.
            if (!_queue.TryUpdate(key, acquired, item))
                continue;

            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.SchedulerV1, key,
                JsonSerializer.Serialize(acquired), ct).ConfigureAwait(false);

            if (acquired.SessionId is not null)
            {
                await _events.AppendAsync(
                    acquired.SessionId,
                    acquired.WorkUnitId,
                    ExecutionEventKind.SchedulerLeaseAcquired,
                    new SchedulerLeaseAcquiredPayload(
                        acquired.WorkUnitId,
                        agentId,
                        now + LeaseTimeout),
                    ct: ct).ConfigureAwait(false);
            }

            return acquired;
        }

        return null;
    }

    public async Task ReleaseAsync(string workUnitId, bool success, CancellationToken ct = default)
    {
        if (success)
        {
            _queue.TryRemove(workUnitId, out var removed);
            var sessionId  = removed?.SessionId;
            var agentId    = removed?.LeasedBy ?? string.Empty;

            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.SchedulerV1, workUnitId,
                "{\"status\":\"completed\"}", ct).ConfigureAwait(false);

            if (sessionId is not null)
            {
                var completedEv = await _events.AppendAsync(
                    sessionId,
                    workUnitId,
                    ExecutionEventKind.WorkUnitCompleted,
                    new WorkUnitCompletedPayload(workUnitId, agentId, null),
                    ct: ct).ConfigureAwait(false);

                await _events.AppendAsync(
                    sessionId,
                    workUnitId,
                    ExecutionEventKind.SchedulerLeaseReleased,
                    new SchedulerLeaseReleasedPayload(workUnitId, agentId, true),
                    causedByEventId: completedEv.EventId,
                    ct: ct).ConfigureAwait(false);
            }
        }
        else
        {
            // Reset lease so the next poll can retry.
            if (_queue.TryGetValue(workUnitId, out var item))
            {
                var sessionId = item.SessionId;
                var agentId   = item.LeasedBy ?? string.Empty;
                var reset     = item with { LeasedBy = null, LeasedAt = null };
                _queue.TryUpdate(workUnitId, reset, item);
                await _nodeStore.WriteNodeAsync(
                    StudioNodeKind.SchedulerV1, workUnitId,
                    JsonSerializer.Serialize(reset), ct).ConfigureAwait(false);

                if (sessionId is not null)
                {
                    var failedEv = await _events.AppendAsync(
                        sessionId,
                        workUnitId,
                        ExecutionEventKind.WorkUnitFailed,
                        new WorkUnitFailedPayload(workUnitId, agentId, "Agent loop exited without success"),
                        ct: ct).ConfigureAwait(false);

                    await _events.AppendAsync(
                        sessionId,
                        workUnitId,
                        ExecutionEventKind.SchedulerLeaseReleased,
                        new SchedulerLeaseReleasedPayload(workUnitId, agentId, false),
                        causedByEventId: failedEv.EventId,
                        ct: ct).ConfigureAwait(false);
                }
            }
        }
    }

    public Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ScheduledItem> items = _queue.Values.OrderBy(i => i.AttemptCount).ToList();
        return Task.FromResult(items);
    }
}
