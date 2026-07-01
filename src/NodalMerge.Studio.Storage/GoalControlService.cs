using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class GoalControlService(
    IGoalNodeService goalNodes,
    IAgentControlService agentControl,
    IExecutionSessionService sessions,
    ISchedulerCommandService scheduler,
    IExecutionEventStream events) : IGoalControlService
{
    public async Task<GoalNode> PauseAsync(
        string goalId,
        string? reason = null,
        string? pausedBy = null,
        CancellationToken ct = default)
    {
        var goal = await goalNodes.GetAsync(goalId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Goal '{goalId}' not found.");

        if (goal.Status is GoalStatus.Paused)
            return goal;

        if (goal.Status is GoalStatus.Converged or GoalStatus.Abandoned)
            throw new InvalidOperationException($"Cannot pause a goal with status '{goal.Status}'.");

        // Stop all active agents for this work unit
        var active = await agentControl.ListActiveAsync(ct).ConfigureAwait(false);
        foreach (var agent in active.Where(a => a.WorkUnitId == goal.WorkUnitId))
        {
            try { await agentControl.StopAsync(agent.AgentId, ct).ConfigureAwait(false); }
            catch { /* best-effort: agent may already be finishing */ }
        }

        // Mark session as paused
        if (goal.SessionId is not null)
        {
            try
            {
                await sessions.SetStatusAsync(goal.SessionId, ExecutionSessionStatus.Paused, ct).ConfigureAwait(false);
                await events.AppendAsync(
                    goal.SessionId, goal.WorkUnitId, ExecutionEventKind.GoalPaused,
                    new GoalPausedPayload(goal.GoalId, goal.WorkUnitId, reason, pausedBy),
                    ct: ct).ConfigureAwait(false);
            }
            catch { /* event stream is best-effort */ }
        }

        var updated = goal with { Status = GoalStatus.Paused, PauseReason = reason, UpdatedAt = DateTimeOffset.UtcNow };
        return await goalNodes.RecordAsync(updated, ct).ConfigureAwait(false);
    }

    public async Task<GoalNode> ResumeAsync(
        string goalId,
        string? steering = null,
        string? resumedBy = null,
        string? profileId = null,
        CancellationToken ct = default)
    {
        var goal = await goalNodes.GetAsync(goalId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Goal '{goalId}' not found.");

        if (goal.Status is not GoalStatus.Paused)
            throw new InvalidOperationException($"Cannot resume a goal with status '{goal.Status}'. Only Paused goals can be resumed.");

        // Restore session to active
        if (goal.SessionId is not null)
        {
            try
            {
                await sessions.SetStatusAsync(goal.SessionId, ExecutionSessionStatus.Active, ct).ConfigureAwait(false);
                await events.AppendAsync(
                    goal.SessionId, goal.WorkUnitId, ExecutionEventKind.GoalResumed,
                    new GoalResumedPayload(goal.GoalId, goal.WorkUnitId, steering, resumedBy),
                    ct: ct).ConfigureAwait(false);
            }
            catch { /* event stream is best-effort */ }
        }

        // Re-enqueue the work unit so the scheduler spawns a new agent.
        // Steering is stored as a clarification response so the next agent kickoff can surface it.
        var resolvedProfileId = profileId ?? await ResolveProfileIdAsync(goal.SessionId, ct).ConfigureAwait(false);
        await scheduler.EnqueueAsync(goal.WorkUnitId, resolvedProfileId, sessionId: goal.SessionId, ct: ct).ConfigureAwait(false);

        var updated = goal with { Status = GoalStatus.Exploring, PauseReason = null, UpdatedAt = DateTimeOffset.UtcNow };
        return await goalNodes.RecordAsync(updated, ct).ConfigureAwait(false);
    }

    private async Task<string> ResolveProfileIdAsync(string? sessionId, CancellationToken ct)
    {
        if (sessionId is null) return "worker";
        var session = await sessions.GetAsync(sessionId, ct).ConfigureAwait(false);
        // Use the first profile that isn't "orchestrator" (prefer worker/execute-stage)
        var profile = session?.ProfileIdSet
            .FirstOrDefault(p => !p.Equals("orchestrator", StringComparison.OrdinalIgnoreCase));
        return profile ?? "worker";
    }
}
