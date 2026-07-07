using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Orchestrator;

// Slice 22a — creates an experiment: one parent container work unit + N child fork work units,
// each optionally enqueued immediately when a ProfileId is provided so they run in parallel
// without further action. The parent is never executed; it's the lineage anchor that the
// ModelDivergenceView projection and the comparison UI key off.
public sealed class ExperimentService(
    IOrchestratorService orchestrator,
    ISchedulerCommandService scheduler,
    IHypothesisNodeService hypotheses,
    IWorkUnitService workUnits,
    IMergeService merges,
    IMergeCommandService mergeCommands,
    IDecisionNodeService decisions,
    IStudioNodeStore nodeStore,
    IRepositoryRegistryService repositories) : IExperimentService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task<ExperimentResult> CreateAsync(ExperimentSpec spec, CancellationToken ct = default)
    {
        if (spec.Forks.Count < 2)
            throw new ArgumentException("An experiment requires at least 2 forks.", nameof(spec));

        // Slice 22b — type-specific validation; REST layer also validates, but the service
        // must be self-guarding for direct (non-HTTP) callers.
        var typeError = ValidateForksForType(spec.ForkType, spec.Forks);
        if (typeError is not null)
            throw new ArgumentException(typeError, nameof(spec));

        // Resolve a durable RepositoryId once, same precedence as WorkUnitCommandService.CreateAsync
        // (RepositoryId takes priority over a raw path; a raw path auto-registers). Every fork gets
        // this same id below — without one, a fork (always has a ParentWorkUnitId) is never allowed
        // to write its apply back to the real repo (see WorkspaceReviewScope.AppliesToRealRepo).
        var effectiveRepositoryId = spec.RepositoryId;
        if (effectiveRepositoryId is not null)
        {
            _ = await repositories.GetAsync(effectiveRepositoryId, ct).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Repository '{effectiveRepositoryId}' was not found.");
        }
        else if (!string.IsNullOrWhiteSpace(spec.RepositoryPath))
        {
            var registered = await repositories.RegisterAsync(spec.RepositoryPath, label: null, ct).ConfigureAwait(false);
            effectiveRepositoryId = registered.RepositoryId;
        }

        // Parent work unit — a named container, never enqueued or executed.
        var parent = await orchestrator.CreateWorkUnitAsync(
            goal:         spec.Goal,
            owner:        spec.Owner,
            metadata:     new Dictionary<string, string> { ["experimentForkType"] = spec.ForkType.ToString() },
            workspaceReviewPolicy: spec.WorkspaceReviewPolicy,
            workspaceReviewHybridTimeoutMinutes: spec.WorkspaceReviewHybridTimeoutMinutes,
            repositoryId: effectiveRepositoryId,
            cancellationToken: ct).ConfigureAwait(false);

        var forkIds = new List<string>(spec.Forks.Count);

        for (var i = 0; i < spec.Forks.Count; i++)
        {
            var fork     = spec.Forks[i];
            var forkGoal = BuildForkGoal(spec.Goal, spec.ForkType, fork, i);

            // Slice 22b — type-specific metadata keys so future projections can query by kind.
            var forkMeta = new Dictionary<string, string>();
            if (fork.ProfileId is not null) forkMeta["experimentProfileId"] = fork.ProfileId;
            AddConstraintMetadata(forkMeta, spec.ForkType, fork.ConstraintText);

            var forkWu = await orchestrator.CreateWorkUnitAsync(
                goal:             forkGoal,
                owner:            spec.Owner,
                parentWorkUnitId: parent.WorkUnitId,
                forkType:         spec.ForkType,
                metadata:         forkMeta.Count > 0 ? forkMeta : null,
                taskReviewPolicy: spec.TaskReviewPolicy,
                taskReviewHybridTimeoutMinutes: spec.TaskReviewHybridTimeoutMinutes,
                // A fork given its own RepositoryId is gated by WorkspaceReviewPolicy instead of
                // TaskReviewPolicy (see WorkspaceReviewScope.AppliesToRealRepo) — pass both through
                // so the fork's own apply gate matches what the caller actually selected, same as
                // Multi-Model Comparison's children (ArtifactExplorerPanel.ts).
                workspaceReviewPolicy: spec.WorkspaceReviewPolicy,
                workspaceReviewHybridTimeoutMinutes: spec.WorkspaceReviewHybridTimeoutMinutes,
                repositoryId: effectiveRepositoryId,
                cancellationToken: ct).ConfigureAwait(false);

            forkIds.Add(forkWu.WorkUnitId);

            await hypotheses.RecordAsync(new HypothesisNode(
                HypothesisId:     $"hyp-{Guid.NewGuid():N}",
                WorkUnitId:       forkWu.WorkUnitId,
                Goal:             forkGoal,
                ForkType:         spec.ForkType,
                Status:           HypothesisStatus.Active,
                ParentWorkUnitId: parent.WorkUnitId,
                BranchedFromProposalId: null,
                Rationale:        null,
                CreatedAt:        DateTimeOffset.UtcNow,
                SessionId:        spec.SessionId), ct).ConfigureAwait(false);

            // Auto-enqueue when a profile is supplied — the scheduler runs forks in parallel
            // up to MaxConcurrentWorkers without any further caller action.
            if (fork.ProfileId is not null)
            {
                await scheduler.EnqueueAsync(
                    forkWu.WorkUnitId,
                    fork.ProfileId,
                    sessionId: spec.SessionId,
                    ct: ct).ConfigureAwait(false);
            }
        }

        var experimentId = $"exp-{Guid.NewGuid():N}";
        var node = new ExperimentNode(
            ExperimentId:         experimentId,
            ParentWorkUnitId:     parent.WorkUnitId,
            ForkType:             spec.ForkType,
            ForkWorkUnitIds:      forkIds,
            ComparisonMetricHint: spec.ComparisonMetricHint,
            CreatedAt:            DateTimeOffset.UtcNow,
            SessionId:            spec.SessionId);

        await nodeStore.WriteNodeAsync(
            StudioNodeKind.ExperimentV1,
            experimentId,
            JsonSerializer.Serialize(node),
            ct).ConfigureAwait(false);

        return new ExperimentResult(experimentId, parent.WorkUnitId, forkIds);
    }

    public async Task<ExperimentNode?> GetAsync(string experimentId, CancellationToken ct = default)
    {
        var json = await nodeStore.ReadNodeAsync(StudioNodeKind.ExperimentV1, experimentId, ct).ConfigureAwait(false);
        return json is null ? null : JsonSerializer.Deserialize<ExperimentNode>(json, JsonOpts);
    }

    public async Task<IReadOnlyList<ExperimentNode>> ListAsync(CancellationToken ct = default)
    {
        var nodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.ExperimentV1, ct).ConfigureAwait(false);
        return nodes
            .Select(n => JsonSerializer.Deserialize<ExperimentNode>(n.PayloadJson, JsonOpts))
            .Where(n => n is not null)
            .Select(n => n!)
            .OrderByDescending(n => n.CreatedAt)
            .ToList();
    }

    // Comparison engine — converges an experiment: approve the winner's latest proposal, reject
    // every other sibling's non-terminal latest proposal, record a DecisionNode per sibling, and
    // transition each sibling's HypothesisNode status. Today this is always human/caller-driven
    // (no autonomous winner selection); the caller decides who won, this just makes that decision
    // durable and propagates it to every loser instead of leaving them dangling.
    public async Task<ConvergenceResult> ConvergeAsync(
        string parentWorkUnitId, string winnerWorkUnitId, string? rationale, CancellationToken ct = default)
    {
        var siblings = await workUnits.GetChildrenAsync(parentWorkUnitId, ct).ConfigureAwait(false);
        if (siblings.Count == 0)
            throw new InvalidOperationException($"No fork children found under parent '{parentWorkUnitId}'.");
        if (!siblings.Any(s => s.WorkUnitId == winnerWorkUnitId))
            throw new InvalidOperationException($"Winner '{winnerWorkUnitId}' is not a fork of experiment parent '{parentWorkUnitId}'.");

        var allProposals = await merges.ListAsync(sourceBranch: null, ct).ConfigureAwait(false);
        var hypothesisNodes = await hypotheses.ListByParentWorkUnitIdAsync(parentWorkUnitId, ct).ConfigureAwait(false);

        var rejected = new List<string>();

        foreach (var sibling in siblings)
        {
            var isWinner = sibling.WorkUnitId == winnerWorkUnitId;
            var latestProposal = allProposals.Where(p => p.WorkUnitId == sibling.WorkUnitId).LastOrDefault();

            if (latestProposal is not null)
            {
                var isTerminal = latestProposal.Status is MergeProposalStatus.Approved
                    or MergeProposalStatus.Merged or MergeProposalStatus.Rejected;

                // Best-effort: a proposal may still be Draft (not yet validated to ReadyForReview),
                // in which case ReviewAsync's transition check throws. Convergence state (decision +
                // hypothesis status) below is still recorded regardless — the proposal transition is
                // a bonus, not a precondition.
                if (isWinner && latestProposal.Status is not (MergeProposalStatus.Approved or MergeProposalStatus.Merged))
                {
                    try
                    {
                        await mergeCommands.ReviewAsync(
                            latestProposal.ProposalId, "Approved", notes: rationale, cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException) { /* proposal not yet reviewable — best-effort */ }
                }
                else if (!isWinner && !isTerminal)
                {
                    try
                    {
                        await mergeCommands.ReviewAsync(
                            latestProposal.ProposalId, "Rejected",
                            notes: rationale ?? $"Superseded by winning fork '{winnerWorkUnitId}'.",
                            cancellationToken: ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException) { /* proposal not yet reviewable — best-effort */ }
                    rejected.Add(sibling.WorkUnitId);
                }
                else if (!isWinner)
                {
                    rejected.Add(sibling.WorkUnitId);
                }
            }
            else if (!isWinner)
            {
                rejected.Add(sibling.WorkUnitId);
            }

            await decisions.RecordAsync(new DecisionNode(
                DecisionId:       $"dec-{Guid.NewGuid():N}",
                WorkUnitId:       sibling.WorkUnitId,
                ProposalId:       latestProposal?.ProposalId,
                Outcome:          isWinner ? DecisionOutcome.Accepted : DecisionOutcome.Rejected,
                ReviewerAgentId:  null,
                ReviewerModel:    null,
                ReviewerProvider: null,
                Confidence:       latestProposal?.Confidence,
                Rationale:        rationale,
                DecidedAt:        DateTimeOffset.UtcNow), ct).ConfigureAwait(false);

            var hypothesis = hypothesisNodes.FirstOrDefault(h => h.WorkUnitId == sibling.WorkUnitId);
            if (hypothesis is not null)
            {
                await hypotheses.UpdateStatusAsync(
                    hypothesis.HypothesisId,
                    isWinner ? HypothesisStatus.Converged : HypothesisStatus.Rejected,
                    ct).ConfigureAwait(false);
            }
        }

        return new ConvergenceResult(parentWorkUnitId, winnerWorkUnitId, rejected, rationale);
    }

    // Slice 22b — Architecture/Library/Product require ConstraintText; Model requires ProfileId.
    // Code/Reasoning/Research have no mandatory fields.
    private static string? ValidateForksForType(HypothesisForkType forkType, IReadOnlyList<ExperimentForkSpec> forks) =>
        forkType switch
        {
            HypothesisForkType.Model => forks.Any(f => string.IsNullOrWhiteSpace(f.ProfileId))
                ? "Model experiments require every fork to have a profileId."
                : null,
            HypothesisForkType.Architecture or HypothesisForkType.Library or HypothesisForkType.Product =>
                forks.Any(f => string.IsNullOrWhiteSpace(f.ConstraintText))
                    ? $"{forkType} experiments require every fork to have a constraintText."
                    : null,
            _ => null
        };

    // Slice 22b — type-specific metadata keys so downstream projections can distinguish
    // what kind of constraint was stored without parsing the goal text.
    private static void AddConstraintMetadata(Dictionary<string, string> meta, HypothesisForkType forkType, string? constraintText)
    {
        if (string.IsNullOrWhiteSpace(constraintText)) return;
        var key = forkType switch
        {
            HypothesisForkType.Architecture => "architectureConstraint",
            HypothesisForkType.Library      => "libraryConstraint",
            HypothesisForkType.Product      => "productStrategy",
            _                               => "experimentConstraint"
        };
        meta[key] = constraintText;
    }

    // Slice 22b — type-specific goal text framing:
    //   Architecture  →  "[Architecture: <constraint>]"   (structural approach label)
    //   Library       →  "[using <constraint>]"           (dependency label)
    //   Product       →  "[strategy: <constraint>]"       (product strategy label)
    //   Model         →  "[Model: <profileId>]"           (model identifier)
    //   others        →  "[ForkType: <constraint>]" or "[Fork A: <label>]" fallback
    private static string BuildForkGoal(string baseGoal, HypothesisForkType forkType, ExperimentForkSpec fork, int index) =>
        forkType switch
        {
            HypothesisForkType.Architecture => $"{baseGoal} [Architecture: {fork.ConstraintText}]",
            HypothesisForkType.Library      => $"{baseGoal} [using {fork.ConstraintText}]",
            HypothesisForkType.Product      => $"{baseGoal} [strategy: {fork.ConstraintText}]",
            HypothesisForkType.Model        => $"{baseGoal} [Model: {fork.ProfileId ?? $"Fork {(char)('A' + index)}"}]",
            _ when fork.ConstraintText is { Length: > 0 } constraint
                                            => $"{baseGoal} [{forkType}: {constraint}]",
            _                               => $"{baseGoal} [Fork {(char)('A' + index)}: {fork.ProfileId ?? $"Fork {index + 1}"}]"
        };
}
