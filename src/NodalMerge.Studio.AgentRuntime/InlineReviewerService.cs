using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace NodalMerge.Studio.AgentRuntime;

// Slice 20b — runs ReviewerAgentLoop synchronously (awaited) for AgentApproval/Hybrid policies.
// Called from AutoReviewRule at the BeforeMerge checkpoint so the gate can return a definitive
// PolicyResult before ApplyAsync proceeds.
public sealed class InlineReviewerService(
    IAgentControlService agentControl,
    IMergeService merge,
    IServiceProvider serviceProvider) : IInlineReviewerService
{
    public async Task<InlineReviewResult> ReviewAsync(
        string workUnitId,
        string proposalId,
        CancellationToken ct = default)
    {
        var creds = agentControl.GetOrchestratorCredentials(workUnitId);
        if (creds is null)
            return new InlineReviewResult(false, "No LLM credentials configured for this work unit.");

        var agentId = $"reviewer-auto-{Guid.NewGuid():N}";
        var dispatcher = serviceProvider.GetRequiredService<McpToolDispatcher>();
        var llm = serviceProvider.GetRequiredService<LlmClient>();

        var loop = new ReviewerAgentLoop(
            agentId, workUnitId, proposalId,
            creds.Provider, creds.Model, creds.BaseUrl, creds.ApiKey,
            dispatcher, llm);

        await loop.RunAsync(ct).ConfigureAwait(false);

        var proposal = await merge.GetAsync(proposalId, ct).ConfigureAwait(false);
        var approved = proposal?.Status is MergeProposalStatus.Approved or MergeProposalStatus.Merged;
        return new InlineReviewResult(approved, proposal?.VerificationResults);
    }
}
