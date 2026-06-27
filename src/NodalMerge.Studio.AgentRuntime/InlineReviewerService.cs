using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace NodalMerge.Studio.AgentRuntime;

// Slice 20b — runs ReviewerAgentLoop synchronously (awaited) for AgentApproval/Hybrid policies.
// Called from AutoReviewRule at the BeforeMerge checkpoint so the gate can return a definitive
// PolicyResult before ApplyAsync proceeds.
public sealed class InlineReviewerService(
    IAgentControlService agentControl,
    IMergeService merge,
    IEvidenceNodeService evidenceNodes,
    IServiceProvider serviceProvider) : IInlineReviewerService
{
    public async Task<InlineReviewResult> ReviewAsync(
        string workUnitId,
        string proposalId,
        CancellationToken ct = default)
    {
        var creds = agentControl.GetOrchestratorCredentials(workUnitId);

        // When the work unit is a child worker spawned by fan-out, the orchestrator
        // credentials are registered on the parent, not the child. Walk up to find them.
        if (creds is null)
        {
            var workUnits = serviceProvider.GetService<IWorkUnitService>();
            if (workUnits is not null)
            {
                var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
                if (wu?.ParentWorkUnitId is { } parentId)
                    creds = agentControl.GetOrchestratorCredentials(parentId);
            }
        }

        if (creds is null)
            return new InlineReviewResult(false, "No LLM credentials configured for this work unit.");

        var agentId = $"reviewer-auto-{Guid.NewGuid():N}";
        var dispatcher = serviceProvider.GetRequiredService<McpToolDispatcher>();
        var llm = serviceProvider.GetRequiredService<LlmClient>();
        var conversationLog = serviceProvider.GetRequiredService<IConversationLogService>();

        // Slice — hand the reviewer filesTouched/justification up front instead of relying on it
        // to remember to go fetch them; ReviewerAgentLoop's prompt already says to check this, but
        // it previously had no tool that could (see ReviewerAgentLoop kickoff message).
        var proposalForReview = await merge.GetAsync(proposalId, ct).ConfigureAwait(false);

        var loop = new ReviewerAgentLoop(
            agentId, workUnitId, proposalId,
            creds.Provider, creds.Model, creds.BaseUrl, creds.ApiKey,
            dispatcher, llm,
            filesTouched: proposalForReview?.FilesTouched,
            noFileChangesJustification: proposalForReview?.NoFileChangesJustification,
            conversationLog: conversationLog);

        await loop.RunAsync(ct).ConfigureAwait(false);

        var proposal = await merge.GetAsync(proposalId, ct).ConfigureAwait(false);
        var approved = proposal?.Status is MergeProposalStatus.Approved or MergeProposalStatus.Merged;

        await evidenceNodes.RecordAsync(new EvidenceNode(
            EvidenceId: $"ev-{Guid.NewGuid():N}",
            WorkUnitId: workUnitId,
            ProposalId: proposalId,
            Kind: EvidenceKind.AutomatedReview,
            Summary: proposal?.VerificationResults ?? (approved ? "Approved" : "Rejected"),
            DetailJson: null,
            AttachedAt: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

        return new InlineReviewResult(approved, proposal?.VerificationResults);
    }
}
