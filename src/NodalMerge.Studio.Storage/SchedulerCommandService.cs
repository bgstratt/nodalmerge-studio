using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 15f — the one implementation of scheduler commands shared by the MCP tool, the REST
// endpoint, and the agent-loop's in-process dispatcher. REST's EnqueueBody previously dropped
// model/baseUrl/apiKey/provider; now those params flow through identically on all three transports.
public sealed class SchedulerCommandService(IWorkScheduler scheduler) : ISchedulerCommandService
{
    public async Task<ScheduledItem> EnqueueAsync(
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
        await scheduler.EnqueueAsync(workUnitId, profileId, taskId, model, baseUrl, apiKey, provider, sessionId, ct)
            .ConfigureAwait(false);

        // IWorkScheduler.EnqueueAsync is fire-and-forget (returns void), so we need to
        // reconstruct the ScheduledItem from what was passed in. The scheduler's internal
        // state (leasedBy, leasedAt, attemptCount, conflict) is best read back via ListPendingAsync.
        // For the immediate caller who just wants confirmation the item was queued, the input
        // params plus a null lease/conflict is accurate enough — any real conflict detection
        // or lease info will be produced by the scheduler itself and surfaced on the next
        // ListPendingAsync call.
        return new ScheduledItem(workUnitId, profileId, taskId, null, null, 0, model, baseUrl, apiKey, provider, sessionId);
    }

    public Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default) =>
        scheduler.ListPendingAsync(ct);
}