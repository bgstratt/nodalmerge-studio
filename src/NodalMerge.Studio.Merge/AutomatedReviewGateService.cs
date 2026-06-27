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
    IDeadLetterService deadLetter,
    IArtifactCommandService artifactCommands) : IAutomatedReviewGateService
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

        var creds = agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Review)
            ?? agentControl.GetOrchestratorCredentials(parentWorkUnitId);
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

        var updatedParent = await workUnits
            .IncrementReviewRejectionCountAsync(parentWorkUnitId, automated: true, cancellationToken)
            .ConfigureAwait(false);
        var rejectionCount = updatedParent.ExecutionInfo!.AutomatedReviewRejectionCount;

        if (rejectionCount >= InMemoryDeadLetterService.MaxFailureAttempts)
        {
            var profileId = agentControl.GetAutoReviewProfileId(parentWorkUnitId) ?? "reviewer";
            var reason = string.IsNullOrWhiteSpace(proposal.VerificationResults)
                ? "Automated review rejected the reconciled proposal."
                : $"Automated review rejected: {proposal.VerificationResults}";
            var failedCreds = agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Review)
                ?? agentControl.GetOrchestratorCredentials(parentWorkUnitId);

            await deadLetter.RecordFailureAsync(
                parentWorkUnitId,
                agentId,
                PipelineStage.Review,
                profileId,
                reason,
                taskId: proposalId,
                sessionId: sessionId,
                model: failedCreds?.Model,
                baseUrl: failedCreds?.BaseUrl,
                apiKey: failedCreds?.ApiKey,
                provider: failedCreds?.Provider,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new AutomatedRejectionResult(AutomatedRejectionOutcome.EscalatedToDeadLetter);
        }

        var children = await workUnits.GetChildrenAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        var creds = agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Execute)
            ?? agentControl.GetOrchestratorCredentials(parentWorkUnitId);
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

            var childTasks = await ResetTasksForRetryAsync(child.WorkUnitId, cancellationToken).ConfigureAwait(false);
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

    public async Task<AutomatedRejectionResult> HandleHumanRejectionAsync(
        string proposalId,
        string? reviewNotes,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await merge.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal?.Status != MergeProposalStatus.Rejected || proposal.WorkUnitId is null)
            return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);

        var workUnitId = proposal.WorkUnitId;
        var workUnit = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        if (workUnit is null)
            return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);

        var updatedWorkUnit = await workUnits
            .IncrementReviewRejectionCountAsync(workUnitId, automated: false, cancellationToken)
            .ConfigureAwait(false);
        var rejectionCount = updatedWorkUnit.ExecutionInfo!.HumanReviewRejectionCount;

        var creds = agentControl.GetCredentialsForStage(workUnitId, PipelineStage.Execute)
            ?? agentControl.GetOrchestratorCredentials(workUnitId)
            ?? (workUnit.ParentWorkUnitId is { } executeParentId
                ? agentControl.GetCredentialsForStage(executeParentId, PipelineStage.Execute) ?? agentControl.GetOrchestratorCredentials(executeParentId)
                : null);

        if (rejectionCount >= InMemoryDeadLetterService.MaxFailureAttempts)
        {
            var reason = string.IsNullOrWhiteSpace(reviewNotes)
                ? "Human reviewer rejected the proposal."
                : $"Human reviewer rejected: {reviewNotes}";

            await deadLetter.RecordFailureAsync(
                workUnitId,
                "human-reviewer",
                PipelineStage.Review,
                "worker",
                reason,
                taskId: proposalId,
                sessionId: sessionId,
                model: creds?.Model,
                baseUrl: creds?.BaseUrl,
                apiKey: creds?.ApiKey,
                provider: creds?.Provider,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new AutomatedRejectionResult(AutomatedRejectionOutcome.EscalatedToDeadLetter);
        }

        // The note becomes a Constraint artifact on the rejected work unit so it shows up in every
        // retried child's inherited-constraints projection (ProjectionManager's AgentWorkspace
        // projection walks ParentWorkUnitId) on their very next turn — without this, a worker
        // re-queued after rejection has no idea what was wrong and just repeats the same mistake.
        if (!string.IsNullOrWhiteSpace(reviewNotes))
        {
            await artifactCommands.RecordAsync(
                workUnitId, "Constraint", "Human review feedback", reviewNotes,
                ct: cancellationToken).ConfigureAwait(false);
        }

        var children = await workUnits.GetChildrenAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        var hasFanOut = children.Count > 0;
        // A reconciled fan-out proposal retries its Proposed/Merged children; a direct
        // single-worker proposal (no children) retries the work unit itself.
        var retryTargets = hasFanOut
            ? children.Where(c => c.Status is WorkUnitStatus.Proposed or WorkUnitStatus.Merged).ToList()
            : [workUnit];

        foreach (var target in retryTargets)
        {
            try
            {
                await workUnits
                    .UpdateStatusAsync(target.WorkUnitId, WorkUnitStatus.Queued, sessionId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                continue;
            }

            var targetTasks = await ResetTasksForRetryAsync(target.WorkUnitId, cancellationToken).ConfigureAwait(false);
            var task = targetTasks.FirstOrDefault();
            await scheduler.EnqueueAsync(
                target.WorkUnitId,
                "worker",
                taskId: task?.TaskId,
                model: creds?.Model,
                baseUrl: creds?.BaseUrl,
                apiKey: creds?.ApiKey,
                provider: creds?.Provider,
                sessionId: sessionId,
                ct: cancellationToken).ConfigureAwait(false);
        }

        if (hasFanOut)
        {
            try
            {
                await workUnits
                    .UpdateStatusAsync(workUnitId, WorkUnitStatus.Executing, sessionId, cancellationToken)
                    .ConfigureAwait(false);
                await workUnits.SetCurrentStageAsync(workUnitId, PipelineStage.Execute, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException) { }
        }

        return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);
    }

    // A rejected proposal's underlying task(s) are usually already Completed (the worker that
    // produced the rejected proposal finished its task before proposing). TaskTransitions has no
    // legal exit from Completed, so without this the re-queued worker can't even mark itself
    // InProgress — it just fights the task state machine for a few cycles and gives up. CreateAsync
    // is an unconditional upsert (unlike UpdateAsync, which enforces the transition table), so it's
    // the only way to legitimately reopen a Completed task here.
    private async Task<IReadOnlyList<StudioTask>> ResetTasksForRetryAsync(
        string workUnitId, CancellationToken cancellationToken)
    {
        var existingTasks = await tasks.ListAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        foreach (var t in existingTasks.Where(t => t.Status != NodalMerge.Studio.Contracts.Domain.TaskStatus.Open))
        {
            await tasks.CreateAsync(
                t with { Status = NodalMerge.Studio.Contracts.Domain.TaskStatus.Open, Assignee = null },
                cancellationToken).ConfigureAwait(false);
        }
        return existingTasks;
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
