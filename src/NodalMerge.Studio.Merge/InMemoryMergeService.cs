using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge;

public sealed class InMemoryMergeService : IMergeService, IRehydratable
{
    private readonly ConcurrentDictionary<string, MergeProposal> _proposals = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly WorkspaceOptions _workspaceOptions;
    private readonly IExecutionEventStream _events;
    private readonly IArtifactLineageService _artifacts;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IFileLeaseService? _fileLease;
    private readonly IRepositoryRegistryService? _repositories;
    private readonly IRepositorySyncService? _repositorySync;
    private readonly ICandidateConflictService? _candidateConflicts;
    private readonly ICandidateConflictResolutionService? _candidateConflictResolution;
    private readonly ITaskConflictService? _taskConflicts;

    // IWorkUnitService is resolved lazily (via IServiceProvider) rather than constructor-injected:
    // its production implementation (InMemoryWorkUnitService) already depends on IMergeService
    // (this service), so a direct dependency here would be a circular constructor graph — same
    // pattern used by WorkSchedulerService for the same interface. IWorkScheduler is resolved the
    // same lazy way below (Phase 12's release-and-resume hook) for the identical reason —
    // WorkSchedulerService takes IMergeService directly, so a direct dependency back here would
    // be the same cycle. IFileLeaseService has no such cycle, so it's constructor-injected
    // directly — but kept optional (default null) so the existing direct (non-DI) test
    // constructions don't all need updating; when null, the release-and-resume hook is skipped.
    private IParticipantEventBus? EventBus =>
        _serviceProvider?.GetService(typeof(IParticipantEventBus)) as IParticipantEventBus;

    public InMemoryMergeService(
        IStudioNodeStore nodeStore,
        IFileWorkspaceService fileWorkspace,
        WorkspaceOptions workspaceOptions,
        IExecutionEventStream events,
        IArtifactLineageService artifacts,
        IServiceProvider? serviceProvider = null,
        IFileLeaseService? fileLease = null,
        IRepositoryRegistryService? repositories = null,
        IRepositorySyncService? repositorySync = null,
        ICandidateConflictService? candidateConflicts = null,
        ICandidateConflictResolutionService? candidateConflictResolution = null,
        ITaskConflictService? taskConflicts = null)
    {
        _nodeStore                    = nodeStore;
        _fileWorkspace                = fileWorkspace;
        _workspaceOptions             = workspaceOptions;
        _events                       = events;
        _artifacts                    = artifacts;
        _serviceProvider              = serviceProvider;
        _fileLease                    = fileLease;
        _repositories                 = repositories;
        _repositorySync               = repositorySync;
        _candidateConflicts           = candidateConflicts;
        _candidateConflictResolution  = candidateConflictResolution;
        _taskConflicts                = taskConflicts;
    }

    public async Task<MergeProposal> ProposeAsync(MergeProposal proposal, CancellationToken cancellationToken = default)
    {
        // Status is the caller's to set — normally Draft (MergeCommandService, MergeReconciliationService),
        // but the policy-gate-blocked path deliberately proposes straight into Rejected. Forcing Draft
        // here unconditionally used to silently clobber that back to Draft.
        var stored = proposal;
        _proposals[proposal.ProposalId] = stored;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            stored.ProposalId,
            JsonSerializer.Serialize(stored),
            cancellationToken).ConfigureAwait(false);

        // Snapshot the target branch's current content as this proposal's base state (S0, 10f).
        // Taken now rather than at apply time so it stays correct regardless of whether the
        // proposal later gets approved, rejected, or applied — none of those touch this copy.
        await _fileWorkspace.InitBranchAsync(
            $"base/{proposal.ProposalId}", proposal.TargetBranch, ct: cancellationToken).ConfigureAwait(false);

