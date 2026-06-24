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

    // IWorkUnitService is resolved lazily (via IServiceProvider) rather than constructor-injected:
    // its production implementation (InMemoryWorkUnitService) already depends on IMergeService
    // (this service), so a direct dependency here would be a circular constructor graph — same
    // pattern used by WorkSchedulerService for the same interface. IWorkScheduler is resolved the
    // same lazy way below (Phase 12's release-and-resume hook) for the identical reason —
    // WorkSchedulerService takes IMergeService directly, so a direct dependency back here would
    // be the same cycle. IFileLeaseService has no such cycle, so it's constructor-injected
    // directly — but kept optional (default null) so the existing direct (non-DI) test
    // constructions don't all need updating; when null, the release-and-resume hook is skipped.
    public InMemoryMergeService(
        IStudioNodeStore nodeStore,
        IFileWorkspaceService fileWorkspace,
        WorkspaceOptions workspaceOptions,
        IExecutionEventStream events,
        IArtifactLineageService artifacts,
        IServiceProvider? serviceProvider = null,
        IFileLeaseService? fileLease = null)
    {
        _nodeStore        = nodeStore;
        _fileWorkspace    = fileWorkspace;
        _workspaceOptions = workspaceOptions;
        _events           = events;
        _artifacts        = artifacts;
        _serviceProvider  = serviceProvider;
        _fileLease        = fileLease;
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
            $"base/{proposal.ProposalId}", proposal.TargetBranch, cancellationToken).ConfigureAwait(false);

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
        };
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

            if (decision == MergeProposalStatus.Rejected)
            {
                await _events.AppendAsync(
                    proposal.SessionId,
                    proposal.WorkUnitId,
                    ExecutionEventKind.ProposalRejected,
                    new ProposalRejectedPayload(proposalId, reviewerAgentId ?? "reviewer", verificationResults),
                    ct: cancellationToken).ConfigureAwait(false);
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
        var bypassPromotionBranch = false;
        if (proposal.WorkUnitId is not null)
        {
            var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
            if (workUnits is not null)
            {
                var owningWorkUnit = await workUnits.GetAsync(proposal.WorkUnitId, cancellationToken).ConfigureAwait(false);
                bypassPromotionBranch = owningWorkUnit?.BypassPromotionBranch ?? false;
            }
        }

        var effectiveTarget = _workspaceOptions.UsePromotionBranch && !bypassPromotionBranch
            ? _workspaceOptions.CandidateBranchId
            : proposal.TargetBranch;

        // Copy workspace files: source branch → effective target branch
        await _fileWorkspace.ApplyBranchAsync(proposal.SourceBranch, effectiveTarget, cancellationToken)
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

        // Write changed files back to disk whenever a repository path is configured
        if (!string.IsNullOrWhiteSpace(_workspaceOptions.SeedRepositoryPath))
        {
            await WriteBackToRepositoryAsync(proposal.SourceBranch, cancellationToken).ConfigureAwait(false);
        }

        var updated = proposal with { Status = MergeProposalStatus.Merged, AutoApplied = autoApplied };
        _proposals[proposalId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.MergeProposalV1,
            proposalId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

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

    private async Task WriteBackToRepositoryAsync(string sourceBranchId, CancellationToken ct)
    {
        var repoPath = _workspaceOptions.SeedRepositoryPath!;
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
        return services;
    }
}
