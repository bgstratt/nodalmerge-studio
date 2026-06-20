using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge;

// Slice 20c — stores Hybrid review timers as DAG nodes. ProcessExpiredAsync is called by the
// work scheduler poll loop; when a timer expires it fires ApplyAsync on the proposal, which
// re-enters the BeforeMerge gate. AutoReviewRule sees the timer is expired+approved and allows.
//
// IMergeCommandService is resolved lazily (via IServiceProvider) rather than constructor-injected:
// MergeCommandService depends on IPolicyGateService, which depends on AutoReviewRule, which depends
// on this service — a direct constructor dependency here would be a circular graph. Same pattern
// used by InMemoryMergeService for IWorkUnitService.
public sealed class ReviewTimerService(
    IStudioNodeStore nodeStore,
    IServiceProvider serviceProvider) : IReviewTimerService
{
    public async Task ScheduleAsync(
        string proposalId,
        string workUnitId,
        TimeSpan delay,
        CancellationToken ct = default)
    {
        var timer = new ReviewTimer(
            TimerId: Guid.NewGuid().ToString("N"),
            ProposalId: proposalId,
            WorkUnitId: workUnitId,
            ExpiresAt: DateTimeOffset.UtcNow.Add(delay));

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.ReviewTimerV1,
            proposalId,
            JsonSerializer.Serialize(timer),
            ct).ConfigureAwait(false);
    }

    public async Task TryCancelAsync(string proposalId, CancellationToken ct = default)
    {
        var existing = await GetAsync(proposalId, ct).ConfigureAwait(false);
        if (existing is null || existing.Cancelled)
            return;

        var cancelled = existing with { Cancelled = true };
        await nodeStore.WriteNodeAsync(
            StudioNodeKind.ReviewTimerV1,
            proposalId,
            JsonSerializer.Serialize(cancelled),
            ct).ConfigureAwait(false);
    }

    public async Task ProcessExpiredAsync(CancellationToken ct = default)
    {
        var records = await nodeStore
            .ReadAllNodesAsync(StudioNodeKind.ReviewTimerV1, ct)
            .ConfigureAwait(false);

        foreach (var (entityId, payloadJson) in records)
        {
            ReviewTimer? timer;
            try { timer = JsonSerializer.Deserialize<ReviewTimer>(payloadJson); }
            catch { continue; }

            if (timer is null || timer.Cancelled || timer.ExpiresAt > DateTimeOffset.UtcNow)
                continue;

            // Mark cancelled first so a second poll doesn't re-fire while ApplyAsync runs.
            await TryCancelAsync(timer.ProposalId, ct).ConfigureAwait(false);

            try
            {
                var mergeCommands = serviceProvider.GetRequiredService<IMergeCommandService>();
                await mergeCommands.ApplyAsync(timer.ProposalId, ct).ConfigureAwait(false);
            }
            catch
            {
                // Proposal may already be merged, rejected, or blocked — ignore.
            }
        }
    }

    public async Task<ReviewTimer?> GetAsync(string proposalId, CancellationToken ct = default)
    {
        var json = await nodeStore
            .ReadNodeAsync(StudioNodeKind.ReviewTimerV1, proposalId, ct)
            .ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<ReviewTimer>(json);
    }
}