        return stored;
    }

    public Task<MergeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        _proposals.TryGetValue(proposalId, out var proposal);
        return Task.FromResult(proposal);
    }

    public async Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);

        if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.ReadyForReview))
        {
            throw new InvalidOperationException(
                $"Cannot validate proposal '{proposalId}': status {proposal.Status} cannot transition to ReadyForReview.");
        }

        var updated = proposal with { Status = MergeProposalStatus.ReadyForReview };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<MergeProposal> ReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        string? notes = null,
        CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);

        if (!MergeProposalTransitions.CanTransition(proposal.Status, decision))
        {
            throw new InvalidOperationException(
                $"Cannot transition proposal '{proposalId}' from {proposal.Status} to {decision}. " +
                $"Proposals must be in ReadyForReview before human review.");
        }

        var updated = proposal with { Status = decision, ReviewNotes = notes ?? proposal.ReviewNotes };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        EventBus?.Publish(new ReviewCompletedEvent(
            proposalId, proposal.WorkUnitId,
            decision.ToString(), Automated: false, ReviewerAgentId: null,
            DateTimeOffset.UtcNow));

        if (proposal.WorkUnitId is not null)
        {
            var artifactStatus = decision == MergeProposalStatus.Approved
                ? ArtifactStatus.Approved
                : ArtifactStatus.Rejected;
            await _artifacts.UpdateStatusAsync(proposalId, artifactStatus, cancellationToken).ConfigureAwait(false);
        }

        if (proposal.SessionId is not null)
        {
            if (decision == MergeProposalStatus.Approved)
            {
                var approvedEv = await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.ProposalApproved,
                    new ProposalApprovedPayload(proposalId, "user"),
                    ct: cancellationToken).ConfigureAwait(false);

                await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.MergeApproved,
                    new MergeApprovedPayload(proposalId, "user", DateTimeOffset.UtcNow),
                    causedByEventId: approvedEv.EventId,
                    ct: cancellationToken).ConfigureAwait(false);
            }
            else if (decision == MergeProposalStatus.Rejected)
            {
                await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.ProposalRejected,
                    new ProposalRejectedPayload(proposalId, "user", null),
                    ct: cancellationToken).ConfigureAwait(false);
            }

            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        // Phase 12 — a human rejecting a proposal through this (non-automated) path is a
        // deliberate, final decision with no automatic retry tied to it (unlike
        // AutomatedReviewGateService's rejection-count retry loop, which already releases leases
        // itself via IDeadLetterService.RecordFailureAsync only once it gives up for good —
        // releasing on every one of ITS rejections here too would race an upcoming auto-retry).
        // Nothing else will ever merge this content, so its leases must be released now or they'd
        // strand their wait queues with no recovery path — there's no future event for a
        // human-rejected proposal to hang a release off of otherwise.
        if (decision == MergeProposalStatus.Rejected && proposal.WorkUnitId is not null && _fileLease is not null)
        {
            var promoted = await _fileLease.ForceReleaseAllForWorkUnitAsync(proposal.WorkUnitId, cancellationToken)
                .ConfigureAwait(false);
            var scheduler = _serviceProvider?.GetService(typeof(IWorkScheduler)) as IWorkScheduler;
            if (scheduler is not null)
            {
                foreach (var promotedWorkUnitId in promoted)
                    await scheduler.ClearAwaitingFileLeaseAsync(promotedWorkUnitId, cancellationToken).ConfigureAwait(false);
            }
        }

        if (decision == MergeProposalStatus.Approved)
        {
            await TryRetriggerParentReconciliationAsync(proposal.WorkUnitId, proposal.SessionId, cancellationToken)
                .ConfigureAwait(false);
        }

        return updated;
    }

    // A fanned-out task child just cleared its own TaskReviewPolicy gate (human via ReviewAsync,
    // or the automated reviewer via AutomatedReviewAsync). MergeReconciliationService only ever
    // gets triggered by the scheduler when a sibling finishes executing (WorkSchedulerService) —
    // nothing re-checks the parent once a HumanRequired child is approved later, so without this
    // the batch would stay stuck in WaitingForChildren forever. Best-effort: reconciliation
    // failing must never fail a review that already succeeded.
    private async Task TryRetriggerParentReconciliationAsync(
        string? workUnitId, string? sessionId, CancellationToken cancellationToken)
    {
        if (workUnitId is null)
            return;

        var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
        var workUnit = workUnits is not null
            ? await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false)
            : null;
        if (workUnit?.ParentWorkUnitId is not { } parentWorkUnitId)
            return;

        var reconciliation = _serviceProvider?.GetService(typeof(IMergeReconciliationService)) as IMergeReconciliationService;
        if (reconciliation is null)
            return;

        try
        {
            var result = await reconciliation.TryReconcileAsync(parentWorkUnitId, sessionId, cancellationToken)
                .ConfigureAwait(false);

            // WorkSchedulerService normally chains TryEnqueueReviewerAsync right after
            // TryReconcileAsync at sibling-completion time — but that first attempt happens before
            // any child is Approved, so it always sees WaitingForChildren and never enqueues the
            // automated reviewer. This later, successful reconciliation is the one that actually
            // produces the reconciled proposal, so it has to chain the same enqueue step itself.
            if (result.Outcome == MergeReconciliationOutcome.Reconciled)
            {
                var automatedReview = _serviceProvider?.GetService(typeof(IAutomatedReviewGateService)) as IAutomatedReviewGateService;
                if (automatedReview is not null)
                    await automatedReview.TryEnqueueReviewerAsync(parentWorkUnitId, sessionId, cancellationToken)
                        .ConfigureAwait(false);
            }
        }
        catch { /* best-effort — the review already succeeded regardless of reconciliation outcome */ }
    }

    public async Task<MergeProposal> AutomatedReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        string verificationResults,
        string? reviewerAgentId = null,
        IReadOnlyList<string>? consideredArtifactIds = null,
        CancellationToken cancellationToken = default)
    {
        if (decision is not (MergeProposalStatus.Approved or MergeProposalStatus.Rejected))
        {
            throw new ArgumentException(
                "Automated review decision must be Approved or Rejected.",
                nameof(decision));
        }

        if (string.IsNullOrWhiteSpace(verificationResults))
        {
            throw new ArgumentException(
                "verificationResults is required for automated review.",
                nameof(verificationResults));
        }

        var proposal = GetRequired(proposalId);

        if (proposal.Status == MergeProposalStatus.ReadyForReview)
        {
            if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.UnderReview))
            {
                throw new InvalidOperationException(
                    $"Cannot begin automated review for proposal '{proposalId}' in status {proposal.Status}.");
            }

            proposal = proposal with { Status = MergeProposalStatus.UnderReview };
            _proposals[proposalId] = proposal;
        }
        else if (proposal.Status != MergeProposalStatus.UnderReview)
        {
            throw new InvalidOperationException(
                $"Cannot complete automated review for proposal '{proposalId}' in status {proposal.Status}. " +
                "Proposal must be ReadyForReview or UnderReview.");
        }

        // Slice 11d's original automated pre-gate hands an Approved verdict back to
        // ReadyForReview for a human to give final sign-off. Slice 20b/20c's inline reviewer
        // (AgentApproval/Hybrid) reuses this same method, but for those policies the reviewer's
        // Approved verdict is terminal — no human ever sees it — so it must land on Approved
        // directly, or InlineReviewerService's `proposal.Status is Approved or Merged` check
        // (the signal AutoReviewRule acts on) never becomes true and the proposal stalls forever.
        var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
        var owningWorkUnit = proposal.WorkUnitId is not null && workUnits is not null
            ? await workUnits.GetAsync(proposal.WorkUnitId, cancellationToken).ConfigureAwait(false)
            : null;
        // See WorkspaceReviewScope — work units whose apply can reach the real repo are gated by
        // WorkspaceReviewPolicy; everything else by TaskReviewPolicy.
        var effectiveInlinePolicy = WorkspaceReviewScope.AppliesToRealRepo(owningWorkUnit)
            ? owningWorkUnit?.WorkspaceReviewPolicy
            : owningWorkUnit?.TaskReviewPolicy;
        var isInlineReviewPolicy = effectiveInlinePolicy is ReviewPolicy.AgentApproval or ReviewPolicy.Hybrid;

        var nextStatus = decision == MergeProposalStatus.Approved
            ? (isInlineReviewPolicy ? MergeProposalStatus.Approved : MergeProposalStatus.ReadyForReview)
            : MergeProposalStatus.Rejected;

        if (!MergeProposalTransitions.CanTransition(proposal.Status, nextStatus))
        {
            throw new InvalidOperationException(
                $"Cannot transition proposal '{proposalId}' from {proposal.Status} to {nextStatus}.");
        }

        var updated = proposal with
        {
            Status = nextStatus,
            VerificationResults = verificationResults,
            AgentId = reviewerAgentId ?? proposal.AgentId,
            ConsideredArtifactIds = consideredArtifactIds ?? proposal.ConsideredArtifactIds,
        };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        EventBus?.Publish(new ReviewCompletedEvent(
            proposalId, proposal.WorkUnitId,
            decision.ToString(), Automated: true, ReviewerAgentId: reviewerAgentId,
            DateTimeOffset.UtcNow));

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);

            if (decision == MergeProposalStatus.Rejected)
            {
                await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.ProposalRejected,
                    new ProposalRejectedPayload(proposalId, reviewerAgentId ?? "reviewer", verificationResults),
                    ct: cancellationToken).ConfigureAwait(false);
            }

            if (consideredArtifactIds is { Count: > 0 })
            {
                foreach (var artifactId in consideredArtifactIds)
                {
                    await _events.AppendAsync(
                        proposal.SessionId,
                        proposal.WorkUnitId,
                        ExecutionEventKind.ArtifactConsideredInDecision,
                        new ArtifactConsideredInDecisionPayload(artifactId, proposalId, updated.Status),
                        ct: cancellationToken).ConfigureAwait(false);
                }
            }
        }

        if (nextStatus == MergeProposalStatus.Approved)
        {
            await TryRetriggerParentReconciliationAsync(proposal.WorkUnitId, proposal.SessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        else if (nextStatus == MergeProposalStatus.Rejected && owningWorkUnit?.ParentWorkUnitId is not null)
        {
            // owningWorkUnit.ParentWorkUnitId != null means this proposal belongs to a fan-out child
            // (or an independent repo-linked comparison child), never the top-level/reconciled
            // proposal — that one's own rejection is already handled by WorkSchedulerService's
            // scheduler-driven HandleAutomatedRejectionAsync call. Without this, an AgentApproval/
            // Hybrid child's own inline reviewer rejecting it would be a dead end: no automatic
            // retry, and MergeProposalTransitions has no outgoing edge from Rejected for a human to
            // manually retry it through either.
            await TryRetriggerTaskRejectionAsync(proposalId, reviewerAgentId, proposal.SessionId, cancellationToken)
                .ConfigureAwait(false);
        }

        return updated;
    }

    // Best-effort — a rejection retry failing must never fail the review call that already
    // succeeded. See the Rejected branch of AutomatedReviewAsync above for why this exists.
    private async Task TryRetriggerTaskRejectionAsync(
        string proposalId, string? reviewerAgentId, string? sessionId, CancellationToken cancellationToken)
    {
        var automatedReview = _serviceProvider?.GetService(typeof(IAutomatedReviewGateService)) as IAutomatedReviewGateService;
        if (automatedReview is null)
            return;

        try
        {
            await automatedReview.HandleAutomatedTaskRejectionAsync(proposalId, reviewerAgentId, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch { /* best-effort — the review already succeeded regardless of retry outcome */ }
    }

    public async Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default, bool autoApplied = false)
    {
        var proposal = GetRequired(proposalId);

        if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.Merged))
        {
            throw new InvalidOperationException(
                $"Cannot apply proposal '{proposalId}': only Approved proposals can be merged (current: {proposal.Status}).");
        }

        // Slice 21b — when promotion branch is on, land on the candidate instead of the
        // work unit's parent branch so the canonical workspace is never touched directly.
        // Slice 21c — a work unit can opt out of the session-wide promotion branch via
        // BypassPromotionBranch, applying directly to the proposal's target branch.
        // Also resolved here (rather than only inline) so the write-back step below can use the
        // same owning work unit's RepositoryId instead of always falling back to the global default.
        var bypassPromotionBranch = false;
        WorkUnit? owningWorkUnit = null;
        if (proposal.WorkUnitId is not null)
        {
            var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            if (workUnits is not null)
            {
                owningWorkUnit = await workUnits.GetAsync(proposal.WorkUnitId, cancellationToken).ConfigureAwait(false);
                bypassPromotionBranch = owningWorkUnit?.BypassPromotionBranch ?? false;
            }
        }

        // Scoped to AppliesToRealRepo work units only — a fan-out child's own TargetBranch was
        // already redirected to merge/{parentWorkUnitId} at propose time (MergeCommandService
        // .ProposeAsync) specifically so it never lands on a shared branch prematurely; promotion
        // branch must not override that with an even-more-shared "candidate" branch. Only work
        // units that would otherwise reach "main"/disk directly benefit from the extra staging step.
        var usingPromotionBranch = _workspaceOptions.UsePromotionBranch && !bypassPromotionBranch
            && WorkspaceReviewScope.AppliesToRealRepo(owningWorkUnit);
        var effectiveTarget = usingPromotionBranch
            ? _workspaceOptions.CandidateBranchId
            : proposal.TargetBranch;

        // Defensive seed — CandidateBranchService.EnsureAsync normally does this when
        // UsePromotionBranch is toggled on via the REST config endpoint, but nothing guarantees
        // that ran first (e.g. the option flipped some other way). InitBranchAsync no-ops if the
        // branch already exists+non-empty. Without this, the first-ever apply's own drift check
        // below would compare against a nonexistent/empty candidate and see its own change as
        // "conflicting" with a phantom full deletion — same class of bug the merge/{parentWorkUnitId}
        // seed in MergeCommandService.ProposeAsync exists to avoid.
        if (usingPromotionBranch)
        {
            await _fileWorkspace.InitBranchAsync(effectiveTarget, seedFromBranchId: "main", ct: cancellationToken)
                .ConfigureAwait(false);
        }

        // Land this proposal's own changes additively rather than mirroring its whole branch onto
        // the target (see TryApplyAdditivelyAsync's own comment for why — ApplyBranchAsync's
        // "delete anything the source doesn't have" semantics silently reverts whatever a sibling
        // proposal already landed on this same target, for any file only the sibling touched).
        // Drift-vs-target conflict detection is scoped to fan-out children (ParentWorkUnitId set —
        // siblings landing on their shared parent branch) and, separately, to independent top-level
        // goals landing on the shared candidate branch when promotion is on (usingPromotionBranch —
        // that IS a meaningfully shared target by design, unlike two goals coincidentally both
        // targeting literal "main": confirmed via MultiRepoWriteBackTests, write-back for a
        // repo-backed goal normally sources from the proposal's own SourceBranch directly, never
        // from whatever ends up on the shared target, so a textual "main" collision isn't a real
        // conflict for that case — but candidate's role is different: it's the actual staged
        // end-state PromoteAsync later writes back from, so two goals' changes really do need to
        // compose there).
        var checkForDrift = owningWorkUnit?.ParentWorkUnitId is not null || usingPromotionBranch;
        await TryApplyAdditivelyAsync(
            proposal, effectiveTarget, checkForDrift, owningWorkUnit?.ReconciliationTargetPaths, owningWorkUnit,
            cancellationToken)
            .ConfigureAwait(false);

        // Phase 12 — release-and-resume: this proposal's holder kept a write lease on every file
        // it touched (McpToolDispatcher.CheckFileLeaseAsync) until this exact moment. Now that the
        // files have actually landed in effectiveTarget, advance each path's FIFO queue; if a
        // sibling was waiting, copy the just-merged content into its branch and clear its parked
        // scheduler flag so it resumes (isResume: true, from its own already-elevated AttemptCount)
        // with current content instead of the stale snapshot it was forked from.
        if (_fileLease is not null && proposal.FilesTouched is { Count: > 0 } filesTouched)
        {
            var scheduler = _serviceProvider?.GetService(typeof(IWorkScheduler)) as IWorkScheduler;
            var workUnitsForResume = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;

            foreach (var touchedPath in filesTouched)
            {
                var waiterWorkUnitId = await _fileLease.ReleaseAndAdvanceAsync(touchedPath, cancellationToken)
                    .ConfigureAwait(false);
                if (waiterWorkUnitId is null)
                    continue;

                var waiter = workUnitsForResume is not null
                    ? await workUnitsForResume.GetAsync(waiterWorkUnitId, cancellationToken).ConfigureAwait(false)
                    : null;
                if (waiter is not null)
                {
                    await _fileWorkspace.CopyFilesAsync(
                        effectiveTarget, waiter.BranchId, [touchedPath], cancellationToken).ConfigureAwait(false);
                }

                if (scheduler is not null)
                    await scheduler.ClearAwaitingFileLeaseAsync(waiterWorkUnitId, cancellationToken).ConfigureAwait(false);
            }
        }

        // Write changed files back to disk whenever a repository path is configured — unless
        // UsePromotionBranch is on (and not bypassed), in which case the write is deliberately
        // deferred to PromoteAsync (a human's explicit /promote call). Effective content already
        // landed on "candidate" above (effectiveTarget); real disk isn't touched until promotion,
        // which is the whole point of the candidate-branch staging mechanism — previously this
        // block ran unconditionally here, sourced from proposal.SourceBranch, completely bypassing
        // the candidate redirect and defeating it for any goal with a real repo attached.
        // Hard invariant (independent of the promotion-branch gating above): disk write-back may
        // only ever fire for a work unit whose own apply is allowed to reach the real repo — see
        // WorkspaceReviewScope. Ordinary fanned-out task children and generic experiment forks never
        // get their own RepositoryId, so they stay excluded even if SeedRepositoryPath is globally
        // configured.
        var writeBackPath = await ResolveWriteBackPathAsync(owningWorkUnit, cancellationToken).ConfigureAwait(false);
        var promotedToDisk = false;
        if (!usingPromotionBranch
            && WorkspaceReviewScope.AppliesToRealRepo(owningWorkUnit) && !string.IsNullOrWhiteSpace(writeBackPath))
        {
            await WriteBackToRepositoryAsync(proposal.SourceBranch, writeBackPath, cancellationToken).ConfigureAwait(false);
            await BestEffortResyncAsync(writeBackPath, cancellationToken).ConfigureAwait(false);
            promotedToDisk = true;
        }

        var updated = proposal with
        {
            Status = MergeProposalStatus.Merged,
            AutoApplied = autoApplied,
            PromotedToDisk = promotedToDisk,
            LandedOnCandidateBranch = usingPromotionBranch,
        };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        // A reconciliation work unit's own proposal just landed — generic across every source
        // (see WorkUnit.ReconciliationSourceProposalIds's own doc comment): supersede every
        // proposal it folds together, then resolve any open conflict record (candidate- or
        // task-level) whose own losing ProposalId is one of the ones we just superseded.
        //
        // Deliberately NOT dispatched off ReconciliationSourceRef alone: a losing proposal can be
        // named by more than one conflict record if it was re-applied and re-detected again after
        // the reconciliation work unit was already created (e.g. a stale retry racing the
        // in-flight reconciliation) — SourceRef only ever points at the ONE record this work unit
        // was originally triggered for, so a second record naming the same now-superseded proposal
        // would otherwise be orphaned forever (its "losing proposal" can never win or be retried
        // again once Superseded). Scanning by ProposalId instead resolves every such record in one
        // pass, including — trivially — the original one SourceRef would have named.
        if (owningWorkUnit?.ReconciliationSourceProposalIds is { Count: > 0 } sourceProposalIds)
        {
            foreach (var sourceProposalId in sourceProposalIds)
            {
                if (_proposals.TryGetValue(sourceProposalId, out var sourceProposal)
                    && MergeProposalTransitions.CanTransition(sourceProposal.Status, MergeProposalStatus.Superseded))
                {
                    var supersededProposal = sourceProposal with
                    {
                        Status = MergeProposalStatus.Superseded,
                        SupersededBy = proposalId,
                    };
                    _proposals[sourceProposalId] = supersededProposal;
                    await _nodeStore.WriteNodeAsync(
                        StudioNodeKind.MergeProposalV1,
                        sourceProposalId,
                        JsonSerializer.Serialize(supersededProposal),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (_candidateConflicts is not null)
            {
                var openCandidateConflicts = await _candidateConflicts.GetOpenAsync(cancellationToken).ConfigureAwait(false);
                foreach (var stale in openCandidateConflicts.Where(c => sourceProposalIds.Contains(c.ProposalId)))
                    await _candidateConflicts.MarkResolvedAsync(stale.ConflictId, cancellationToken).ConfigureAwait(false);
            }

            if (_taskConflicts is not null)
            {
                var openTaskConflicts = await _taskConflicts.GetOpenAsync(ct: cancellationToken).ConfigureAwait(false);
                foreach (var stale in openTaskConflicts.Where(c => sourceProposalIds.Contains(c.ProposalId)))
                    await _taskConflicts.MarkResolvedAsync(stale.ConflictId, cancellationToken).ConfigureAwait(false);
            }
        }

        EventBus?.Publish(new MergeAcceptedEvent(
            proposalId, proposal.WorkUnitId,
            proposal.SourceBranch, effectiveTarget,
            DateTimeOffset.UtcNow));

        if (proposal.WorkUnitId is not null)
        {
            await _artifacts.UpdateStatusAsync(proposalId, ArtifactStatus.Applied, cancellationToken).ConfigureAwait(false);
            await _artifacts.RecordAsync(new ArtifactRef(
                $"MR-{Guid.NewGuid():N}",
                ArtifactType.MergeResult,
                proposalId,
                ArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                proposal.WorkUnitId,
                proposal.AgentId), cancellationToken).ConfigureAwait(false);
        }

        // ── Post-merge execution ("system truth" validation) ────────────────
        var execMode = _workspaceOptions.PostMergeExecutionMode;
        if (execMode is "Async" or "Blocking")
        {
            var execution = _serviceProvider?.GetService(typeof(IWorkspaceExecutionService))
                as IWorkspaceExecutionService;
            if (execution is not null)
            {
                var execRequest = new WorkspaceExecutionRequest(
                    Build: true,
                    Test: true,
                    BuildCommand: _workspaceOptions.BuildCommand,
                    TestCommand: _workspaceOptions.TestCommand,
                    TimeoutSeconds: _workspaceOptions.ExecutionTimeoutSeconds);

                if (execMode == "Async")
                {
                    // Fire-and-forget: apply returns immediately, results logged in background.
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await execution.ExecuteAsync(
                                proposal.TargetBranch, execRequest, CancellationToken.None)
                                .ConfigureAwait(false);
                            await _nodeStore.WriteNodeAsync(
                                StudioNodeKind.ExecutionResultV1,
                                $"postmerge/{proposalId}",
                                JsonSerializer.Serialize(result)).ConfigureAwait(false);

                            if (!result.AllSucceeded && proposal.SessionId is not null)
                            {
                                await _events.AppendAsync(
                                    proposal.SessionId,
                                    proposal.WorkUnitId,
                                    ExecutionEventKind.MergeProposalStatusChanged,
                                    new MergeProposalStatusChangedPayload(
                                        proposalId, MergeProposalStatus.Merged, MergeProposalStatus.Merged),
                                    ct: CancellationToken.None).ConfigureAwait(false);
                            }
                        }
                        catch { /* best-effort background task */ }
                    });
                }
                else // Blocking
                {
                    try
                    {
                        var execResult = await execution.ExecuteAsync(
                            proposal.TargetBranch, execRequest, cancellationToken)
                            .ConfigureAwait(false);

                        await _nodeStore.WriteNodeAsync(
                            StudioNodeKind.ExecutionResultV1,
                            $"postmerge/{proposalId}",
                            JsonSerializer.Serialize(execResult),
                            cancellationToken).ConfigureAwait(false);

                        // Blocking mode: failure rolls back the apply.
                        if (!execResult.AllSucceeded)
                        {
                            await _fileWorkspace.ApplyBranchAsync(
                                $"base/{proposalId}", proposal.TargetBranch, CancellationToken.None)
                                .ConfigureAwait(false);

                            var rolledBack = updated with { Status = MergeProposalStatus.Rejected };
                            _proposals[proposalId] = rolledBack;
                            await _nodeStore.WriteNodeAsync(
                                StudioNodeKind.MergeProposalV1, proposalId,
                                JsonSerializer.Serialize(rolledBack),
                                CancellationToken.None).ConfigureAwait(false);

                            throw new InvalidOperationException(
                                $"Post-merge execution failed on target branch '{proposal.TargetBranch}'. " +
                                $"Apply rolled back. Builds: {execResult.Builds.Count(b => !b.Success)} failed. " +
                                $"Tests: {execResult.Tests.Sum(t => t.Failed)} of {execResult.Tests.Sum(t => t.TotalTests)} failed.");
                        }
                    }
                    catch when (execMode != "Blocking") { /* unreachable */ }
                }
            }
        }

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeApplied,
                new MergeAppliedPayload(proposalId, proposal.TargetBranch, string.Empty),
                ct: cancellationToken).ConfigureAwait(false);

            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        if (proposal.WorkUnitId is not null)
        {
            // Best-effort — a proposal applying means the owning work unit's pipeline is done.
            // Not worth failing the merge apply over an illegal transition (e.g. the legacy
            // direct-spawn path never reaches WorkUnitStatus.Proposed).
            var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            if (workUnits is not null)
            {
                try
                {
                    var mergedUnit = await workUnits.GetAsync(proposal.WorkUnitId, cancellationToken).ConfigureAwait(false);
                    await workUnits.UpdateStatusAsync(
                        proposal.WorkUnitId, WorkUnitStatus.Merged, proposal.SessionId, cancellationToken).ConfigureAwait(false);
                    await workUnits.SetCurrentStageAsync(proposal.WorkUnitId, null, cancellationToken).ConfigureAwait(false);

                    // Phase 12 — IsReadyToEnqueueAsync now gates a dependent on its dependency
                    // reaching Merged, not Proposed. The existing trigger for
                    // TryEnqueueReadyDependentsAsync fires at worker-completion time (Proposed),
                    // which is too early for a dependent under the tighter gate; this is the
                    // complementary trigger for the gate's later threshold. Reconciliation doesn't
                    // need an equivalent merge-time call — it already tolerates a Merged child
                    // (MergeReconciliationService.cs:48) and is re-checked whenever the *other*
                    // sibling's own worker later completes.
                    if (mergedUnit?.ParentWorkUnitId is { } parentWorkUnitId)
                    {
                        var fanOut = _serviceProvider?.GetService(typeof(IFanOutService)) as IFanOutService;
                        if (fanOut is not null)
                        {
                            await fanOut
                                .TryEnqueueReadyDependentsAsync(parentWorkUnitId, proposal.SessionId, cancellationToken)
                                .ConfigureAwait(false);
                        }
                    }
                }
                catch (InvalidOperationException) { }
                catch (KeyNotFoundException) { }
            }
        }

        return updated;
    }

    public async Task<PromoteResult> PromoteAsync(CancellationToken cancellationToken = default)
    {
        var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;

        // Every proposal that landed on candidate (Merged) but hasn't been promoted yet. candidate
        // is a single session-wide branch, so this spans every goal that used it, not just one.
        // Excludes any proposal whose owning work unit never applies to the real repo (fan-out
        // children etc.) — those never target candidate in the first place (see ApplyAsync's
        // usingPromotionBranch gating); excluded here too, defensively.
        var pending = new List<(MergeProposal Proposal, WorkUnit? OwningWorkUnit)>();
        foreach (var proposal in _proposals.Values)
        {
            if (proposal.Status != MergeProposalStatus.Merged || proposal.PromotedToDisk
                || !proposal.LandedOnCandidateBranch)
                continue;

            WorkUnit? owningWorkUnit = proposal.WorkUnitId is not null && workUnits is not null
                ? await workUnits.GetAsync(proposal.WorkUnitId, cancellationToken).ConfigureAwait(false)
                : null;
            if (!WorkspaceReviewScope.AppliesToRealRepo(owningWorkUnit))
                continue;

            pending.Add((proposal, owningWorkUnit));
        }

        if (pending.Count == 0)
            return new PromoteResult(true, []);

        // Opt-in build/test gate against the composed candidate branch itself, before touching
        // anything. Reuses the same flags ProposeAsync's own pre-diff gate uses (Slice 16g) — no
        // new WorkspaceOptions needed.
        if (_workspaceOptions.RequireBuildBeforeProposal || _workspaceOptions.RequireTestBeforeProposal)
        {
            var execution = _serviceProvider?.GetService(typeof(IWorkspaceExecutionService)) as IWorkspaceExecutionService;
            if (execution is not null)
            {
                var execRequest = new WorkspaceExecutionRequest(
                    Build: _workspaceOptions.RequireBuildBeforeProposal,
                    Test: _workspaceOptions.RequireTestBeforeProposal,
                    BuildCommand: _workspaceOptions.BuildCommand,
                    TestCommand: _workspaceOptions.TestCommand,
                    TimeoutSeconds: _workspaceOptions.ExecutionTimeoutSeconds);
                var execResult = await execution
                    .ExecuteAsync(_workspaceOptions.CandidateBranchId, execRequest, cancellationToken)
                    .ConfigureAwait(false);

                if (!execResult.AllSucceeded)
                {
                    return new PromoteResult(false, [],
                        $"Build/test on '{_workspaceOptions.CandidateBranchId}' failed — nothing promoted. " +
                        $"Builds failed: {execResult.Builds.Count(b => !b.Success)}. " +
                        $"Tests failed: {execResult.Tests.Sum(t => t.Failed)} of {execResult.Tests.Sum(t => t.TotalTests)}.");
                }
            }
        }

        // Advance the workspace branch itself — candidate becomes the new main. A destructive full
        // mirror is correct here (unlike the sibling-apply case TryApplyAdditivelyAsync guards
        // against): candidate is a single accumulated end-state being promoted wholesale, not one
        // of several proposals landing on a shared target one at a time.
        await _fileWorkspace.ApplyBranchAsync(_workspaceOptions.CandidateBranchId, "main", cancellationToken)
            .ConfigureAwait(false);

        // Group by resolved write-back path (multi-repo support) so each distinct real repo gets
        // exactly one write-back call sourced from candidate's current composed content, not one
        // per proposal (candidate already additively contains every landed proposal's changes).
        var byWriteBackPath = new Dictionary<string, List<MergeProposal>>(StringComparer.Ordinal);
        foreach (var (proposal, owningWorkUnit) in pending)
        {
            var writeBackPath = await ResolveWriteBackPathAsync(owningWorkUnit, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(writeBackPath))
                continue; // workspace-only session (no repo attached) — the branch mirror above is the whole promotion

            if (!byWriteBackPath.TryGetValue(writeBackPath, out var list))
                byWriteBackPath[writeBackPath] = list = [];
            list.Add(proposal);
        }

        foreach (var (writeBackPath, _) in byWriteBackPath)
        {
            await WriteBackToRepositoryAsync(_workspaceOptions.CandidateBranchId, writeBackPath, cancellationToken)
                .ConfigureAwait(false);
            await BestEffortResyncAsync(writeBackPath, cancellationToken).ConfigureAwait(false);
        }

        // Every pending proposal is now promoted, whether or not it individually resolved a real
        // writeBackPath — a workspace-only proposal still "promotes" via the branch mirror above.
        var promoted = new List<MergeProposal>();
        foreach (var (proposal, _) in pending)
        {
            var updated = proposal with { PromotedToDisk = true };
            _proposals[proposal.ProposalId] = updated;
            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.MergeProposalV1,
                proposal.ProposalId,
                JsonSerializer.Serialize(updated),
                cancellationToken).ConfigureAwait(false);
            promoted.Add(updated);
        }

        return new PromoteResult(true, promoted);
    }

    // Found via a live multi-sibling run (see plans/orchestrator-reliability-and-observability.md
    // Phase 4 item 3): landing a proposal via the old ApplyBranchAsync (full mirror — deletes
    // anything in the target absent from the source) silently reverted an already-merged
    // sibling's changes for any file only that sibling had touched, because neither sibling's
    // branch was ever refreshed with the other's changes (only *declared dependencies* get that
    // treatment, via FanOutService.RefreshBranchFromDependenciesAsync — independent siblings never
    // do). This lands only the files THIS proposal actually changed (add/modify by copying,
    // delete only what it explicitly deleted), and additionally detects the case where the target
    // has drifted since this proposal's own base/{proposalId} snapshot in a way that genuinely
    // overlaps this proposal's own changed lines — reusing the exact same line-range-overlap
    // primitive MergeReconciliationService already uses for sibling-vs-sibling conflict detection,
    // just pointed at base-vs-current-target instead of proposal-vs-proposal. Both comparisons
    // share the same "before" side (base/{proposalId}), so the resulting ranges are directly
    // comparable — no coordinate translation needed.
    //
    // Deliberately conservative for the first pass: on a genuine overlap, this throws rather than
    // attempting any automatic rebase/resolution — a human resolves it (the diff editor already
    // wired to GET /studio/merges/{id}/file-changes shows exactly what this proposal changed;
    // the conflict report added here shows what changed underneath it since).
    //
    // checkForDrift is the caller's call on whether "target" is a meaningfully shared concept for
    // this proposal (true for a fan-out child landing on its parent's own branch — the actual bug
    // scenario) — additive apply itself (never destructively mirroring) always happens regardless.
    private async Task TryApplyAdditivelyAsync(
        MergeProposal proposal, string effectiveTarget, bool checkForDrift,
        IReadOnlyList<string>? reconciliationTargetPaths, WorkUnit? owningWorkUnit, CancellationToken ct)
    {
        var baseBranch = $"base/{proposal.ProposalId}";
        var filesToScan = proposal.FilesTouched.Count > 0
            ? proposal.FilesTouched
            : await _fileWorkspace.ListAsync(proposal.SourceBranch, ct: ct).ConfigureAwait(false);

        var toCopy = new List<string>();
        var toDelete = new List<string>();
        var conflicts = new List<string>();

        foreach (var path in filesToScan)
        {
            var baseContent = await _fileWorkspace.ReadAsync(baseBranch, path, ct).ConfigureAwait(false);
            var proposalContent = await _fileWorkspace.ReadAsync(proposal.SourceBranch, path, ct).ConfigureAwait(false);

            if (baseContent == proposalContent)
                continue; // this proposal never actually changed this path — nothing to land or check

            // A reconciliation work unit's own apply is authoritative for exactly the paths it was
            // created to resolve — it was seeded from this same target branch specifically to
            // combine both sides, so re-running the drift check against it here would just
            // re-detect the same conflict it exists to fix. Bypass drift entirely for these paths,
            // regardless of which subsystem originally flagged them (IReconciliationAgentService).
            if (checkForDrift && reconciliationTargetPaths is { Count: > 0 } && reconciliationTargetPaths.Contains(path))
            {
                if (proposalContent is null)
                    toDelete.Add(path);
                else
                    toCopy.Add(path);
                continue;
            }

            if (checkForDrift)
            {
                var targetContent = await _fileWorkspace.ReadAsync(effectiveTarget, path, ct).ConfigureAwait(false);
                var proposalRanges = LineRangeConflictDetector.ComputeChangedRanges(baseContent, proposalContent);
                var driftRanges = LineRangeConflictDetector.ComputeChangedRanges(baseContent, targetContent);

                if (driftRanges.Count > 0 && proposalRanges.Count > 0 &&
                    LineRangeConflictDetector.RangesOverlap(proposalRanges, driftRanges))
                {
                    // Candidate-branch case specifically (independent goals, not fan-out siblings —
                    // those keep their existing tested report-and-block behavior unchanged): attempt
                    // automated per-file resolution before giving up. targetContent here is what's
                    // already landed on candidate (the "winner" so far); proposalContent is the
                    // incoming change.
                    if (_candidateConflictResolution is not null
                        && string.Equals(effectiveTarget, _workspaceOptions.CandidateBranchId, StringComparison.Ordinal))
                    {
                        var resolution = await _candidateConflictResolution
                            .TryResolveAsync(path, baseContent, targetContent, proposalContent, ct)
                            .ConfigureAwait(false);
                        if (resolution.Resolved)
                        {
                            await _fileWorkspace.WriteAsync(effectiveTarget, path, resolution.MergedContent!, ct)
                                .ConfigureAwait(false);
                            continue; // resolved and already written — not a toCopy candidate either
                        }
                    }

                    conflicts.Add(path);
                    continue;
                }
            }

            if (proposalContent is null)
                toDelete.Add(path);
            else
                toCopy.Add(path);
        }

        if (conflicts.Count > 0)
        {
            var report =
                $"# Merge conflict report\n\n" +
                $"Proposal '{proposal.ProposalId}' conflicts with changes already on '{effectiveTarget}' " +
                "that landed after this proposal's own branch was created. Resolve manually (open each " +
                "file's diff, e.g. via GET /studio/merges/{proposalId}/file-changes, to see both sides) " +
                "or re-propose from the current target state.\n\n" +
                string.Join("\n", conflicts.Select(f => $"## {f}"));
            await _fileWorkspace
                .WriteAsync(proposal.SourceBranch, MergeReconciliationService.ConflictReportFileName, report, ct)
                .ConfigureAwait(false);

            // Candidate-branch case specifically (independent goals, not fan-out siblings): also
            // persist a queryable record, not just the report file + thrown exception, so the
            // promote UI can list open conflicts without having to react to a failed apply call it
            // happened to make.
            if (_candidateConflicts is not null
                && string.Equals(effectiveTarget, _workspaceOptions.CandidateBranchId, StringComparison.Ordinal))
            {
                // Best-effort — the already-landed proposal whose content is currently on candidate
                // for these paths, so IReconciliationAgentService has both sides to work from.
                var winningProposal = _proposals.Values.FirstOrDefault(p =>
                    p.ProposalId != proposal.ProposalId
                    && p.Status == MergeProposalStatus.Merged
                    && p.LandedOnCandidateBranch
                    && p.FilesTouched.Intersect(conflicts).Any());

                var conflictRecord = new CandidateConflictRecord(
                    $"candconf-{Guid.NewGuid():N}",
                    proposal.ProposalId,
                    proposal.WorkUnitId,
                    conflicts,
                    DateTimeOffset.UtcNow,
                    WinningProposalId: winningProposal?.ProposalId);
                await _candidateConflicts.RecordAsync(conflictRecord, ct).ConfigureAwait(false);

                await TryAutoTriggerReconciliationAsync(conflictRecord, ct).ConfigureAwait(false);
            }

            // Fan-out-sibling case: this proposal's own apply landed on merge/{ParentWorkUnitId}
            // (checkForDrift is true here because owningWorkUnit.ParentWorkUnitId is set, not
            // because of UsePromotionBranch) and collided with a sibling that already landed there.
            // Same queryable-record treatment as the candidate case above.
            if (_taskConflicts is not null && owningWorkUnit?.ParentWorkUnitId is { } parentWorkUnitId
                && string.Equals(effectiveTarget, $"merge/{parentWorkUnitId}", StringComparison.Ordinal))
            {
                var winningSibling = _proposals.Values.FirstOrDefault(p =>
                    p.ProposalId != proposal.ProposalId
                    && p.Status == MergeProposalStatus.Merged
                    && p.TargetBranch == effectiveTarget
                    && p.FilesTouched.Intersect(conflicts).Any());

                var taskConflictRecord = new TaskConflictRecord(
                    $"taskconf-{Guid.NewGuid():N}",
                    parentWorkUnitId,
                    proposal.ProposalId,
                    proposal.WorkUnitId,
                    conflicts,
                    DateTimeOffset.UtcNow,
                    WinningProposalId: winningSibling?.ProposalId);
                await _taskConflicts.RecordAsync(taskConflictRecord, ct).ConfigureAwait(false);

                await TryAutoTriggerTaskReconciliationAsync(taskConflictRecord, ct).ConfigureAwait(false);
            }

            throw new InvalidOperationException(
                $"Cannot apply proposal '{proposal.ProposalId}': it conflicts with changes already on " +
                $"'{effectiveTarget}' for file(s): {string.Join(", ", conflicts)}. See " +
                $"{MergeReconciliationService.ConflictReportFileName} on the proposal's own branch " +
                "('" + proposal.SourceBranch + "') for details.");
        }

        foreach (var path in toDelete)
            await _fileWorkspace.DeleteAsync(effectiveTarget, path, ct).ConfigureAwait(false);

        if (toCopy.Count > 0)
            await _fileWorkspace.CopyFilesAsync(proposal.SourceBranch, effectiveTarget, toCopy, ct).ConfigureAwait(false);
    }

    // Auto-trigger a reconciliation work unit without waiting for a human to notice, but only when
    // no human checkpoint exists anywhere between either goal and candidate — i.e. both the losing
    // and winning work units are fully AgentApproval. Hybrid still gets its own timeout-based human
    // checkpoint elsewhere in that goal's lifecycle, so it's deliberately excluded here; a human can
    // always trigger reconciliation manually regardless of policy (see the REST endpoint).
    // Best-effort — same shape as TryRetriggerTaskRejectionAsync: a trigger failure must never fail
    // the apply call (and its already-correct conflict recording) that led here.
    private async Task TryAutoTriggerReconciliationAsync(CandidateConflictRecord conflict, CancellationToken ct)
    {
        try
        {
            var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            var trigger = _serviceProvider?.GetService(typeof(ICandidateReconciliationTrigger)) as ICandidateReconciliationTrigger;
            if (workUnits is null || trigger is null)
                return;

            var losingWorkUnit = conflict.WorkUnitId is not null
                ? await workUnits.GetAsync(conflict.WorkUnitId, ct).ConfigureAwait(false)
                : null;
            var winningProposal = conflict.WinningProposalId is not null
                ? await GetAsync(conflict.WinningProposalId, ct).ConfigureAwait(false)
                : null;
            var winningWorkUnit = winningProposal?.WorkUnitId is not null
                ? await workUnits.GetAsync(winningProposal.WorkUnitId, ct).ConfigureAwait(false)
                : null;

            if (losingWorkUnit?.WorkspaceReviewPolicy == ReviewPolicy.AgentApproval
                && winningWorkUnit?.WorkspaceReviewPolicy == ReviewPolicy.AgentApproval)
            {
                await trigger.TryTriggerAsync(conflict.ConflictId, ct: ct).ConfigureAwait(false);
            }
        }
        catch { /* best-effort — a human can still trigger reconciliation manually */ }
    }

    // Mirrors TryAutoTriggerReconciliationAsync exactly, gated on TaskReviewPolicy instead of
    // WorkspaceReviewPolicy — siblings are gated by TaskReviewPolicy (WorkspaceReviewScope), not
    // WorkspaceReviewPolicy, which only ever governs a top-level goal's own real-repo apply.
    private async Task TryAutoTriggerTaskReconciliationAsync(TaskConflictRecord conflict, CancellationToken ct)
    {
        try
        {
            var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            var trigger = _serviceProvider?.GetService(typeof(ITaskReconciliationTrigger)) as ITaskReconciliationTrigger;
            if (workUnits is null || trigger is null)
                return;

            var losingWorkUnit = conflict.WorkUnitId is not null
                ? await workUnits.GetAsync(conflict.WorkUnitId, ct).ConfigureAwait(false)
                : null;
            var winningProposal = conflict.WinningProposalId is not null
                ? await GetAsync(conflict.WinningProposalId, ct).ConfigureAwait(false)
                : null;
            var winningWorkUnit = winningProposal?.WorkUnitId is not null
                ? await workUnits.GetAsync(winningProposal.WorkUnitId, ct).ConfigureAwait(false)
                : null;

            if (losingWorkUnit?.TaskReviewPolicy == ReviewPolicy.AgentApproval
                && winningWorkUnit?.TaskReviewPolicy == ReviewPolicy.AgentApproval)
            {
                await trigger.TryTriggerAsync(conflict.ConflictId, ct: ct).ConfigureAwait(false);
            }
        }
        catch { /* best-effort — a human can still trigger reconciliation manually */ }
    }

    private async Task WriteBackToRepositoryAsync(string sourceBranchId, string repoPath, CancellationToken ct)
    {
        var files = await _fileWorkspace.ListAsync(sourceBranchId, ct: ct).ConfigureAwait(false);
        foreach (var relativePath in files)
        {
            var content = await _fileWorkspace.ReadAsync(sourceBranchId, relativePath, ct).ConfigureAwait(false);
            if (content is null) continue;
            var dest = Path.Combine(repoPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var destDir = Path.GetDirectoryName(dest)!;
            if (!Directory.Exists(destDir))
                Directory.CreateDirectory(destDir);
            await File.WriteAllTextAsync(dest, content, ct).ConfigureAwait(false);
        }
    }

    // Prefer the owning work unit's own repository — so a multi-repo goal writes back to the repo
    // it actually came from — falling back to the global default only when the work unit has no
    // RepositoryId (preserves single-repo behavior unchanged). Shared by ApplyAsync and PromoteAsync.
    private async Task<string?> ResolveWriteBackPathAsync(WorkUnit? owningWorkUnit, CancellationToken ct)
    {
        var writeBackPath = _workspaceOptions.SeedRepositoryPath;
        if (owningWorkUnit?.RepositoryId is { } repositoryId && _repositories is not null)
        {
            var repository = await _repositories.GetAsync(repositoryId, ct).ConfigureAwait(false);
            if (repository is not null)
                writeBackPath = repository.Path;
        }

        return writeBackPath;
    }

    // Best-effort CAS/snapshot audit-trail refresh — scoped to the global default repo
    // specifically, because "main"'s on-disk branch directory mirrors that repo. A multi-repo work
    // unit's own writeBackPath points at a *different* physical repo, and syncing "main" against it
    // would diff the wrong pairing. A failed or skipped resync must never affect the write-back
    // itself, which already succeeded.
    private async Task BestEffortResyncAsync(string writeBackPath, CancellationToken ct)
    {
        if (_repositorySync is null
            || !string.Equals(writeBackPath, _workspaceOptions.SeedRepositoryPath, StringComparison.Ordinal))
            return;

        try
        {
            await _repositorySync.SyncBranchFromRepositoryAsync(
                "main", writeBackPath, SyncTrigger.PostMergeWriteBack, ct).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort audit-trail refresh — the write-back already succeeded; a resync failure
            // must never fail or roll back an already-completed apply/promote.
        }
    }

    public Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken cancellationToken = default)
    {
        var items = _proposals.Values
            .Where(p => sourceBranch is null || p.SourceBranch == sourceBranch)
            .ToList();
        return Task.FromResult<IReadOnlyList<MergeProposal>>(items);
    }

    public async Task<MergeProposal> SupersedeAsync(
        string proposalId,
        string supersededByProposalId,
        CancellationToken cancellationToken = default)
    {
        var proposal = GetRequired(proposalId);

        if (!MergeProposalTransitions.CanTransition(proposal.Status, MergeProposalStatus.Superseded))
        {
            throw new InvalidOperationException(
                $"Cannot supersede proposal '{proposalId}': status {proposal.Status} cannot transition to Superseded.");
        }

        var updated = proposal with
        {
            Status = MergeProposalStatus.Superseded,
            SupersededBy = supersededByProposalId,
        };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (proposal.WorkUnitId is not null)
            await _artifacts.UpdateStatusAsync(proposalId, ArtifactStatus.Superseded, cancellationToken).ConfigureAwait(false);

        if (proposal.SessionId is not null)
        {
            await _events.AppendAsync(
                proposal.SessionId,
                proposal.WorkUnitId,
                ExecutionEventKind.MergeProposalStatusChanged,
                new MergeProposalStatusChangedPayload(proposalId, proposal.Status, updated.Status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.MergeProposalV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var proposal = JsonSerializer.Deserialize<MergeProposal>(payloadJson);
            if (proposal is not null)
                _proposals[entityId] = proposal;
        }
    }

    private MergeProposal GetRequired(string proposalId)
    {
        if (!_proposals.TryGetValue(proposalId, out var proposal))
            throw new KeyNotFoundException($"Merge proposal '{proposalId}' was not found.");
        return proposal;
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioMerge(this IServiceCollection services)
    {
        // IStudioNodeStore must be registered before this (AddStudioStorage)
        services.AddSingleton<InMemoryMergeService>();
        services.AddSingleton<IMergeService>(sp => sp.GetRequiredService<InMemoryMergeService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryMergeService>());
        services.AddSingleton<IMergeCommandService, MergeCommandService>();
        services.AddSingleton<IProposalReviewService, ProposalReviewService>();
        services.AddSingleton<IMergeReconciliationService, MergeReconciliationService>();
        services.AddSingleton<IAutomatedReviewGateService, AutomatedReviewGateService>();
        // Slice 20b — BeforeMerge policy rule for AgentApproval/Hybrid policies.
        services.AddSingleton<IPolicyRule, AutoReviewRule>();
        // Slice 20c — Hybrid countdown timer.
        services.AddSingleton<IReviewTimerService, ReviewTimerService>();

        // Phase 10 — merge strategy chain. Strategies are tried in registration order.
        services.AddSingleton<ThreeWayMergeStrategy>();
        services.AddSingleton<AstMergeStrategy>();
        services.AddSingleton<LlmAssistedMergeStrategy>();
        services.AddSingleton<HumanReviewStrategy>();
        services.AddSingleton<IMergeStrategy>(sp => sp.GetRequiredService<ThreeWayMergeStrategy>());
        services.AddSingleton<IMergeStrategy>(sp => sp.GetRequiredService<AstMergeStrategy>());
        services.AddSingleton<IMergeStrategy>(sp => sp.GetRequiredService<LlmAssistedMergeStrategy>());
        services.AddSingleton<IMergeStrategy>(sp => sp.GetRequiredService<HumanReviewStrategy>());
        services.AddSingleton<IConflictResolutionService, ConflictResolutionService>();
        // Reuses the same IMergeStrategy chain directly for candidate-branch cross-goal conflicts —
        // see CandidateConflictResolutionService's own doc comment.
        services.AddSingleton<ICandidateConflictResolutionService, CandidateConflictResolutionService>();
        // Source-agnostic reconciliation-agent core (see IReconciliationAgentService's doc comment)
        // plus its two adapters: candidate-branch (independent top-level goals) and task-level
        // (fan-out siblings under the same goal).
        services.AddSingleton<IReconciliationAgentService, ReconciliationAgentService>();
        services.AddSingleton<ICandidateReconciliationTrigger, CandidateReconciliationTrigger>();
        services.AddSingleton<ITaskReconciliationTrigger, TaskReconciliationTrigger>();
        return services;
    }
}
