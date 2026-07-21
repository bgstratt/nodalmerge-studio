using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime;

// Phase 2 item 1 — see IGoalGuardrailService's doc comment for the alert-only design rationale.
// Computed fresh on every call rather than cached/polled: a goal's token total only ever grows
// and its subtree rarely has more than a few dozen work units, so re-summing each time is cheap
// and avoids any stale-state bookkeeping (no "have we already alerted for this goal" tracking).
public sealed class GoalGuardrailService(
    IWorkUnitService workUnits,
    IConversationLogService conversationLog,
    WorkspaceOptions options) : IGoalGuardrailService
{
    // "No longer actively running" — used by GetActiveGoalStatusesAsync to decide which goals the
    // guardrail still watches (token/time budget). This is the BROADEST terminal set on purpose and
    // is intentionally different from the GC/retention set (SnapshotRetentionPolicy = {Completed,
    // Merged}): a Failed or Cancelled goal is not burning budget so the guardrail ignores it — but its
    // seed snapshot must still be RETAINED because a human can revive it, which is a different
    // question answered by a different set. If a human revives one, it returns to Executing and
    // reappears here as active. Do not unify these sets.
    private static readonly WorkUnitStatus[] TerminalStatuses =
    [
        WorkUnitStatus.Completed,
        WorkUnitStatus.Merged,
        WorkUnitStatus.Cancelled,
        WorkUnitStatus.Failed,
    ];

    public async Task<GoalGuardrailStatus?> GetStatusAsync(string goalWorkUnitId, CancellationToken cancellationToken = default)
    {
        var goal = await workUnits.GetAsync(goalWorkUnitId, cancellationToken).ConfigureAwait(false);
        return goal is null ? null : await ComputeAsync(goal, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<GoalGuardrailStatus>> GetActiveGoalStatusesAsync(CancellationToken cancellationToken = default)
    {
        var all = await workUnits.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var activeGoals = all.Where(w => w.ParentWorkUnitId is null && !TerminalStatuses.Contains(w.Status));

        var statuses = new List<GoalGuardrailStatus>();
        foreach (var goal in activeGoals)
            statuses.Add(await ComputeAsync(goal, cancellationToken).ConfigureAwait(false));

        return statuses;
    }

    private async Task<GoalGuardrailStatus> ComputeAsync(WorkUnit goal, CancellationToken ct)
    {
        var subtreeIds = await CollectSubtreeIdsAsync(goal.WorkUnitId, ct).ConfigureAwait(false);

        long totalTokens = 0;
        foreach (var id in subtreeIds)
        {
            var entries = await conversationLog.GetEntriesAsync(id, ct).ConfigureAwait(false);
            foreach (var entry in entries)
                totalTokens += (entry.InputTokens ?? 0) + (entry.OutputTokens ?? 0);
        }

        var elapsedMinutes = (DateTimeOffset.UtcNow - goal.CreatedAt).TotalMinutes;

        return new GoalGuardrailStatus(
            goal.WorkUnitId,
            totalTokens,
            options.MaxGoalTokens,
            options.MaxGoalTokens is { } maxTokens && totalTokens > maxTokens,
            elapsedMinutes,
            options.MaxGoalDurationMinutes,
            options.MaxGoalDurationMinutes is { } maxMinutes && elapsedMinutes > maxMinutes);
    }

    private async Task<List<string>> CollectSubtreeIdsAsync(string rootWorkUnitId, CancellationToken ct)
    {
        var ids = new List<string> { rootWorkUnitId };
        var frontier = new Queue<string>();
        frontier.Enqueue(rootWorkUnitId);

        while (frontier.Count > 0)
        {
            var current = frontier.Dequeue();
            var children = await workUnits.GetChildrenAsync(current, ct).ConfigureAwait(false);
            foreach (var child in children)
            {
                ids.Add(child.WorkUnitId);
                frontier.Enqueue(child.WorkUnitId);
            }
        }

        return ids;
    }
}
