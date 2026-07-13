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
        // Review-stage Model Profile first (the user's explicit "who reviews" choice via Agent
        // Topology — the same tier AutomatedReviewGateService's enqueue path resolves), then the
        // orchestrator registration as the fallback it always was.
        var creds = agentControl.GetCredentialsForStage(workUnitId, PipelineStage.Review)
            ?? agentControl.GetOrchestratorCredentials(workUnitId);

        // When the work unit is a child worker spawned by fan-out, the orchestrator
        // credentials are registered on the parent, not the child. Walk up to find them.
        if (creds is null)
        {
            var workUnits = serviceProvider.GetService<IWorkUnitService>();
            if (workUnits is not null)
            {
                var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
                if (wu?.ParentWorkUnitId is { } parentId)
                {
                    creds = agentControl.GetCredentialsForStage(parentId, PipelineStage.Review)
                        ?? agentControl.GetOrchestratorCredentials(parentId);
                }
            }
        }

        if (creds is null)
            return new InlineReviewResult(false, "No LLM credentials configured for this work unit.");

        var agentId = $"reviewer-auto-{Guid.NewGuid():N}";

        // plans/review-seam-and-clarification-sessions.md S2 — construction moves behind the
        // executor seam: a claude-cli/codex-cli Review provider routes to that CLI adapter
        // (review-request contract in, .workspace/review.json verdict out, harvested onto the same
        // AutomatedReviewAsync call the native nm_v1_merge_review tool makes), anything else
        // degrades to native, whose Mode==Review branch does exactly what this method used to do
        // inline (fetch the proposal for filesTouched/justification, run ReviewerAgentLoop).
        var resolver = serviceProvider.GetRequiredService<IHarnessExecutorResolver>();
        var executor = resolver.ResolveForProvider(creds.Provider, null);
        if (!executor.Capabilities.SupportsReviewMode)
            executor = resolver.Resolve("native");

        // Registers this run in the same agent-visibility registry SpawnAsync-driven loops use
        // (Activity Center / /studio/agents), for the duration of RunAsync only — this run is
        // otherwise invisible since it's awaited synchronously here rather than dispatched through
        // IWorkScheduler like the enqueued reviewer path.
        var completion = await agentControl.TrackInlineAgentAsync(
            agentId, workUnitId, proposalId,
            async onActivity =>
            {
                var request = new HarnessRunRequest(
                    HarnessMode.Review, agentId, workUnitId, proposalId, Profile: null,
                    SessionId: null, IsResume: false, RuleFileContext: null,
                    PromptGuidanceContext: null, SelfVerifyBuild: false, SelfVerifyTest: false,
                    OnActivity: onActivity,
                    Provider: creds.Provider, Model: creds.Model, BaseUrl: creds.BaseUrl, ApiKey: creds.ApiKey);
                var result = await executor.RunAsync(request, ct).ConfigureAwait(false);
                return result.Completion;
            },
            ct).ConfigureAwait(false);

        var proposal = await merge.GetAsync(proposalId, ct).ConfigureAwait(false);
        var approved = proposal?.Status is MergeProposalStatus.Approved or MergeProposalStatus.Merged;

        // The loop can stop without ever calling nm_v1_merge_review (ran out of iterations,
        // cancelled, or parked awaiting a clarification it can't get inline) — that used to look
        // identical to an explicit rejection: AutoReviewRule reported "Reviewer agent rejected the
        // proposal" with no notes, and the proposal just sat at ReadyForReview forever with nothing
        // to explain why (no dead-letter entry, no reviewedBy, no notes). Distinguish it and record
        // it as MaxIterationsExceeded/Stalled so it shows up in /studio/dead-letter instead of
        // vanishing — Continue can resume it with a larger budget instead of a human having to guess.
        var reachedADecision = proposal?.Status is MergeProposalStatus.Approved
            or MergeProposalStatus.Merged or MergeProposalStatus.Rejected;
        if (!approved && !reachedADecision && completion != AgentLoopCompletion.AwaitingClarification)
        {
            var reason = completion == AgentLoopCompletion.MaxIterationsExceeded
                ? "Reviewer agent ran out of iterations without submitting a decision — this is not a rejection, just an inconclusive review."
                : $"Reviewer agent stopped ({completion}) without submitting a decision.";

            var deadLetter = serviceProvider.GetService<IDeadLetterService>();
            if (deadLetter is not null)
            {
                await deadLetter.RecordFailureAsync(
                    workUnitId,
                    agentId,
                    PipelineStage.Review,
                    "reviewer",
                    reason,
                    taskId: proposalId,
                    model: creds.Model,
                    baseUrl: creds.BaseUrl,
                    apiKey: creds.ApiKey,
                    provider: creds.Provider,
                    kind: completion == AgentLoopCompletion.MaxIterationsExceeded
                        ? FailureKind.MaxIterationsExceeded
                        : FailureKind.Stalled,
                    cancellationToken: ct).ConfigureAwait(false);
            }

            await evidenceNodes.RecordAsync(new EvidenceNode(
                EvidenceId: $"ev-{Guid.NewGuid():N}",
                WorkUnitId: workUnitId,
                ProposalId: proposalId,
                Kind: EvidenceKind.AutomatedReview,
                Summary: reason,
                DetailJson: null,
                AttachedAt: DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

            return new InlineReviewResult(false, reason);
        }

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
