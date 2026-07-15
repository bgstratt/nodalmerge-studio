using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// plans/phase-d-implementation.md D3 — plan-staleness signals only, no auto-replan. Hooked from
// ArtifactCommandService.RecordAsync (a superseding Decision was recorded) and
// InMemoryDeadLetterService.RecordFailureAsync (a slice was dead-lettered) — the two cheapest
// existing checkpoints where the underlying data changes, never a polling timer.
public sealed class PlanStalenessService(
    IArtifactLineageService artifacts,
    IWorkUnitService workUnits,
    WorkspaceOptions options,
    IExecutionEventStream? events = null) : IPlanStalenessService
{
    public async Task NotifySupersedingDecisionRecordedAsync(ArtifactRef decision, CancellationToken ct = default)
    {
        if (decision.Type != ArtifactType.Decision || decision.Supersedes.Count == 0)
            return;
        if (decision.OwnedByWorkUnitId is not { } workUnitId)
            return;

        var owning = await FindOwningPlanAsync(workUnitId, ct).ConfigureAwait(false);
        if (owning is not { } found)
            return;

        var (planOwnerId, plan) = found;
        var count = await CountSupersedingDecisionsAsync(planOwnerId, plan.CreatedAt, ct).ConfigureAwait(false);
        if (count < options.PlanStalenessSupersedingDecisionThreshold)
            return;

        await RaiseAsync(
            planOwnerId, plan.ArtifactId, "SupersedingDecisions", count,
            options.PlanStalenessSupersedingDecisionThreshold, ct).ConfigureAwait(false);
    }

    public async Task NotifySliceDeadLetteredAsync(string workUnitId, CancellationToken ct = default)
    {
        var unit = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        if (unit?.ParentWorkUnitId is not { } parentId)
            return;

        var siblings = await workUnits.GetChildrenAsync(parentId, ct).ConfigureAwait(false);
        var count = siblings.Count(s => s.Status == WorkUnitStatus.DeadLettered);
        if (count < options.PlanStalenessDeadLetteredSliceThreshold)
            return;

        var plan = await FindLatestPlanArtifactAsync(parentId, ct).ConfigureAwait(false);
        await RaiseAsync(
            parentId, plan?.ArtifactId, "DeadLetteredSlices", count,
            options.PlanStalenessDeadLetteredSliceThreshold, ct).ConfigureAwait(false);
    }

    public async Task<PlanStalenessState> GetStateAsync(string planOwnerWorkUnitId, CancellationToken ct = default)
    {
        var plan = await FindLatestPlanArtifactAsync(planOwnerWorkUnitId, ct).ConfigureAwait(false);
        var decisionCount = plan is not null
            ? await CountSupersedingDecisionsAsync(planOwnerWorkUnitId, plan.CreatedAt, ct).ConfigureAwait(false)
            : 0;

        var children = await workUnits.GetChildrenAsync(planOwnerWorkUnitId, ct).ConfigureAwait(false);
        var deadLetterCount = children.Count(c => c.Status == WorkUnitStatus.DeadLettered);

        var decisionThreshold = options.PlanStalenessSupersedingDecisionThreshold;
        var deadLetterThreshold = options.PlanStalenessDeadLetteredSliceThreshold;

        return new PlanStalenessState(
            IsStale: decisionCount >= decisionThreshold || deadLetterCount >= deadLetterThreshold,
            decisionCount, decisionThreshold, deadLetterCount, deadLetterThreshold, plan?.ArtifactId);
    }

    // Walks WorkUnit ancestors (starting at workUnitId itself) for the nearest self-owned Plan
    // artifact — "the plan for this chain." Mirrors ArtifactCommandService.CollectChainWithAncestorsAsync's
    // own ParentWorkUnitId walk.
    private async Task<(string PlanOwnerId, ArtifactRef Plan)?> FindOwningPlanAsync(string workUnitId, CancellationToken ct)
    {
        var currentId = workUnitId;
        while (currentId is not null)
        {
            var plan = await FindLatestPlanArtifactAsync(currentId, ct).ConfigureAwait(false);
            if (plan is not null)
                return (currentId, plan);

            var unit = await workUnits.GetAsync(currentId, ct).ConfigureAwait(false);
            currentId = unit?.ParentWorkUnitId;
        }

        return null;
    }

    private async Task<ArtifactRef?> FindLatestPlanArtifactAsync(string workUnitId, CancellationToken ct)
    {
        var chain = await artifacts.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
        return chain
            .Where(a => a.Type == ArtifactType.Plan && a.OwnedByWorkUnitId == workUnitId)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefault();
    }

    // "Same work-unit chain" is scoped to the plan owner plus its immediate fanned-out children —
    // one level, matching NotifySliceDeadLetteredAsync's own sibling scope, not a full recursive
    // subtree scan. Replanning/re-decomposition stays one level deep today (ReplanService's
    // additive-fold model never nests a plan's children further), so this is the cheapest correct
    // spot, not an approximation of a deeper structure that doesn't exist yet.
    private async Task<int> CountSupersedingDecisionsAsync(string planOwnerId, DateTimeOffset plannedAt, CancellationToken ct)
    {
        var ids = new List<string> { planOwnerId };
        var children = await workUnits.GetChildrenAsync(planOwnerId, ct).ConfigureAwait(false);
        ids.AddRange(children.Select(c => c.WorkUnitId));

        var count = 0;
        foreach (var id in ids)
        {
            var chain = await artifacts.GetChainAsync(id, ct).ConfigureAwait(false);
            // >= not > — a decision recorded in the same instant as the plan (sub-millisecond
            // clock resolution in a fast test/run) still counts as "since the plan was recorded";
            // there's no meaningful ordering claim being made at that granularity either way.
            count += chain.Count(a =>
                a.Type == ArtifactType.Decision && a.Supersedes.Count > 0 && a.CreatedAt >= plannedAt);
        }

        return count;
    }

    private async Task RaiseAsync(
        string planOwnerId, string? planArtifactId, string reason, int count, int threshold, CancellationToken ct)
    {
        if (events is null)
            return;

        await events.AppendAsync(
            planOwnerId, planOwnerId, ExecutionEventKind.PlanStalenessSignalRaised,
            new PlanStalenessSignalPayload(planOwnerId, planArtifactId, reason, count, threshold),
            ct: ct).ConfigureAwait(false);
    }
}
