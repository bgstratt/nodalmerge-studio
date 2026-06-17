using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge;

public sealed class AutomatedReviewGateService(
    IAgentControlService agentControl,
    IMergeService merge,
    IArtifactLineageService artifacts,
    IWorkScheduler scheduler,
    IWorkUnitService workUnits,
    ITaskService tasks,
    IDeadLetterService deadLetter) : IAutomatedReviewGateService
{
    public async Task<AutomatedReviewGateResult> TryEnqueueReviewerAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var profileId = agentControl.GetAutoReviewProfileId(parentWorkUnitId);
        if (string.IsNullOrWhiteSpace(profileId))
            return new AutomatedReviewGateResult(AutomatedReviewGateOutcome.NotEnabled);

        var proposal = await FindReviewableProposalAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
            return new AutomatedReviewGateResult(AutomatedReviewGateOutcome.NotApplicable);

        if (proposal.Status == MergeProposalStatus.UnderReview)
        {
            return new AutomatedReviewGateResult(
                AutomatedReviewGateOutcome.AlreadyEnqueued,
                proposal.ProposalId);
        }

        var pending = await scheduler.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Any(p =>
                p.WorkUnitId == parentWorkUnitId &&
                string.Equals(p.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return new AutomatedReviewGateResult(
                AutomatedReviewGateOutcome.AlreadyEnqueued,
                proposal.ProposalId);
        }

        var creds = agentControl.GetOrchestratorCredentials(parentWorkUnitId);
        await scheduler.EnqueueAsync(
            parentWorkUnitId,
            profileId,
            taskId: proposal.ProposalId,
            model: creds?.Model,
            baseUrl: creds?.BaseUrl,
            apiKey: creds?.ApiKey,
            provider: creds?.Provider,
            sessionId: sessionId,
            ct: cancellationToken).ConfigureAwait(false);

        return new AutomatedReviewGateResult(
            AutomatedReviewGateOutcome.Enqueued,
            proposal.ProposalId);
    }

    public async Task<AutomatedRejectionResult> HandleAutomatedRejectionAsync(
        string parentWorkUnitId,
        string proposalId,
        string agentId,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await merge.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal?.Status != MergeProposalStatus.Rejected)
            return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);

        var parent = await workUnits.GetAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        if (parent is null)
            return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);

        var previousCount = parent.ExecutionInfo?.AutomatedReviewRejectionCount ?? 0;
        var rejectionCount = previousCount + 1;
        var executionInfo = (parent.ExecutionInfo ?? new WorkUnitExecutionInfo(0, 0)) with
        {
            AutomatedReviewRejectionCount = rejectionCount,
        };
        await workUnits.CreateAsync(parent with { ExecutionInfo = executionInfo }, cancellationToken).ConfigureAwait(false);

        if (rejectionCount >= InMemoryDeadLetterService.MaxFailureAttempts)
        {
            var profileId = agentControl.GetAutoReviewProfileId(parentWorkUnitId) ?? "reviewer";
            var reason = string.IsNullOrWhiteSpace(proposal.VerificationResults)
                ? "Automated review rejected the reconciled proposal."
                : $"Automated review rejected: {proposal.VerificationResults}";

            await deadLetter.RecordFailureAsync(
                parentWorkUnitId,
                agentId,
                PipelineStage.Review,
                profileId,
                reason,
                taskId: proposalId,
                sessionId: sessionId,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new AutomatedRejectionResult(AutomatedRejectionOutcome.EscalatedToDeadLetter);
        }

        var children = await workUnits.GetChildrenAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        var creds = agentControl.GetOrchestratorCredentials(parentWorkUnitId);
        foreach (var child in children)
        {
            if (child.Status is not WorkUnitStatus.Proposed and not WorkUnitStatus.Merged)
                continue;

            try
            {
                await workUnits
                    .UpdateStatusAsync(child.WorkUnitId, WorkUnitStatus.Queued, sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var childTasks = await tasks.ListAsync(child.WorkUnitId, cancellationToken).ConfigureAwait(false);
            var task = childTasks.FirstOrDefault();
            await scheduler.EnqueueAsync(
                child.WorkUnitId,
                "worker",
                taskId: task?.TaskId,
                model: creds?.Model,
                baseUrl: creds?.BaseUrl,
                apiKey: creds?.ApiKey,
                provider: creds?.Provider,
                sessionId: sessionId,
                ct: cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await workUnits
                .UpdateStatusAsync(parentWorkUnitId, WorkUnitStatus.Executing, sessionId, cancellationToken)
                .ConfigureAwait(false);
            await workUnits.SetCurrentStageAsync(parentWorkUnitId, PipelineStage.Execute, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }

        return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);
    }

    private async Task<MergeProposal?> FindReviewableProposalAsync(
        string workUnitId,
        CancellationToken cancellationToken)
    {
        var chain = await artifacts.GetChainAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        foreach (var artifact in chain.Where(a => a.Type == ArtifactType.MergeProposal).Reverse())
        {
            var proposal = await merge.GetAsync(artifact.ArtifactId, cancellationToken).ConfigureAwait(false);
            if (proposal is null)
                continue;

            if (proposal.Status == MergeProposalStatus.ReadyForReview &&
                string.IsNullOrEmpty(proposal.VerificationResults))
            {
                return proposal;
            }
        }

        return (await merge.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p =>
                p.WorkUnitId == workUnitId &&
                p.Status == MergeProposalStatus.ReadyForReview &&
                string.IsNullOrEmpty(p.VerificationResults));
    }
}
