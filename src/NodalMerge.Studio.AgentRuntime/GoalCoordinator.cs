using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// plans/orchestrator-pure-service.md M2 — the deterministic replacement for
// OrchestratorAgentLoop. Everything here is the loop's own post-LLM tail (fan-out, rescue sweep,
// reconcile, reviewer enqueue, completion check) plus the one thing the LLM uniquely did: enqueue
// the planner at goal start with injected credentials (including the D2 planner-executor
// selection). Dependencies are resolved through IServiceProvider, matching how the loop's own
// construction site did it and avoiding a DI cycle with IAgentControlService (whose
// implementation calls back into this coordinator on spawn/reinvoke).
internal sealed class GoalCoordinator(IServiceProvider serviceProvider, ILogger<GoalCoordinator> logger)
    : IGoalCoordinator
{
    // Decision-log author id — the coordinator has no agent identity, but OrchestrationEvent
    // requires one; a stable constant keeps the timeline readable and filterable.
    internal const string CoordinatorAgentId = "goal-coordinator";

    public Task StartGoalAsync(string workUnitId, string? sessionId = null, CancellationToken ct = default) =>
        ConvergeAsync(workUnitId, sessionId, ensurePlanner: true, ct);

    public async Task ConvergeAsync(
        string workUnitId, string? sessionId = null, bool ensurePlanner = false,
        CancellationToken ct = default)
    {
        var workUnits = serviceProvider.GetRequiredService<IWorkUnitService>();
        var unit = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        if (unit is null || unit.Status is WorkUnitStatus.Completed or WorkUnitStatus.Merged or WorkUnitStatus.Cancelled)
            return;

        // A converging DeadLettered goal is being driven again (a dead-letter retry re-enqueued
        // its planner, a child just released, or a human hit Reinvoke) — repair the stale badge
        // so the goal doesn't read as dead while work runs underneath it. Best-effort: an
        // in-flight status race just means the next sweep repairs it instead.
        if (unit.Status == WorkUnitStatus.DeadLettered)
        {
            try
            {
                await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.Executing, sessionId, ct).ConfigureAwait(false);
            }
            catch (InvalidOperationException) { /* concurrent transition — next sweep repairs */ }
        }

        if (ensurePlanner)
            await EnsurePlannerAsync(workUnitId, unit, sessionId, ct).ConfigureAwait(false);

        var fanOut = serviceProvider.GetService<IFanOutService>();
        if (fanOut is not null)
        {
            await fanOut.TryFanOutFromPlanAsync(workUnitId, sessionId, ct).ConfigureAwait(false);

            // Rescue sweep (ported verbatim from the LLM loop's tail) — a child that is itself an
            // orchestrator-type unit (reconciliation units are the canonical case) can hold a
            // recorded Plan whose fan-out never fired. TryFanOutFromPlanAsync is idempotent and
            // returns immediately for ordinary leaf children with no Plan artifact of their own.
            try
            {
                var children = await workUnits.GetChildrenAsync(workUnitId, ct).ConfigureAwait(false);
                foreach (var child in children)
                {
                    if (child.Status is WorkUnitStatus.Executing or WorkUnitStatus.Active or WorkUnitStatus.Waiting)
                        await fanOut.TryFanOutFromPlanAsync(child.WorkUnitId, sessionId, ct).ConfigureAwait(false);
                }
            }
            catch { /* best-effort — the reconciliation sweep below still runs */ }

            await fanOut.TryEnqueueReadyDependentsAsync(workUnitId, sessionId, ct).ConfigureAwait(false);
        }

        var mergeReconciliation = serviceProvider.GetService<IMergeReconciliationService>();
        var reconciliation = mergeReconciliation is not null
            ? await mergeReconciliation.TryReconcileAsync(workUnitId, sessionId, ct).ConfigureAwait(false)
            : null;

        var automatedReview = serviceProvider.GetService<IAutomatedReviewGateService>();
        if (automatedReview is not null)
            await automatedReview.TryEnqueueReviewerAsync(workUnitId, sessionId, ct).ConfigureAwait(false);

        if (reconciliation is null)
            return;

        // Record what the sweep actually concluded — same reasons the LLM loop's tail wrote, so a
        // human reading the decision log sees "waiting on child X" / "reconciled — review it"
        // instead of an apparent dead stop.
        var reconAction = reconciliation.Outcome switch
        {
            MergeReconciliationOutcome.Reconciled or MergeReconciliationOutcome.AlreadyReconciled
                => OrchestrationAction.AwaitReview,
            MergeReconciliationOutcome.Conflict => OrchestrationAction.Escalate,
            _ => OrchestrationAction.NoOp,
        };
        var reconReason = reconciliation.Outcome switch
        {
            MergeReconciliationOutcome.Reconciled =>
                $"Reconciled child proposals into {reconciliation.ReconciledProposalId} — awaiting workspace review.",
            MergeReconciliationOutcome.AlreadyReconciled =>
                $"Reconciled proposal {reconciliation.ReconciledProposalId} already exists — awaiting review/apply.",
            _ => reconciliation.Detail,
        };
        if (reconReason is not null)
            await RecordDecisionAsync(workUnitId, reconAction, [], reconReason, sessionId, ct).ConfigureAwait(false);

        // Only complete once the reconciled proposal has actually been approved/merged — Reconciled
        // means a fresh Draft proposal (pending review), and AlreadyReconciled only means a
        // non-Rejected proposal exists, which could still be sitting ReadyForReview/UnderReview.
        // Completed is terminal (no transition out), so completing early would strand a later
        // rejection's retry — see the identical check the LLM loop's tail carried.
        if (reconciliation.Outcome is MergeReconciliationOutcome.Reconciled or MergeReconciliationOutcome.AlreadyReconciled
            && reconciliation.ReconciledProposalId is { } reconciledProposalId
            && serviceProvider.GetService<IMergeService>() is { } merge)
        {
            var reconciledProposal = await merge.GetAsync(reconciledProposalId, ct).ConfigureAwait(false);
            if (reconciledProposal?.Status is MergeProposalStatus.Approved or MergeProposalStatus.Merged)
            {
                var current = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
                if (current?.Status != WorkUnitStatus.Completed)
                {
                    await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.Completed, cancellationToken: ct)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    // The one decision the LLM loop made that services didn't already cover: kick off planning.
    // Guarded so it only fires when nothing exists yet — no Plan artifact, no children, no pending
    // scheduler item — which is what makes ConvergeAsync(ensurePlanner: true) safe to call from
    // manual recovery on any goal state.
    private async Task EnsurePlannerAsync(string workUnitId, WorkUnit unit, string? sessionId, CancellationToken ct)
    {
        var workUnits = serviceProvider.GetRequiredService<IWorkUnitService>();
        var children = await workUnits.GetChildrenAsync(workUnitId, ct).ConfigureAwait(false);
        if (children.Count > 0)
            return;

        var artifactLineage = serviceProvider.GetService<IArtifactLineageService>();
        if (artifactLineage is not null)
        {
            var chain = await artifactLineage.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
            if (chain.Any(a => a.Type == ArtifactType.Plan))
                return;
        }

        var scheduler = serviceProvider.GetService<ISchedulerCommandService>();
        if (scheduler is null)
            return;
        var pending = await scheduler.ListPendingAsync(ct).ConfigureAwait(false);
        if (pending.Any(p => p.WorkUnitId == workUnitId))
            return;

        // A reconciliation work unit (see ReconciliationAgentService.BuildReconciliationGoal) already
        // carries a single, fully-formed goal — merge these already-completed sibling changes into one
        // coherent result. It must never go through the Planner: planning would re-decompose that
        // already-done work into a fresh task list and fan it back out, which is exactly the
        // re-litigate-everything behavior reconciliation exists to avoid (a single reconciler agent
        // doing the merge directly, not a planner/fan-out over it again). Route it straight to the
        // "reconciler" profile instead, on the same enqueue path planning would have used.
        var isReconciliation = unit.ReconciliationSourceProposalIds is { Count: > 0 };

        // Credential injection — ported from OrchestratorAgentLoop.InjectSpawnCredentialsAsync:
        // the Plan-stage Agent Topology override wins; otherwise the goal's Default-profile
        // credentials, with the D2 planner-executor selection consulted only when the Plan stage
        // is auto/unset (an explicit per-stage assignment is never second-guessed). Reconciliation
        // units use the Reconcile stage instead and skip planner selection entirely — there is no
        // planning decision to make.
        var agentControl = serviceProvider.GetService<IAgentControlService>();
        var stageCreds = agentControl?.GetCredentialsForStage(
            workUnitId, isReconciliation ? PipelineStage.Reconcile : PipelineStage.Plan);
        var defaultCreds = agentControl?.GetGoalDefaultCredentials(workUnitId);
        var profileId = isReconciliation ? "reconciler" : "planner";
        string? selectedProvider = null;

        if (!isReconciliation && stageCreds is null
            && serviceProvider.GetService<IPlannerSelectionService>() is { } plannerSelection)
        {
            var selection = await plannerSelection.SelectPlannerAsync(unit, defaultCreds, ct).ConfigureAwait(false);
            profileId = selection.ProfileId;
            selectedProvider = selection.Provider;
        }

        var creds = stageCreds ?? defaultCreds;
        if (creds is null)
        {
            // Same graceful degradation the credential-less spawn path always had: the goal stays
            // visible and a human can supply credentials and reinvoke. Never throw from here.
            logger.LogWarning(
                "[GoalCoordinator] {Profile} enqueue for workUnit={WorkUnitId} skipped — no {Stage}-stage or " +
                "Default-profile credentials registered. Resupply credentials and reinvoke to start.",
                profileId, workUnitId, isReconciliation ? "Reconcile" : "Plan");
            return;
        }

        await scheduler.EnqueueAsync(
            workUnitId,
            profileId,
            model: stageCreds?.Model ?? defaultCreds?.Model,
            baseUrl: stageCreds?.BaseUrl ?? defaultCreds?.BaseUrl,
            apiKey: stageCreds?.ApiKey ?? defaultCreds?.ApiKey,
            provider: selectedProvider ?? stageCreds?.Provider ?? defaultCreds?.Provider,
            sessionId: sessionId,
            credentialRef: stageCreds?.CredentialRef ?? defaultCreds?.CredentialRef,
            ct: ct).ConfigureAwait(false);

        await RecordDecisionAsync(
            workUnitId, isReconciliation ? OrchestrationAction.SpawnWorker : OrchestrationAction.SpawnPlanner,
            [workUnitId],
            $"Enqueued {(isReconciliation ? "reconciler" : "planner")} (profile '{profileId}') for goal start.",
            sessionId, ct).ConfigureAwait(false);
    }

    private async Task RecordDecisionAsync(
        string workUnitId, OrchestrationAction action, IReadOnlyList<string> spawnedIds,
        string? reason, string? sessionId, CancellationToken ct)
    {
        // The decision log is informational — never worth failing convergence over (same posture
        // the LLM loop's tail took).
        try
        {
            var decisionLog = serviceProvider.GetService<IOrchestrationDecisionLogService>();
            if (decisionLog is null)
                return;

            var workUnits = serviceProvider.GetRequiredService<IWorkUnitService>();
            var unit = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            var stage = unit?.CurrentStage ?? PipelineStage.Orchestrate;

            var projectionJson = "{}";
            if (serviceProvider.GetService<IProjectionManager>() is { } projections)
            {
                var projection = await projections.GetAsync(
                    new ProjectionRequest(
                        ProjectionType.AgentWorkspace, ProjectionLevel.Normal,
                        WorkUnitId: workUnitId, AgentId: CoordinatorAgentId),
                    ct).ConfigureAwait(false);
                projectionJson = projection.DataJson;
            }

            await decisionLog.RecordAsync(
                workUnitId, CoordinatorAgentId, stage, projectionJson, action, spawnedIds, reason, sessionId, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "[GoalCoordinator] Decision-log write failed for workUnit={WorkUnitId}.", workUnitId);
        }
    }
}
