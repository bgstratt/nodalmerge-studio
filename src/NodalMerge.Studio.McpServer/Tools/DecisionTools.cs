using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class DecisionTools(IMergeService merges, IDecisionNodeService decisionNodes)
{
    [McpServerTool(Name = McpToolNames.DecisionRecord), Description("Record a decision against a merge proposal.")]
    public async Task<string> RecordAsync(
        string proposalId,
        string outcome,
        string? rationale = null,
        string? reviewerAgentId = null,
        string? reviewerModel = null,
        string? reviewerProvider = null,
        double? confidence = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await merges.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
            return McpJson.Error(McpToolNames.DecisionRecord, $"Proposal '{proposalId}' not found.");

        if (!Enum.TryParse<DecisionOutcome>(outcome, ignoreCase: true, out var decision))
            return McpJson.Error(McpToolNames.DecisionRecord, $"Invalid outcome '{outcome}'. Use Accepted, Rejected, Deferred, or Superseded.");

        var mergeStatus = decision switch
        {
            DecisionOutcome.Accepted => MergeProposalStatus.Approved,
            DecisionOutcome.Rejected => MergeProposalStatus.Rejected,
            DecisionOutcome.Superseded => MergeProposalStatus.Superseded,
            _ => proposal.Status
        };

        if (mergeStatus != proposal.Status)
        {
            proposal = await merges.ReviewAsync(proposalId, mergeStatus, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Persist decision node
        var decisionNode = new DecisionNode(
            DecisionId: $"dec-{Guid.NewGuid():N}",
            WorkUnitId: proposal.WorkUnitId ?? string.Empty,
            ProposalId: proposalId,
            Outcome: decision,
            ReviewerAgentId: reviewerAgentId,
            ReviewerModel: reviewerModel,
            ReviewerProvider: reviewerProvider,
            Confidence: confidence,
            Rationale: rationale,
            DecidedAt: DateTimeOffset.UtcNow);
        await decisionNodes.RecordAsync(decisionNode, cancellationToken).ConfigureAwait(false);

        return McpJson.Ok(new
        {
            decisionId = decisionNode.DecisionId,
            proposalId,
            outcome = decision.ToString(),
            status = proposal.Status.ToString(),
            confidence,
            rationale,
            decidedAt = decisionNode.DecidedAt
        });
    }

    [McpServerTool(Name = McpToolNames.DecisionList), Description("List decisions (merge proposals with their review status).")]
    public async Task<string> ListAsync(string? workUnitId = null, CancellationToken cancellationToken = default)
    {
        if (workUnitId is not null)
        {
            var storedDecisions = await decisionNodes.ListByWorkUnitAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            if (storedDecisions.Count > 0)
            {
                var decisions = storedDecisions.Select(d => new
                {
                    decisionId = d.DecisionId,
                    proposalId = d.ProposalId,
                    workUnitId = d.WorkUnitId,
                    outcome = d.Outcome.ToString(),
                    confidence = d.Confidence,
                    decidedAt = d.DecidedAt
                }).ToList();
                return McpJson.Ok(new { decisions, source = "decision-store" });
            }
        }

        var proposals = await merges.ListAsync(sourceBranch: null, cancellationToken).ConfigureAwait(false);
        var fallback = proposals.Select(p => new
        {
            decisionId = $"dec-{p.ProposalId}",
            proposalId = p.ProposalId,
            workUnitId = p.WorkUnitId,
            sourceBranch = p.SourceBranch,
            status = p.Status.ToString(),
            confidence = p.Confidence,
            agentId = p.AgentId,
            model = p.Model,
            provider = p.Provider
        }).ToList();

        return McpJson.Ok(new { decisions = fallback, source = "proposals" });
    }
}