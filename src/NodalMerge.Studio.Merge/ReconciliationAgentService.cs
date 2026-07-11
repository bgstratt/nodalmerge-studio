using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Merge;

// See IReconciliationAgentService's own doc comment for what this is and isn't. Deliberately knows
// nothing about CandidateConflictRecord, CandidateBranchId, or any other subsystem-specific concept
// — everything it needs arrives via ReconciliationRequest, and everything it hands back to the
// triggering adapter is carried on the returned WorkUnit's own fields.
public sealed class ReconciliationAgentService(
    IMergeService merge,
    IWorkUnitService workUnits,
    IProposalReviewService proposalReview,
    IWorkUnitCommandService workUnitCommands,
    IArtifactLineageService artifacts,
    // Optional so direct-construction test call sites keep compiling — when null, the work unit is
    // still created but left at Created for a human to Spawn manually (today's behavior).
    IAgentControlService? agentControl = null,
    IFileLeaseService? fileLease = null,
    // Only needed to lazily resolve IWorkScheduler for ReconciliationFileLeaseRelease — see that
    // class's own comment on why IWorkScheduler can't be a direct constructor dependency here.
    IServiceProvider? serviceProvider = null,
    IExecutionSessionService? sessions = null) : IReconciliationAgentService
{
    // Same cap AutomatedReviewGateService.BuildRevisionContextBody uses for a single proposal's
    // diff — kept here per-proposal since this goal folds in ProposalIds.Count of them.
    private const int MaxDiffCharsPerProposal = 1500;

    public async Task<WorkUnit> TriggerAsync(ReconciliationRequest request, CancellationToken ct = default)
    {
        if (request.ProposalIds.Count < 2)
            throw new InvalidOperationException(
                $"Reconciliation requires at least 2 proposals, got {request.ProposalIds.Count}.");

        var participants = new List<(MergeProposal Proposal, WorkUnit? OwningWorkUnit, IReadOnlyList<ProposalFileChange> Changes)>();
        foreach (var proposalId in request.ProposalIds)
        {
            var proposal = await merge.GetAsync(proposalId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Proposal '{proposalId}' was not found.");
            var owningWorkUnit = proposal.WorkUnitId is not null
                ? await workUnits.GetAsync(proposal.WorkUnitId, ct).ConfigureAwait(false)
                : null;
            var changes = await proposalReview.GetFileChangesAsync(proposalId, ct).ConfigureAwait(false);
            participants.Add((proposal, owningWorkUnit, changes));
        }

        // The losing (and, harmlessly, the already-released winning) source goal's file leases on
        // the conflicting path(s) must be released now — otherwise the reconciliation work unit
        // we're about to create would immediately queue behind a holder that will never finish (see
        // ReconciliationFileLeaseRelease's own comment). Released here, at trigger time, rather than
        // waiting for the eventual supersede at apply time, since the whole point is to unblock the
        // new work right away.
        await ReconciliationFileLeaseRelease.ReleaseForWorkUnitsAsync(
            participants.Select(p => p.OwningWorkUnit?.WorkUnitId), fileLease, serviceProvider, ct)
            .ConfigureAwait(false);

        var goal = BuildReconciliationGoal(request.ConflictingPaths, participants, request.SteeringNotes);

        var workUnit = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand(
                Goal: goal,
                Owner: "reconciliation-agent",
                ParentWorkUnitId: request.ParentWorkUnitId,
                SeedFromBranchId: request.SeedBranchId,
                // Whichever of these WorkspaceReviewScope.AppliesToRealRepo actually consults for
                // this work unit (WorkspaceReviewPolicy when it ends up top-level, TaskReviewPolicy
                // when ParentWorkUnitId makes it a fan-out child) gets the same value either way.
                TaskReviewPolicy: request.WorkspaceReviewPolicy,
                WorkspaceReviewPolicy: request.WorkspaceReviewPolicy,
                ReconciliationSourceProposalIds: request.ProposalIds,
                ReconciliationTargetPaths: request.ConflictingPaths,
                ReconciliationSourceRef: request.SourceRef),
            ct).ConfigureAwait(false);

        // Audit/lineage trail only — the goal text above already carries everything the agent
        // needs, so nothing downstream depends on this artifact being read back.
        await artifacts.RecordAsync(
            new ArtifactRef(
                $"KA-{Guid.NewGuid():N}",
                ArtifactType.ReconciliationContext,
                workUnit.WorkUnitId,
                ArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                workUnit.WorkUnitId,
                null,
                "Reconciliation source proposals",
                string.Join(", ", request.ProposalIds)),
            ct).ConfigureAwait(false);

        // The candidate-branch adapter creates a fresh top-level goal (ParentWorkUnitId null) —
        // give it its own session up front, the same explicit step every other top-level goal gets
        // from its own creation flow (nothing does this automatically on work unit creation or
        // spawn), so it shows up in Goal Workspace like any other goal instead of existing only as
        // an orphaned work unit. The task-level adapter's reconciliation work unit is a fan-out
        // child instead (ParentWorkUnitId set) — it belongs under its parent goal's own session,
        // not a new one. Done regardless of whether a spawn below succeeds, so even a
        // credentials-less reconciliation is still visible for a human to find and Spawn manually.
        if (workUnit.ParentWorkUnitId is null && sessions is not null)
        {
            try
            {
                await sessions.CreateAsync(workUnit.WorkUnitId, "{}", ["reconciliation-agent"], ct: ct)
                    .ConfigureAwait(false);
            }
            catch { /* best-effort — a human can still work with this goal without a session record */ }
        }

        // One-click Reconcile: spawn the orchestrator immediately using whichever source goal's
        // own credentials we can find, instead of leaving the work unit at Created for a human to
        // notice and Spawn manually. Best-effort — a spawn failure (e.g. no credentials resolvable)
        // must never fail the reconciliation trigger itself; the work unit still exists and a human
        // can always Spawn it by hand.
        if (agentControl is not null)
        {
            var creds = request.Credentials ?? ResolveCredentials(participants.Select(p => p.OwningWorkUnit));
            if (creds is not null)
            {
                try
                {
                    await agentControl.SpawnAsync(
                        "orchestrator", workUnit.WorkUnitId,
                        model: creds.Model, baseUrl: creds.BaseUrl, apiKey: creds.ApiKey,
                        provider: creds.Provider, profileId: creds.ProfileId,
                        credentialRef: creds.CredentialRef,
                        cancellationToken: ct).ConfigureAwait(false);
                }
                catch { /* best-effort — see comment above */ }
            }
        }

        return workUnit;
    }

    // Same fallback chain AutomatedReviewGateService.RetryRejectedProposalOwnerAsync uses to find
    // credentials for a retried work unit: per-stage override, then the work unit's own orchestrator
    // credentials, then (if it's itself a fan-out child) the same two lookups on its parent. Tries
    // every source participant in order — any one of the conflicting goals' own credentials working
    // is enough to get the reconciliation agent started.
    private OrchestratorCredentials? ResolveCredentials(IEnumerable<WorkUnit?> owners)
    {
        foreach (var owner in owners)
        {
            if (owner is null)
                continue;

            var creds = agentControl!.GetCredentialsForStage(owner.WorkUnitId, PipelineStage.Execute)
                ?? agentControl.GetOrchestratorCredentials(owner.WorkUnitId)
                ?? (owner.ParentWorkUnitId is { } parentId
                    ? agentControl.GetCredentialsForStage(parentId, PipelineStage.Execute)
                        ?? agentControl.GetOrchestratorCredentials(parentId)
                    : null);
            if (creds is not null)
                return creds;
        }

        return null;
    }

    private static string BuildReconciliationGoal(
        IReadOnlyList<string> conflictingPaths,
        IReadOnlyList<(MergeProposal Proposal, WorkUnit? OwningWorkUnit, IReadOnlyList<ProposalFileChange> Changes)> participants,
        string? steeringNotes)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Reconcile the following independently-developed goals, which diverged on the same file(s):");
        sb.AppendLine($"Conflicting path(s): {string.Join(", ", conflictingPaths)}");
        sb.AppendLine();

        // Surfaced first and most prominently, ahead of either goal's own text — a human wrote
        // this specifically because the "right" combination is genuinely ambiguous from the goals
        // alone (e.g. both entirely valid, mutually exclusive outcomes like picking a winner vs.
        // keeping both). Without it, the agent has no way to know which one the human actually
        // wants, no matter how carefully it reads the two goals.
        if (!string.IsNullOrWhiteSpace(steeringNotes))
        {
            sb.AppendLine("HUMAN STEERING NOTES — follow these over your own judgment of the goals below:");
            sb.AppendLine(steeringNotes);
            sb.AppendLine();
        }

        for (var i = 0; i < participants.Count; i++)
        {
            var (proposal, owningWorkUnit, changes) = participants[i];
            sb.AppendLine($"--- Goal {i + 1} ---");
            sb.AppendLine(owningWorkUnit?.Goal ?? proposal.Goal);
            if (!string.IsNullOrWhiteSpace(owningWorkUnit?.SuccessCriteria))
                sb.AppendLine("Success criteria: " + owningWorkUnit.SuccessCriteria);

            foreach (var change in changes.Where(c => conflictingPaths.Contains(c.Path)))
            {
                var after = change.AfterContent ?? "(deleted)";
                var truncated = after.Length > MaxDiffCharsPerProposal
                    ? after[..MaxDiffCharsPerProposal] + "\n… (truncated)"
                    : after;
                sb.AppendLine($"File '{change.Path}' as this goal left it:");
                sb.AppendLine(truncated);
            }

            sb.AppendLine();
        }

        sb.AppendLine(
            "Produce a single combined result for the conflicting path(s) above that satisfies every goal " +
            "listed. Build and test before proposing; iterate if either fails.");
        return sb.ToString();
    }
}
