using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ExternalGoalTools(
    IGoalControlService goalControl,
    IGoalNodeService goalNodes,
    IWorkUnitCommandService workUnitCommands,
    IExecutionSessionService sessions,
    ISchedulerCommandService scheduler,
    IClarificationCommandService clarifications,
    IDeadLetterService deadLetter,
    IReplanService replan,
    IContinueService continueService)
{
    [McpServerTool(Name = McpServerToolNames.GoalPause)]
    [Description("Pause a goal and all its active agents. The goal session is marked Paused and active agents are stopped gracefully. The goal can be resumed later via nms_v1_goal_resume.")]
    public async Task<string> PauseAsync(
        [Description("The goalId returned by nm_v1_goal_create or nms_v1_goal_list.")] string goalId,
        [Description("Optional human-readable reason for pausing.")] string? reason = null,
        [Description("Who or what initiated the pause (e.g. the caller's name or 'user').")] string? pausedBy = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var goal = await goalControl.PauseAsync(goalId, reason, pausedBy, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                goalId = goal.GoalId,
                status = goal.Status.ToString(),
                pauseReason = goal.PauseReason,
                updatedAt = goal.UpdatedAt
            });
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpServerToolNames.GoalPause, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpServerToolNames.GoalPause, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.GoalResume)]
    [Description("Resume a paused goal. Restores the session to Active and re-enqueues the work unit so the scheduler spawns a new agent. Optionally inject a steering message to redirect the agent's next run.")]
    public async Task<string> ResumeAsync(
        [Description("The goalId of the paused goal.")] string goalId,
        [Description("Optional steering message injected as context for the agent's next run — use this to redirect the work, add constraints, or correct course.")] string? steering = null,
        [Description("Who or what initiated the resume (e.g. 'user').")] string? resumedBy = null,
        [Description("Agent profile ID to use when re-enqueueing. Defaults to the session's existing profile or 'worker'.")] string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var goal = await goalControl.ResumeAsync(goalId, steering, resumedBy, profileId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                goalId = goal.GoalId,
                status = goal.Status.ToString(),
                updatedAt = goal.UpdatedAt
            });
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpServerToolNames.GoalResume, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpServerToolNames.GoalResume, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.GoalRun)]
    [Description("Start a new goal. Creates a work unit, an execution session, and enqueues the orchestrator agent — all in one call. Returns the goalId and sessionId needed to track and interact with this goal.")]
    public async Task<string> RunAsync(
        [Description("The goal to accomplish — describe what you want the agent to do.")] string goal,
        [Description("ID of a previously registered repository to work in. Use nms_v1_repo_list to find IDs.")] string? repositoryId = null,
        [Description("Optional success criteria to guide the agent.")] string? successCriteria = null,
        [Description("Worker agent profile ID. Defaults to 'worker'.")] string? workerProfileId = null,
        [Description("Review policy for both gates (task + workspace): 'HumanRequired' (a human applies), 'AgentApproval' (an agent reviews and auto-applies on approval — fully autonomous), or 'Hybrid' (agent approves, human can override for a window). Omit to use the server's configured default (Workspace:DefaultTaskReviewPolicy).")] string? reviewPolicy = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(goal))
                return McpJson.Error(McpServerToolNames.GoalRun, "goal is required.");

            // Per-call override of the server default (WorkspaceOptions.DefaultTaskReviewPolicy). Null
            // leaves the command's policies null, so CreateWorkUnitAsync applies the server default —
            // the "default, but any caller can override per goal" model the UI already uses.
            ReviewPolicy? parsedPolicy = null;
            if (!string.IsNullOrWhiteSpace(reviewPolicy))
            {
                if (!Enum.TryParse<ReviewPolicy>(reviewPolicy, ignoreCase: true, out var p))
                    return McpJson.Error(
                        McpServerToolNames.GoalRun,
                        $"Invalid reviewPolicy '{reviewPolicy}'. Use HumanRequired, AgentApproval, or Hybrid.");
                parsedPolicy = p;
            }

            var workUnit = await workUnitCommands.CreateAsync(
                new WorkUnitCreateCommand(
                    goal,
                    "mcp-external",
                    SuccessCriteria: successCriteria,
                    RepositoryId: repositoryId,
                    TaskReviewPolicy: parsedPolicy,
                    WorkspaceReviewPolicy: parsedPolicy),
                cancellationToken).ConfigureAwait(false);

            var goalNode = new GoalNode(
                GoalId: workUnit.WorkUnitId,
                Goal: workUnit.Goal,
                WorkUnitId: workUnit.WorkUnitId,
                BranchId: workUnit.BranchId,
                Status: GoalStatus.Exploring,
                CreatedAt: workUnit.CreatedAt,
                UpdatedAt: workUnit.UpdatedAt,
                Owner: "mcp-external",
                ParentGoalId: null,
                RepositoryId: workUnit.RepositoryId); // #1 goal replication — route to the repo room.
            await goalNodes.RecordAsync(goalNode, cancellationToken).ConfigureAwait(false);

            var session = await sessions.CreateAsync(
                workUnit.WorkUnitId,
                "{}",
                ["orchestrator", workerProfileId ?? "worker"],
                ct: cancellationToken).ConfigureAwait(false);

            await scheduler.EnqueueAsync(
                workUnit.WorkUnitId,
                "orchestrator",
                sessionId: session.SessionId,
                ct: cancellationToken).ConfigureAwait(false);

            return McpJson.Ok(new
            {
                goalId = workUnit.WorkUnitId,
                workUnitId = workUnit.WorkUnitId,
                sessionId = session.SessionId,
                branchId = workUnit.BranchId,
                status = "running"
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.GoalRun, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.GoalList)]
    [Description("List all goals in the workspace with their current status. Use this to discover goalIds for status checks, pause/resume, or cancellation.")]
    public async Task<string> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var goals = await goalNodes.ListAsync(cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                goals = goals.Select(g => new
                {
                    goalId = g.GoalId,
                    goal = g.Goal,
                    status = g.Status.ToString(),
                    pauseReason = g.PauseReason,
                    workUnitId = g.WorkUnitId,
                    branchId = g.BranchId,
                    updatedAt = g.UpdatedAt
                })
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.GoalList, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.GoalStatus)]
    [Description("Get detailed status for a specific goal, including any pending clarifications that need a human response, an unresolved failure (if the goal is dead-lettered), and the current session state.")]
    public async Task<string> StatusAsync(
        [Description("The goalId returned by nms_v1_goal_run or nms_v1_goal_list.")] string goalId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var goal = await goalNodes.GetAsync(goalId, cancellationToken).ConfigureAwait(false);
            if (goal is null)
                return McpJson.Error(McpServerToolNames.GoalStatus, $"Goal '{goalId}' not found.");

            var allClarifications = await clarifications.ListActiveRequestsAsync(cancellationToken).ConfigureAwait(false);
            var pending = allClarifications
                .Where(c => c.WorkUnitId == goal.WorkUnitId)
                .Select(c => new
                {
                    clarificationId = c.RequestId,
                    question = c.Question,
                    context = c.Context,
                    options = c.Options,
                    timeoutAt = c.TimeoutAt,
                    timeoutBehavior = c.TimeoutBehavior,
                    requestedAt = c.RequestedAt
                })
                .ToList();

            var allSessions = await sessions.ListAsync(cancellationToken).ConfigureAwait(false);
            var session = allSessions.FirstOrDefault(s => s.RootWorkUnitId == goal.WorkUnitId);

            // Surfaces an unresolved failure right where the recommended external flow already
            // looks for state — without this, a caller following nms_v1_goal_run -> poll
            // nms_v1_goal_status has no way to discover a goal is dead-lettered at all short of
            // dropping to the detailed nm_v1_dead_letter_* tools or REST.
            var deadLetterEntry = await deadLetter.GetLatestForWorkUnitAsync(goal.WorkUnitId, cancellationToken)
                .ConfigureAwait(false);
            var deadLetterInfo = deadLetterEntry is null ? null : new
            {
                entryId = deadLetterEntry.EntryId,
                kind = deadLetterEntry.Kind.ToString(),
                reason = deadLetterEntry.Reason,
                attemptCount = deadLetterEntry.AttemptCount,
                maxAttemptsReached = deadLetterEntry.MaxAttemptsReached,
                occurredAt = deadLetterEntry.OccurredAt,
                recoverableActions = deadLetterEntry.MaxAttemptsReached
                    ? new[] { "replan" }
                    : deadLetterEntry.Kind == FailureKind.MaxIterationsExceeded
                        ? new[] { "retry", "retry_with_context", "continue", "replan" }
                        : new[] { "retry", "retry_with_context", "replan" }
            };

            return McpJson.Ok(new
            {
                goalId = goal.GoalId,
                goal = goal.Goal,
                status = goal.Status.ToString(),
                pauseReason = goal.PauseReason,
                workUnitId = goal.WorkUnitId,
                branchId = goal.BranchId,
                sessionId = goal.SessionId ?? session?.SessionId,
                sessionStatus = session?.Status.ToString(),
                pendingClarifications = pending,
                deadLetter = deadLetterInfo,
                updatedAt = goal.UpdatedAt
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.GoalStatus, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.GoalRecover)]
    [Description("Recover a dead-lettered goal. Resolves the goal's own latest dead-letter entry internally (no entryId needed — use nms_v1_goal_status to see whether a goal has a recoverable failure and which actions apply). action must be one of: retry (resume with captured credentials), retry_with_context (resume with a human correction folded into the goal, bypassing the normal attempt cap), continue (MaxIterationsExceeded only — resume the SAME work unit with its own prior conversation reconstructed and a fresh iteration budget), or replan (decompose the failed goal into fresh sub-slices; the only action still available once max attempts is reached).")]
    public async Task<string> RecoverAsync(
        [Description("The goalId to recover.")] string goalId,
        [Description("One of: retry, retry_with_context, continue, replan.")] string action,
        [Description("Required when action=retry_with_context — a human correction folded into the goal, addressing a different root cause than what produced the prior failures.")] string? steeringContext = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var goal = await goalNodes.GetAsync(goalId, cancellationToken).ConfigureAwait(false);
            if (goal is null)
                return McpJson.Error(McpServerToolNames.GoalRecover, $"Goal '{goalId}' not found.");

            var entry = await deadLetter.GetLatestForWorkUnitAsync(goal.WorkUnitId, cancellationToken).ConfigureAwait(false);
            if (entry is null)
                return McpJson.Error(McpServerToolNames.GoalRecover, $"Goal '{goalId}' has no dead-letter entry to recover from.");

            switch (action)
            {
                case "retry":
                {
                    var result = await deadLetter.RetryAsync(entry.EntryId, cancellationToken).ConfigureAwait(false);
                    return result.Outcome == DeadLetterRetryOutcome.Retried
                        ? McpJson.Ok(new { goalId, action, outcome = result.Outcome.ToString() })
                        : McpJson.Error(McpServerToolNames.GoalRecover, result.Message ?? $"Retry failed: {result.Outcome}.");
                }
                case "retry_with_context":
                {
                    if (string.IsNullOrWhiteSpace(steeringContext))
                        return McpJson.Error(McpServerToolNames.GoalRecover, "steeringContext is required for action=retry_with_context.");
                    var result = await deadLetter.RetryWithContextAsync(entry.EntryId, steeringContext, cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                    return result.Outcome == DeadLetterRetryOutcome.Retried
                        ? McpJson.Ok(new { goalId, action, outcome = result.Outcome.ToString() })
                        : McpJson.Error(McpServerToolNames.GoalRecover, result.Message ?? $"Retry failed: {result.Outcome}.");
                }
                case "continue":
                {
                    var result = await continueService.ContinueWithPriorContextAsync(entry.EntryId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    return result.Outcome == ContinueOutcome.Continued
                        ? McpJson.Ok(new { goalId, action, outcome = result.Outcome.ToString() })
                        : McpJson.Error(McpServerToolNames.GoalRecover, result.Message ?? $"Continue failed: {result.Outcome}.");
                }
                case "replan":
                {
                    var result = await replan.ReplanFailedSliceAsync(entry.EntryId, cancellationToken).ConfigureAwait(false);
                    // plans/phase-d-implementation.md D3 — stalenessSignal rides the response
                    // regardless of outcome (when resolvable) so a human sees "this plan looks
                    // stale" alongside the replan result itself.
                    return result.Outcome == ReplanOutcome.Replanned
                        ? McpJson.Ok(new
                        {
                            goalId, action, outcome = result.Outcome.ToString(),
                            newWorkUnitIds = result.NewWorkUnitIds, stalenessSignal = result.StalenessSignal,
                        })
                        : McpJson.Error(McpServerToolNames.GoalRecover, result.Message ?? $"Re-plan failed: {result.Outcome}.");
                }
                default:
                    return McpJson.Error(McpServerToolNames.GoalRecover, $"Unknown action '{action}' — must be one of: retry, retry_with_context, continue, replan.");
            }
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.GoalRecover, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.GoalCancel)]
    [Description("Cancel a goal and its entire subtree of spawned sub-tasks. Agents are stopped gracefully. Work units already completed or merged are left untouched.")]
    public async Task<string> CancelAsync(
        [Description("The goalId to cancel.")] string goalId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var goal = await goalNodes.GetAsync(goalId, cancellationToken).ConfigureAwait(false);
            if (goal is null)
                return McpJson.Error(McpServerToolNames.GoalCancel, $"Goal '{goalId}' not found.");

            var cancelled = await workUnitCommands.CancelAsync(goal.WorkUnitId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                goalId,
                workUnitId = goal.WorkUnitId,
                cancelledWorkUnits = cancelled.Count,
                status = "cancelled"
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.GoalCancel, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.GoalRequeue)]
    [Description("Un-cancel a previously cancelled goal and resume it — the inverse of nms_v1_goal_cancel. Leaf work units are re-queued for a worker; fan-out parents re-attempt reconciliation. Work units that finished before the cancel (Merged/Completed) are left untouched.")]
    public async Task<string> RequeueAsync(
        [Description("The goalId to requeue.")] string goalId,
        [Description("Optional steering notes recorded as a Constraint artifact for the resumed work.")] string? notes = null,
        [Description("Optional LLM credential overrides — a cancel/requeue cycle commonly spans a Host restart, which wipes the in-memory credential cache the inline reviewer and re-enqueued workers depend on.")] string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        string? overrideCredentialRef = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var goal = await goalNodes.GetAsync(goalId, cancellationToken).ConfigureAwait(false);
            if (goal is null)
                return McpJson.Error(McpServerToolNames.GoalRequeue, $"Goal '{goalId}' not found.");

            var requeued = await workUnitCommands.RequeueAsync(
                goal.WorkUnitId, notes,
                overrideModel, overrideBaseUrl, overrideApiKey, overrideProvider,
                overrideProfileId, overrideCredentialRef, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                goalId,
                workUnitId = goal.WorkUnitId,
                requeuedWorkUnits = requeued.Count,
                status = "requeued"
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.GoalRequeue, ex.Message);
        }
    }
}
