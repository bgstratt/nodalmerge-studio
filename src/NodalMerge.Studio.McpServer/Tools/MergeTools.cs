using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class MergeTools(IMergeService merge, IStudioNodeStore nodeStore)
{
    [McpServerTool(Name = McpToolNames.MergePropose), Description("Submit a merge proposal from a work branch.")]
    public async Task<string> ProposeAsync(
        string sourceBranch,
        string targetBranch,
        string summary,
        string? goal = null,
        string? changeDescription = null,
        [Description("Idempotency key (GUID). Same commandId returns the cached result without creating a second proposal.")]
        string? commandId = null,
        CancellationToken cancellationToken = default)
    {
        // Idempotent: return cached result for same commandId.
        if (commandId is not null)
        {
            var cached = await nodeStore.ReadNodeAsync(StudioNodeKind.CommandResultV1, commandId, cancellationToken)
                .ConfigureAwait(false);
            if (cached is not null)
                return cached;
        }

        var proposalId = $"MP-{Guid.NewGuid():N}";
        var proposal = new MergeProposal(
            proposalId,
            sourceBranch,
            targetBranch,
            goal ?? summary,
            summary,
            changeDescription ?? summary,
            null,
            null,
            null,
            MergeProposalStatus.Draft);

        var created = await merge.ProposeAsync(proposal, cancellationToken).ConfigureAwait(false);
        var result = McpJson.Ok(new { proposalId = created.ProposalId, status = created.Status.ToString() });

        if (commandId is not null)
            await nodeStore.WriteNodeAsync(StudioNodeKind.CommandResultV1, commandId, result, cancellationToken)
                .ConfigureAwait(false);

        return result;
    }

    [McpServerTool(Name = McpToolNames.MergeValidate), Description("Validate a draft proposal, moving it to ReadyForReview.")]
    public async Task<string> ValidateAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        try
        {
            var proposal = await merge.ValidateAsync(proposalId, cancellationToken).ConfigureAwait(false);
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

    [McpServerTool(Name = McpToolNames.MergeReview), Description("Human review of a proposal (AP-4). Decision must be Approved or Rejected.")]
    public async Task<string> ReviewAsync(
        string proposalId,
        string decision,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<MergeProposalStatus>(decision, ignoreCase: true, out var status) ||
            status is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected))
        {
            return McpJson.Error(McpToolNames.MergeReview, "Decision must be 'Approved' or 'Rejected'.");
        }

        try
        {
            var proposal = await merge.ReviewAsync(proposalId, status, cancellationToken).ConfigureAwait(false);
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
    }

    [McpServerTool(Name = McpToolNames.MergeApply), Description("Apply an approved merge proposal (AP-4 gate: only Approved proposals may be applied).")]
    public async Task<string> ApplyAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        try
        {
            var proposal = await merge.ApplyAsync(proposalId, cancellationToken).ConfigureAwait(false);
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
