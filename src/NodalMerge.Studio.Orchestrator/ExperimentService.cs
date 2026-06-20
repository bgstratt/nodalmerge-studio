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
    IStudioNodeStore nodeStore) : IExperimentService
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

        // Parent work unit — a named container, never enqueued or executed.
        var parent = await orchestrator.CreateWorkUnitAsync(
            goal:         spec.Goal,
            owner:        spec.Owner,
            metadata:     new Dictionary<string, string> { ["experimentForkType"] = spec.ForkType.ToString() },
            reviewPolicy: spec.ReviewPolicy,
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
                reviewPolicy:     spec.ReviewPolicy,
                cancellationToken: ct).ConfigureAwait(false);

            forkIds.Add(forkWu.WorkUnitId);

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
