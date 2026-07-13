using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — the harvest block
// (decisions/inbox harvest, merge.propose + merge.validate, the mechanical build/test gate,
// AwaitingClarification pause) extracted out of ClaudeCodeExecutor.RunAsync's private HarvestAsync
// method so a second adapter (CodexCliExecutor) can call the exact same pipeline instead of
// duplicating it. This is C2's "prove the seam isn't Claude-shaped" requirement: every parameter
// below is either already executor-agnostic (branchId, resultText, tokens) or was pulled out of
// what used to be implicit "Name"/"anthropic" literals (executorName, providerName). Behavior for
// claude-code is unchanged byte-for-byte — only the call site moved.
internal sealed class HarnessHarvestPipeline(
    IWorkspaceContractService workspaceContracts,
    IClarificationCommandService clarifications,
    IMergeCommandService mergeCommands,
    ILogger<HarnessHarvestPipeline> logger)
{
    public async Task<HarnessRunResult> HarvestAsync(
        HarnessRunRequest request, string branchId, string? resultText,
        int? inputTokens, int? outputTokens, double? totalCostUsd, string? sessionId,
        string executorName, string providerName, CancellationToken ct)
    {
        await workspaceContracts.HarvestDecisionsAsync(request.WorkUnitId, ct).ConfigureAwait(false);

        var inboxEntries = await workspaceContracts.HarvestInboxAsync(request.WorkUnitId, ct).ConfigureAwait(false);
        if (inboxEntries.Count > 0)
        {
            // Same domain-level parking mechanism a native worker's own nm_v1_clarification_request
            // tool call triggers (ClarificationCommandService.RequestAsync marks the scheduler item
            // AwaitingResume and the work unit Waiting) — no new pause/resume plumbing needed here.
            foreach (var entry in inboxEntries)
            {
                await clarifications.RequestAsync(
                    request.WorkUnitId, entry.Question,
                    requestedByAgentId: request.AgentId, sessionId: request.SessionId,
                    ct: ct).ConfigureAwait(false);
            }

            return new HarnessRunResult(
                AgentLoopCompletion.AwaitingClarification, FailureReason: null,
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        MergeProposal proposal;
        try
        {
            // targetBranch: "main" is a safe default — MergeCommandService.ProposeAsync's own
            // fan-out redirect (merge/{parentWorkUnitId}) overrides this internally for a
            // fanned-out child regardless of what the caller passes, same as the native worker's
            // own nm_v1_merge_propose tool call relies on.
            proposal = await mergeCommands.ProposeAsync(
                sourceBranch: branchId,
                targetBranch: "main",
                summary: resultText ?? $"{executorName} run completed.",
                workUnitId: request.WorkUnitId,
                agentId: request.AgentId,
                model: executorName,
                provider: providerName,
                sessionId: request.SessionId,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Executor}] harvest ProposeAsync failed for workUnitId={WorkUnitId}", executorName, request.WorkUnitId);
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled, $"Harvest failed: {ex.Message}",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        // The mechanical hard gate (WorkspaceExecutionRule, fired inside ProposeAsync itself when
        // WorkspaceOptions.RequireBuildBeforeProposal/RequireTestBeforeProposal are set) rejects the
        // proposal before Draft, before diff, before artifact lineage — this is the real
        // "a build-breaking stub edit is blocked at the gate" guarantee, not the kickoff-contract
        // hint (WorkspaceContractReviewPolicy.SelfVerifyBuildRequired/TestRequired).
        if (proposal.Status == MergeProposalStatus.Rejected)
        {
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled,
                proposal.ChangeDescription ?? "Merge proposal was rejected at the gate.",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        try
        {
            await mergeCommands.ValidateAsync(proposal.ProposalId, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[{Executor}] harvest ValidateAsync failed for workUnitId={WorkUnitId}", executorName, request.WorkUnitId);
            return new HarnessRunResult(
                AgentLoopCompletion.Stalled, $"Proposal validation failed: {ex.Message}",
                resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
        }

        return new HarnessRunResult(
            AgentLoopCompletion.Succeeded, FailureReason: null,
            resultText, inputTokens, outputTokens, totalCostUsd, sessionId);
    }
}
