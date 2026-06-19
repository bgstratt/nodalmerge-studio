using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class MergeTools(IMergeCommandService mergeCommands)
{
    [McpServerTool(Name = McpToolNames.MergePropose), Description("Submit a merge proposal with full diff, artifact lineage, execution event, and work-unit status transition.")]
    public async Task<string> ProposeAsync(
        string sourceBranch,
        string targetBranch,
        string summary,
        string? goal = null,
        string? changeDescription = null,
        [Description("Your work unit ID — required for artifact lineage, the work-unit status transition, and for the proposal to be discoverable when the Review pane is scoped to a session.")]
        string? workUnitId = null,
        [Description("Your agent ID for attribution (optional).")]
        string? agentId = null,
        string? model = null,
        string? provider = null,
        string? sessionId = null,
        [Description("Idempotency key (GUID). Same commandId returns the cached result without creating a second proposal.")]
        string? commandId = null,
        CancellationToken cancellationToken = default)
    {
        var created = await mergeCommands.ProposeAsync(
            sourceBranch, targetBranch, summary, goal, changeDescription,
            workUnitId: workUnitId, agentId: agentId, model: model, provider: provider, sessionId: sessionId,
            commandId: commandId, cancellationToken: cancellationToken).ConfigureAwait(false);

        return McpJson.Ok(new { proposalId = created.ProposalId, status = created.Status.ToString() });
    }

    [McpServerTool(Name = McpToolNames.MergeValidate), Description("Validate a draft proposal, moving it to ReadyForReview.")]
    public async Task<string> ValidateAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        try
        {
            var proposal = await mergeCommands.ValidateAsync(proposalId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(proposal);
        }
        catch (KeyNotFoundException)
        {
            return McpJson.Error(McpToolNames.MergeValidate, $"Proposal '{proposalId}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpToolNames.MergeValidate, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.MergeReview), Description("Review a proposal. Set automated=true for pre-gate reviewer; otherwise human review.")]
    public async Task<string> ReviewAsync(
        string proposalId,
        string decision,
        string? verificationResults = null,
        bool automated = false,
        string? reviewerAgentId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var proposal = await mergeCommands.ReviewAsync(
                proposalId, decision, verificationResults, automated, reviewerAgentId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(proposal);
        }
        catch (KeyNotFoundException)
        {
            return McpJson.Error(McpToolNames.MergeReview, $"Proposal '{proposalId}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpToolNames.MergeReview, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return McpJson.Error(McpToolNames.MergeReview, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.MergeApply), Description("Apply an approved merge proposal (AP-4 gate: only Approved proposals may be applied).")]
    public async Task<string> ApplyAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        try
        {
            var proposal = await mergeCommands.ApplyAsync(proposalId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(proposal);
        }
        catch (KeyNotFoundException)
        {
            return McpJson.Error(McpToolNames.MergeApply, $"Proposal '{proposalId}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpToolNames.MergeApply, ex.Message);
        }
    }
}
