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
        IRepositorySyncService? repositorySync = null)
    {
        _nodeStore        = nodeStore;
        _fileWorkspace    = fileWorkspace;
        _workspaceOptions = workspaceOptions;
        _events           = events;
        _artifacts        = artifacts;
        _serviceProvider  = serviceProvider;
        _fileLease        = fileLease;
        _repositories     = repositories;
        _repositorySync   = repositorySync;
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

        return updated;
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
        var isInlineReviewPolicy = owningWorkUnit?.ReviewPolicy is ReviewPolicy.AgentApproval or ReviewPolicy.Hybrid;

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

        return updated;
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

        var effectiveTarget = _workspaceOptions.UsePromotionBranch && !bypassPromotionBranch
            ? _workspaceOptions.CandidateBranchId
            : proposal.TargetBranch;

        // Land this proposal's own changes additively rather than mirroring its whole branch onto
        // the target (see TryApplyAdditivelyAsync's own comment for why — ApplyBranchAsync's
        // "delete anything the source doesn't have" semantics silently reverts whatever a sibling
        // proposal already landed on this same target, for any file only the sibling touched).
        // Drift-vs-target conflict detection is scoped to fan-out children specifically
        // (ParentWorkUnitId set) — that's the actual scenario the bug is in (siblings landing on
        // their shared parent branch). Two independent top-level goals that happen to both target
        // the literal branch name "main" (e.g. distinct single-repo goals sharing the default
        // convention) aren't "siblings" in any meaningful sense, and — confirmed via
        // MultiRepoWriteBackTests — write-back for a repo-backed goal always sources from the
        // proposal's own SourceBranch directly, never from whatever ends up on the shared target,
        // so a textual collision there isn't a real conflict for that case.
        var checkForDrift = owningWorkUnit?.ParentWorkUnitId is not null;
        await TryApplyAdditivelyAsync(proposal, effectiveTarget, checkForDrift, cancellationToken).ConfigureAwait(false);

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

        // Write changed files back to disk whenever a repository path is configured. Prefer the
        // owning work unit's own repository — so a multi-repo goal writes back to the repo it
        // actually came from — falling back to the global default only when the work unit has no
        // RepositoryId (preserves today's single-repo behavior unchanged).
        var writeBackPath = _workspaceOptions.SeedRepositoryPath;
        if (owningWorkUnit?.RepositoryId is { } repositoryId && _repositories is not null)
        {
            var repository = await _repositories.GetAsync(repositoryId, cancellationToken).ConfigureAwait(false);
            if (repository is not null)
                writeBackPath = repository.Path;
        }

        if (!string.IsNullOrWhiteSpace(writeBackPath))
        {
            await WriteBackToRepositoryAsync(proposal.SourceBranch, writeBackPath, cancellationToken).ConfigureAwait(false);

            // Best-effort CAS/snapshot audit-trail refresh — scoped to the global default repo
            // specifically, because "main"'s on-disk branch directory mirrors that repo. A
            // multi-repo work unit's own writeBackPath (resolved above) points at a *different*
            // physical repo, and syncing "main" against it would diff the wrong pairing. A failed
            // or skipped resync must never affect the merge itself, which already succeeded.
            if (_repositorySync is not null
                && string.Equals(writeBackPath, _workspaceOptions.SeedRepositoryPath, StringComparison.Ordinal))
            {
                try
                {
                    await _repositorySync.SyncBranchFromRepositoryAsync(
                        "main", writeBackPath, SyncTrigger.PostMergeWriteBack, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort audit-trail refresh — the merge already succeeded and files are
                    // already on disk; a resync failure must never fail or roll back an
                    // already-completed apply.
                }
            }
        }

        var updated = proposal with { Status = MergeProposalStatus.Merged, AutoApplied = autoApplied };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

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
        MergeProposal proposal, string effectiveTarget, bool checkForDrift, CancellationToken ct)
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

            if (checkForDrift)
            {
                var targetContent = await _fileWorkspace.ReadAsync(effectiveTarget, path, ct).ConfigureAwait(false);
                var proposalRanges = LineRangeConflictDetector.ComputeChangedRanges(baseContent, proposalContent);
                var driftRanges = LineRangeConflictDetector.ComputeChangedRanges(baseContent, targetContent);

                if (driftRanges.Count > 0 && proposalRanges.Count > 0 &&
                    LineRangeConflictDetector.RangesOverlap(proposalRanges, driftRanges))
                {
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
        return services;
    }
}
