using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class MergeTools(IMergeService merge)
{
    [McpServerTool(Name = McpToolNames.MergePropose), Description("Submit a merge proposal.")]
    public async Task<string> ProposeAsync(
        string sourceBranch,
        string targetBranch,
        string summary,
        string? goal = null,
        string? changeDescription = null,
        CancellationToken cancellationToken = default)
    {
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
        return McpJson.Ok(new { proposalId = created.ProposalId });
    }

    [McpServerTool(Name = McpToolNames.MergeValidate), Description("Validate a merge proposal for review.")]
    public async Task<string> ValidateAsync(string proposalId, CancellationToken cancellationToken = default) =>
        McpJson.Ok(await merge.ValidateAsync(proposalId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = McpToolNames.MergeReview), Description("Review a merge proposal (human approval required in v1).")]
    public async Task<string> ReviewAsync(
        string proposalId,
        string decision,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<MergeProposalStatus>(decision, ignoreCase: true, out var status) ||
            status is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected))
        {
            return McpJson.Error(McpToolNames.MergeReview, "Decision must be Approved or Rejected.");
        }

        var proposal = await merge.ReviewAsync(proposalId, status, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(proposal);
    }

    [McpServerTool(Name = McpToolNames.MergeApply), Description("Apply an approved merge proposal.")]
    public async Task<string> ApplyAsync(string proposalId, CancellationToken cancellationToken = default) =>
        McpJson.Ok(await merge.ApplyAsync(proposalId, cancellationToken).ConfigureAwait(false));
}
