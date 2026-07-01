using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ExternalResultsTools(
    IGoalNodeService goalNodes,
    IMergeService mergeService,
    IMergeCommandService mergeCommands)
{
    [McpServerTool(Name = McpServerToolNames.ResultsGet)]
    [Description("Get merge proposals (results) for a goal. Returns a list of proposals with their status, summary, confidence, and files touched. A proposal in ReadyForReview or Approved status is ready to be applied via nms_v1_results_apply.")]
    public async Task<string> GetAsync(
        [Description("The goalId returned by nms_v1_goal_run or nms_v1_goal_list.")] string goalId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var goal = await goalNodes.GetAsync(goalId, cancellationToken).ConfigureAwait(false);
            if (goal is null)
                return McpJson.Error(McpServerToolNames.ResultsGet, $"Goal '{goalId}' not found.");

            var all = await mergeService.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
            var proposals = all
                .Where(p => p.WorkUnitId == goal.WorkUnitId)
                .Select(p => new
                {
                    proposalId = p.ProposalId,
                    status = p.Status.ToString(),
                    goal = p.Goal,
                    summary = p.Summary,
                    changeDescription = p.ChangeDescription,
                    confidence = p.Confidence,
                    filesTouched = p.FilesTouched ?? [],
                    reviewNotes = p.ReviewNotes,
                    autoApplied = p.AutoApplied
                })
                .ToList();

            return McpJson.Ok(new { goalId, proposals });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.ResultsGet, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.ResultsApply)]
    [Description("Apply an approved merge proposal, merging the agent's changes into the target branch. The proposal must be in ReadyForReview or Approved status. Returns the final status and any conflict information.")]
    public async Task<string> ApplyAsync(
        [Description("The proposalId to apply. Obtain this from nms_v1_results_get.")] string proposalId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var proposal = await mergeCommands.ApplyAsync(proposalId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                proposalId = proposal.ProposalId,
                status = proposal.Status.ToString(),
                sourceBranch = proposal.SourceBranch,
                targetBranch = proposal.TargetBranch,
                autoApplied = proposal.AutoApplied
            });
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpServerToolNames.ResultsApply, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpServerToolNames.ResultsApply, ex.Message);
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.ResultsApply, ex.Message);
        }
    }
}
