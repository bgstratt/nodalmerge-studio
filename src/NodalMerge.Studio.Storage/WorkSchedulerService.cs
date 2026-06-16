using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class WorkSchedulerService : IWorkScheduler
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, ScheduledItem> _queue = new();
    private readonly IStudioNodeStore _nodeStore;

    public WorkSchedulerService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task EnqueueAsync(
        string workUnitId,
        string profileId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        CancellationToken ct = default)
    {
        // Idempotent: if already pending (not leased), update profileId without duplicating.
        _queue.AddOrUpdate(
            workUnitId,
            _ => new ScheduledItem(workUnitId, profileId, taskId, null, null, 0, model, baseUrl, apiKey, provider),
            (_, existing) => existing.LeasedBy is not null
                ? existing  // keep leased item unchanged
                : existing with { ProfileId = profileId, TaskId = taskId, Model = model, BaseUrl = baseUrl, ApiKey = apiKey, Provider = provider });

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.SchedulerV1,
            workUnitId,
            JsonSerializer.Serialize(_queue[workUnitId]),
            ct).ConfigureAwait(false);
    }

    public async Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

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

            return acquired;
        }

        return null;
    }

    public async Task ReleaseAsync(string workUnitId, bool success, CancellationToken ct = default)
    {
        if (success)
        {
            _queue.TryRemove(workUnitId, out _);
            // Overwrite with a terminal marker so the node store reflects completion.
            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.SchedulerV1, workUnitId,
                "{\"status\":\"completed\"}", ct).ConfigureAwait(false);
        }
        else
        {
            // Reset lease so the next poll can retry.
            if (_queue.TryGetValue(workUnitId, out var item))
            {
                var reset = item with { LeasedBy = null, LeasedAt = null };
                _queue.TryUpdate(workUnitId, reset, item);
                await _nodeStore.WriteNodeAsync(
                    StudioNodeKind.SchedulerV1, workUnitId,
                    JsonSerializer.Serialize(reset), ct).ConfigureAwait(false);
            }
        }
    }

    public Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ScheduledItem> items = _queue.Values.OrderBy(i => i.AttemptCount).ToList();
        return Task.FromResult(items);
    }
}
