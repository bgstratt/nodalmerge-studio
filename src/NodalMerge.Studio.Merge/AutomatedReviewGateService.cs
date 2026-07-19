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
    IArtifactCommandService artifactCommands,
    IFileWorkspaceService fileWorkspace,
    IMergeDiffResolver diffResolver) : IAutomatedReviewGateService
{
    public async Task<AutomatedReviewGateResult> TryEnqueueReviewerAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await FindReviewableProposalAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        if (proposal is null)
            return new AutomatedReviewGateResult(AutomatedReviewGateOutcome.NotApplicable);

        // Agent review is enabled when the review POLICY calls for it (AgentApproval/Hybrid) OR an
        // auto-review profile was explicitly configured. The policy arm is the fix: previously this gated
        // ONLY on GetAutoReviewProfileId, so every goal-creation path that didn't send an autoReviewProfileId
        // (MCP nms_v1_goal_run, REST, the extension pre-fix) silently never reviewed AgentApproval goals that
        // depend on this scheduled gate — atomic no-plan goals, which have no reconciliation → inline-review
        // path. The autoReviewProfileId arm preserves agent PRE-review: a HumanRequired goal with a reviewer
        // configured still gets VerificationResults added here, while the human gate decides the apply
        // (AutomatedReviewAsync respects the policy for auto-apply downstream — see AutoReviewRule/Fix 1).
        // Effective policy mirrors AutoReviewRule (the inline gate): real-repo apply → WorkspaceReviewPolicy,
        // otherwise TaskReviewPolicy.
        var autoReviewProfileId = agentControl.GetAutoReviewProfileId(parentWorkUnitId);
        var wu = await workUnits.GetAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        var policy = (WorkspaceReviewScope.AppliesToRealRepo(wu)
            ? wu?.WorkspaceReviewPolicy
            : wu?.TaskReviewPolicy) ?? ReviewPolicy.HumanRequired;
        if (policy is not (ReviewPolicy.AgentApproval or ReviewPolicy.Hybrid)
            && string.IsNullOrWhiteSpace(autoReviewProfileId))
            return new AutomatedReviewGateResult(AutomatedReviewGateOutcome.NotEnabled);

        if (proposal.Status == MergeProposalStatus.UnderReview)
        {
            return new AutomatedReviewGateResult(
                AutomatedReviewGateOutcome.AlreadyEnqueued,
                proposal.ProposalId);
        }

        var creds = agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Review)
            ?? agentControl.GetGoalDefaultCredentials(parentWorkUnitId);

        // Which profile the reviewer runs under: an explicitly-assigned reviewer wins; otherwise walk to
        // the goal's Default profile (inherit-default — a profile is a profile, any role inherits Default),
        // then a bare "reviewer" slot as a last resort so the scheduler item is always well-formed. The
        // reviewer's credentials resolve independently above (Review-stage override, else the goal default),
        // so this id is only the scheduler's profile slot, not the credential source.
        var profileId = autoReviewProfileId
            ?? creds?.ProfileId
            ?? "reviewer";

        var pending = await scheduler.ListPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending.Any(p =>
                p.WorkUnitId == parentWorkUnitId &&
                string.Equals(p.ProfileId, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return new AutomatedReviewGateResult(
                AutomatedReviewGateOutcome.AlreadyEnqueued,
                proposal.ProposalId);
        }

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
                ?? agentControl.GetGoalDefaultCredentials(parentWorkUnitId);

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
                kind: FailureKind.ReviewRejected,
                credentialRef: failedCreds?.CredentialRef,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return new AutomatedRejectionResult(AutomatedRejectionOutcome.EscalatedToDeadLetter);
        }

        // Reset merge/{parentWorkUnitId} back to a clean parent.BranchId mirror before children start
        // re-executing. MergeReconciliationService's own reset (when it eventually reruns) is the
        // authoritative rebuild regardless, but resetting here too closes the window where a retried
        // child's TaskReviewPolicy.AgentApproval/Hybrid auto-apply could land before reconciliation
        // reruns and see the previous (rejected) attempt's stale content as spurious drift.
        await fileWorkspace.ApplyBranchAsync(parent.BranchId, $"merge/{parentWorkUnitId}", cancellationToken)
            .ConfigureAwait(false);

        var children = await workUnits.GetChildrenAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        var creds = agentControl.GetCredentialsForStage(parentWorkUnitId, PipelineStage.Execute)
            ?? agentControl.GetGoalDefaultCredentials(parentWorkUnitId);
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
        RestartMode mode = RestartMode.Revise,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await merge.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal?.Status != MergeProposalStatus.Rejected || proposal.WorkUnitId is null)
            return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);

        var workUnit = await workUnits.GetAsync(proposal.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (workUnit is null)
            return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);

        return await RetryRejectedProposalOwnerAsync(
            proposal, workUnit, automated: false, reviewerAttribution: "human-reviewer",
            reviewNotes, mode, sessionId, cancellationToken).ConfigureAwait(false);
    }

    // A fanned-out task child's own inline reviewer (TaskReviewPolicy.AgentApproval/Hybrid) just
    // rejected that specific child's proposal. Unlike HandleAutomatedRejectionAsync — which retries
    // every child of a parent whose *reconciled batch* proposal was rejected — this retries only
    // the one work unit that owns proposalId. Without this, an AgentApproval/Hybrid child that gets
    // rejected has no automatic (or even manual — MergeProposalTransitions has no outgoing edge
    // from Rejected) path back to Queued, and MergeReconciliationService correctly refuses to fold
    // a Rejected proposal in, so the parent goal would stall in WaitingForChildren forever.
    public async Task<AutomatedRejectionResult> HandleAutomatedTaskRejectionAsync(
        string proposalId,
        string? reviewerAgentId = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = await merge.GetAsync(proposalId, cancellationToken).ConfigureAwait(false);
        if (proposal?.Status != MergeProposalStatus.Rejected || proposal.WorkUnitId is null)
            return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);

        var workUnit = await workUnits.GetAsync(proposal.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (workUnit is null)
            return new AutomatedRejectionResult(AutomatedRejectionOutcome.RetriedWorkers);

        return await RetryRejectedProposalOwnerAsync(
            proposal, workUnit, automated: true, reviewerAttribution: reviewerAgentId ?? "auto-reviewer",
            reviewNotes: proposal.VerificationResults, mode: RestartMode.Revise, sessionId, cancellationToken)
            .ConfigureAwait(false);
    }

    // Shared by HandleHumanRejectionAsync and HandleAutomatedTaskRejectionAsync — both retry the
    // single work unit that owns a rejected proposal (or, if that work unit is itself a reconciled
    // fan-out parent, its Proposed/Merged children). automated selects which of the work unit's two
    // independent rejection-count budgets (AutomatedReviewRejectionCount vs
    // HumanReviewRejectionCount) this cycle is tracked and escalated under.
    private async Task<AutomatedRejectionResult> RetryRejectedProposalOwnerAsync(
        MergeProposal proposal,
        WorkUnit workUnit,
        bool automated,
        string reviewerAttribution,
        string? reviewNotes,
        RestartMode mode,
        string? sessionId,
        CancellationToken cancellationToken)
    {
        var proposalId = proposal.ProposalId;
        var workUnitId = workUnit.WorkUnitId;

        var updatedWorkUnit = await workUnits
            .IncrementReviewRejectionCountAsync(workUnitId, automated, cancellationToken)
            .ConfigureAwait(false);
        var rejectionCount = automated
            ? updatedWorkUnit.ExecutionInfo!.AutomatedReviewRejectionCount
            : updatedWorkUnit.ExecutionInfo!.HumanReviewRejectionCount;

        var creds = agentControl.GetCredentialsForStage(workUnitId, PipelineStage.Execute)
            ?? agentControl.GetGoalDefaultCredentials(workUnitId)
            ?? (workUnit.ParentWorkUnitId is { } executeParentId
                ? agentControl.GetCredentialsForStage(executeParentId, PipelineStage.Execute) ?? agentControl.GetGoalDefaultCredentials(executeParentId)
                : null);

        // The max-attempts cap exists to stop AGENTS from spinning — an automated reviewer
        // rejecting the same work over and over burns tokens with no one steering. A human
        // explicitly asking for another attempt is the opposite situation: they've looked at it,
        // decided to spend more, and supplied their own steering notes. Blocking them behind the
        // cap turned "retry with my correction" into a dead end (live-observed: an Unreject-and-
        // Revise click silently escalated to dead-letter instead of retrying, because earlier
        // automated cycles had already consumed the budget). Humans get warned by the count in the
        // UI; they never get blocked. Only automated cycles escalate here.
        if (automated && rejectionCount >= InMemoryDeadLetterService.MaxFailureAttempts)
        {
            var reason = string.IsNullOrWhiteSpace(reviewNotes)
                ? $"{reviewerAttribution} rejected the proposal."
                : $"{reviewerAttribution} rejected: {reviewNotes}";

            await deadLetter.RecordFailureAsync(
                workUnitId,
                reviewerAttribution,
                PipelineStage.Review,
                "worker",
                reason,
                taskId: proposalId,
                sessionId: sessionId,
                model: creds?.Model,
                baseUrl: creds?.BaseUrl,
                apiKey: creds?.ApiKey,
                provider: creds?.Provider,
                kind: FailureKind.ReviewRejected,
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
                workUnitId, "Constraint",
                automated ? "Automated review feedback" : "Human review feedback",
                reviewNotes,
                ct: cancellationToken).ConfigureAwait(false);
        }

        // Revise: attach a compacted summary of the almost-correct attempt (goal, files touched,
        // truncated diff) so the agent has something to build on instead of re-deriving the change
        // from scratch. Recorded via the artifact-lineage service directly (like MergeProposal
        // artifacts are) rather than IArtifactCommandService.RecordAsync — that path is deliberately
        // gated to the agent-recordable knowledge-note types (Research/Decision/Constraint);
        // RevisionContext is a review-gate-only artifact, not something an agent free-form records.
        // Superseded rather than accumulated across repeat Revise attempts — a prior attempt's own
        // context is stale the moment a newer one exists on top of it.
        if (mode == RestartMode.Revise)
        {
            var existingChain = await artifacts.GetChainAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            foreach (var stale in existingChain.Where(a => a.Type == ArtifactType.RevisionContext))
            {
                await artifacts.UpdateStatusAsync(stale.ArtifactId, ArtifactStatus.Superseded, cancellationToken)
                    .ConfigureAwait(false);
            }

            await artifacts.RecordAsync(
                new ArtifactRef(
                    $"KA-{Guid.NewGuid():N}",
                    ArtifactType.RevisionContext,
                    workUnitId,
                    ArtifactStatus.Active,
                    DateTimeOffset.UtcNow,
                    workUnitId,
                    null,
                    "Prior attempt (revise)",
                    BuildRevisionContextBody(
                        proposal,
                        await diffResolver.ResolveAsync(proposal, cancellationToken).ConfigureAwait(false) ?? string.Empty)),
                cancellationToken).ConfigureAwait(false);
        }

        var children = await workUnits.GetChildrenAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        var hasFanOut = children.Count > 0;
        // A reconciled fan-out proposal retries its Proposed/Merged children; a direct
        // single-worker proposal (no children) retries the work unit itself.
        var retryTargets = hasFanOut
            ? children.Where(c => c.Status is WorkUnitStatus.Proposed or WorkUnitStatus.Merged).ToList()
            : [workUnit];

        if (hasFanOut)
        {
            // Same reset as HandleAutomatedRejectionAsync's parent-level retry, for the same reason —
            // this is the reconciled batch proposal being rejected (workUnit is the top-level goal
            // here, since only a reconciled proposal's owner has children), so merge/{workUnitId} is
            // about to accumulate a fresh attempt underneath whatever the rejected attempt left there.
            await fileWorkspace.ApplyBranchAsync(workUnit.BranchId, $"merge/{workUnitId}", cancellationToken)
                .ConfigureAwait(false);
        }

        foreach (var target in retryTargets)
        {
            // Revert: wipe the target's branch back to its pre-attempt snapshot before requeuing —
            // a genuinely clean slate, no stale diff for the agent to anchor on. The solo (no
            // fan-out) case already knows its own proposalId; fan-out children each propose
            // independently, so their own base/{proposalId} snapshot has to be resolved per child.
            if (mode == RestartMode.Revert)
            {
                var seedProposalId = hasFanOut
                    ? (await artifacts.GetChainAsync(target.WorkUnitId, cancellationToken).ConfigureAwait(false))
                        .LastOrDefault(a => a.Type == ArtifactType.MergeProposal)?.ArtifactId
                    : proposalId;
                var seedBranchId = seedProposalId is not null
                    ? $"base/{seedProposalId}"
                    : target.FanOutInfo?.SeedFromBranchId;
                if (seedBranchId is not null)
                {
                    await fileWorkspace.ApplyBranchAsync(seedBranchId, target.BranchId, cancellationToken)
                        .ConfigureAwait(false);
                }
            }

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

    // Kept deliberately short — this rides along on the retried worker's next AgentWorkspace
    // projection fetch, not a fresh prompt budget of its own. The full diff lives on the proposal
    // itself (fetchable by ProposalId) for anyone who needs it; this is a nudge, not a replay.
    private const int MaxRevisionDiffChars = 1500;

    private static string BuildRevisionContextBody(MergeProposal proposal, string diff)
    {
        var truncatedDiff = diff.Length > MaxRevisionDiffChars
            ? diff[..MaxRevisionDiffChars] + "\n… (truncated)"
            : diff;
        var filesTouched = proposal.FilesTouched.Count > 0
            ? string.Join(", ", proposal.FilesTouched)
            : "(none recorded)";

        return "Prior attempt summary: " + proposal.Summary +
            "\nChange description: " + proposal.ChangeDescription +
            "\nFiles touched: " + filesTouched +
            "\nDiff (may be truncated):\n" + truncatedDiff;
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
