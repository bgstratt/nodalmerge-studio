using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ExternalWorkspaceTools(
    IGoalNodeService goalNodes,
    IAgentControlService agentControl,
    IClarificationCommandService clarifications,
    IArtifactCommandService artifactCommands,
    IArtifactLineageService artifactLineage)
{
    [McpServerTool(Name = McpServerToolNames.WorkspaceStatus)]
    [Description("Get a high-level snapshot of the workspace: goal counts by status, active agent count, pending clarification count, and which goals need human input right now.")]
    public async Task<string> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var goals = await goalNodes.ListAsync(cancellationToken).ConfigureAwait(false);
            var activeAgents = await agentControl.ListActiveAsync(cancellationToken).ConfigureAwait(false);
            var pendingClarifications = await clarifications.ListActiveRequestsAsync(cancellationToken).ConfigureAwait(false);

            var goalCounts = goals
                .GroupBy(g => g.Status)
                .ToDictionary(g => g.Key.ToString(), g => g.Count());

            var clarificationsByGoal = pendingClarifications
                .GroupBy(c => c.WorkUnitId)
                .ToDictionary(g => g.Key, g => g.Count());

            var goalsNeedingInput = goals
                .Where(g => clarificationsByGoal.ContainsKey(g.WorkUnitId))
                .Select(g => new
                {
                    goalId = g.GoalId,
                    goal = g.Goal,
                    clarificationCount = clarificationsByGoal[g.WorkUnitId]
                })
                .ToList();

            return McpJson.Ok(new
            {
                goals = new
                {
                    total = goals.Count,
                    byStatus = goalCounts
                },
                activeAgentCount = activeAgents.Count,
                pendingClarificationCount = pendingClarifications.Count,
                goalsNeedingInput
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.WorkspaceStatus, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.FeedbackRecord)]
    [Description("Record a human feedback note as a durable constraint artifact. If a goalId is provided the feedback is scoped to that goal's timeline; if omitted it becomes a workspace-wide constraint that all future agents can see.")]
    public async Task<string> RecordFeedbackAsync(
        [Description("Short label for this feedback (e.g. 'Use tabs not spaces', 'Never modify the config folder').")] string title,
        [Description("The feedback body — human guidance, constraint, or correction.")] string feedback,
        [Description("Optional goalId to scope this feedback to a specific goal's artifact timeline. Omit for workspace-wide guidance.")] string? goalId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(title))
                return McpJson.Error(McpServerToolNames.FeedbackRecord, "title is required.");
            if (string.IsNullOrWhiteSpace(feedback))
                return McpJson.Error(McpServerToolNames.FeedbackRecord, "feedback is required.");

            if (goalId is not null)
            {
                var goal = await goalNodes.GetAsync(goalId, cancellationToken).ConfigureAwait(false);
                if (goal is null)
                    return McpJson.Error(McpServerToolNames.FeedbackRecord, $"Goal '{goalId}' not found.");

                var artifact = await artifactCommands.RecordAsync(
                    goal.WorkUnitId,
                    "Constraint",
                    title,
                    feedback,
                    ct: cancellationToken).ConfigureAwait(false);

                return McpJson.Ok(new
                {
                    recorded = true,
                    scope = "goal",
                    goalId,
                    workUnitId = goal.WorkUnitId,
                    artifactId = artifact.ArtifactId
                });
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                var workspaceArtifact = new ArtifactRef(
                    ArtifactId: $"FEEDBACK-{Guid.NewGuid():N}",
                    Type: ArtifactType.Constraint,
                    ParentArtifactId: null,
                    Status: ArtifactStatus.Active,
                    CreatedAt: now,
                    OwnedByWorkUnitId: null,
                    OwnedByAgentId: null,
                    Title: title,
                    Body: feedback);

                await artifactLineage.RecordAsync(workspaceArtifact, cancellationToken).ConfigureAwait(false);

                return McpJson.Ok(new
                {
                    recorded = true,
                    scope = "workspace",
                    goalId = (string?)null,
                    artifactId = workspaceArtifact.ArtifactId
                });
            }
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.FeedbackRecord, ex.Message);
        }
    }
}
