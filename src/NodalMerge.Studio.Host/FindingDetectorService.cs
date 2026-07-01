using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Host;

// Manual-only ("Detect Findings" button) — never scheduled, never chained automatically after a
// run. Lives in Host rather than Storage or Projections because it needs IProjectionManager
// (Projections, which already depends on Storage), IFindingService, IMergeService,
// IEvidenceNodeService, and IStudioNodeStore (Storage/Merge) — putting it in any of those would
// create a circular project reference.
public sealed class FindingDetectorService(
    IProjectionManager projections,
    IFindingService findings,
    IMergeService merges,
    IEvidenceNodeService evidenceNodes,
    IStudioNodeStore nodeStore,
    ICoModService? coMod = null,
    IWorkUnitService? workUnits = null,
    WorkspaceOptions? workspaceOptions = null)
{
    private const int MinSampleSize = 5;
    private const double WinRateThreshold = 0.70;
    private const int MinRejectedProposals = 5;
    private const double MissingEvidenceThreshold = 0.50;
    private const int MinDistinctWorkUnitsForSteeringKeyword = 3;
    private const double CoModMissConfidenceThreshold = 0.6;
    private const double BoundaryViolationConfidenceThreshold = 0.4;
    private const int BoundaryViolationMinCount = 3;

    public async Task<IReadOnlyList<Finding>> DetectDeterministicAsync(CancellationToken ct = default)
    {
        var result = await projections.GetAsync(
            new ProjectionRequest(ProjectionType.RunRetrospective, ProjectionLevel.Normal), ct).ConfigureAwait(false);
        var retro = JsonSerializer.Deserialize<RunRetrospectiveProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        if (retro is null) { return []; }

        // Dedupe against existing Open deterministic findings by title — re-clicking "Detect
        // Findings" shouldn't spam the queue with the same recommendation every time.
        var existing = await findings.ListAsync(ct).ConfigureAwait(false);
        var openTitles = existing
            .Where(f => f.Source == FindingSource.Deterministic && f.Status == FindingStatus.Open)
            .Select(f => f.Title)
            .ToHashSet();

        var created = new List<Finding>();

        foreach (var stat in retro.ForkWinRates)
        {
            var decided = stat.Wins + stat.Losses;
            if (decided < MinSampleSize || stat.WinRate < WinRateThreshold) { continue; }

            var title = $"{stat.ForkType} forks win {Math.Round(stat.WinRate * 100)}% of the time";
            if (openTitles.Contains(title)) { continue; }

            created.Add(await findings.ProposeAsync(new Finding(
                FindingId: $"finding-{Guid.NewGuid():N}",
                Kind: FindingKind.KnowledgeGuideline,
                Source: FindingSource.Deterministic,
                Title: title,
                Summary: $"Across {decided} decided {stat.ForkType} forks ({stat.Wins} won, {stat.Losses} lost), " +
                    $"the winning approach succeeded {Math.Round(stat.WinRate * 100)}% of the time. Consider " +
                    $"promoting this as a default guideline for future {stat.ForkType} decisions.",
                SupportingDataJson: JsonSerializer.Serialize(stat, JsonSerializerOptions.Web),
                Status: FindingStatus.Open,
                CreatedAt: DateTimeOffset.UtcNow), ct).ConfigureAwait(false));
        }

        foreach (var stat in retro.ForkConstraintWinRates)
        {
            var decided = stat.Wins + stat.Losses;
            if (decided < MinSampleSize || stat.WinRate < WinRateThreshold) { continue; }

            var title = $"Prefer \"{stat.Constraint}\" for {stat.ForkType} decisions";
            if (openTitles.Contains(title)) { continue; }

            created.Add(await findings.ProposeAsync(new Finding(
                FindingId: $"finding-{Guid.NewGuid():N}",
                Kind: FindingKind.KnowledgeGuideline,
                Source: FindingSource.Deterministic,
                Title: title,
                Summary: $"\"{stat.Constraint}\" won {stat.Wins} of {decided} decided {stat.ForkType} forks " +
                    $"({Math.Round(stat.WinRate * 100)}%). Consider promoting this as a default choice.",
                SupportingDataJson: JsonSerializer.Serialize(stat, JsonSerializerOptions.Web),
                Status: FindingStatus.Open,
                CreatedAt: DateTimeOffset.UtcNow), ct).ConfigureAwait(false));
        }

        return created;
    }

    // Stopwords/short-word filter for the recurring-steering heuristic below — deliberately tiny
    // and hand-picked, not a real NLP stopword list, since this is just keyword-overlap detection,
    // not language understanding.
    private static readonly HashSet<string> SteeringStopwords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "that", "this", "from", "into", "should", "must", "always",
        "never", "instead", "rather", "than", "use", "using", "used", "not", "but", "all", "any",
    };

    public async Task<IReadOnlyList<Finding>> DetectPromptImprovementsAsync(CancellationToken ct = default)
    {
        var existing = await findings.ListAsync(ct).ConfigureAwait(false);
        var openTitles = existing
            .Where(f => f.Source == FindingSource.Deterministic && f.Status == FindingStatus.Open)
            .Select(f => f.Title)
            .ToHashSet();

        var created = new List<Finding>();

        // Missing verification — among Rejected proposals, how many had zero Test evidence
        // attached to their work unit before being proposed? A high rate suggests Workers should
        // be told to always attach test evidence before calling merge.propose.
        var rejectedProposals = (await merges.ListAsync(sourceBranch: null, ct).ConfigureAwait(false))
            .Where(p => p.Status == MergeProposalStatus.Rejected && p.WorkUnitId is not null)
            .ToList();
        if (rejectedProposals.Count >= MinRejectedProposals)
        {
            var missingEvidenceCount = 0;
            foreach (var p in rejectedProposals)
            {
                var evidence = await evidenceNodes.ListByWorkUnitAsync(p.WorkUnitId!, ct).ConfigureAwait(false);
                if (!evidence.Any(e => e.Kind == EvidenceKind.Test))
                    missingEvidenceCount++;
            }

            var rate = (double)missingEvidenceCount / rejectedProposals.Count;
            if (rate >= MissingEvidenceThreshold)
            {
                var title = "Attach test evidence before proposing a merge";
                if (!openTitles.Contains(title))
                {
                    created.Add(await findings.ProposeAsync(new Finding(
                        FindingId: $"finding-{Guid.NewGuid():N}",
                        Kind: FindingKind.PromptImprovement,
                        Source: FindingSource.Deterministic,
                        Title: title,
                        Summary: $"{missingEvidenceCount} of {rejectedProposals.Count} rejected merge proposals " +
                            $"({Math.Round(rate * 100)}%) had no test evidence attached to their work unit. " +
                            "Consider instructing workers to always attach test evidence before calling merge.propose.",
                        SupportingDataJson: JsonSerializer.Serialize(new
                        {
                            rejectedProposals = rejectedProposals.Count, missingEvidenceCount, rate,
                        }, JsonSerializerOptions.Web),
                        Status: FindingStatus.Open,
                        CreatedAt: DateTimeOffset.UtcNow,
                        TargetStage: PipelineStage.Execute), ct).ConfigureAwait(false));
                }
            }
        }

        // Recurring steering — approximate, keyword-derived (not NLP): tokenize every human
        // steering note's InjectedConstraint text, and flag any keyword that recurs across enough
        // distinct work units to suggest the orchestrator should just be told this up front.
        var steeringRecords = await nodeStore.ReadAllNodesAsync(StudioNodeKind.SteeringDecisionV1, ct).ConfigureAwait(false);
        var keywordToWorkUnits = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, payloadJson) in steeringRecords)
        {
            var decision = JsonSerializer.Deserialize<SteeringDecision>(payloadJson);
            if (decision is null || string.IsNullOrWhiteSpace(decision.InjectedConstraint)) { continue; }

            var words = decision.InjectedConstraint
                .Split([' ', '\t', '\n', '\r', '.', ',', ';', ':', '!', '?', '"', '\''], StringSplitOptions.RemoveEmptyEntries)
                .Select(w => w.ToLowerInvariant())
                .Where(w => w.Length > 4 && !SteeringStopwords.Contains(w));

            foreach (var word in words)
            {
                if (!keywordToWorkUnits.TryGetValue(word, out var set))
                    keywordToWorkUnits[word] = set = [];
                set.Add(decision.WorkUnitId);
            }
        }

        foreach (var (keyword, workUnitIds) in keywordToWorkUnits)
        {
            if (workUnitIds.Count < MinDistinctWorkUnitsForSteeringKeyword) { continue; }

            var title = $"Recurring human steering: \"{keyword}\"";
            if (openTitles.Contains(title)) { continue; }

            created.Add(await findings.ProposeAsync(new Finding(
                FindingId: $"finding-{Guid.NewGuid():N}",
                Kind: FindingKind.PromptImprovement,
                Source: FindingSource.Deterministic,
                Title: title,
                Summary: $"Human steering redirected the orchestrator with constraint text mentioning " +
                    $"\"{keyword}\" on {workUnitIds.Count} distinct work units. Consider addressing this " +
                    "directly in the orchestrator's process so it isn't needed as a manual redirect.",
                SupportingDataJson: JsonSerializer.Serialize(new { keyword, distinctWorkUnits = workUnitIds.Count }, JsonSerializerOptions.Web),
                Status: FindingStatus.Open,
                CreatedAt: DateTimeOffset.UtcNow,
                TargetStage: PipelineStage.Orchestrate), ct).ConfigureAwait(false));
        }

        return created;
    }

    // Phase 11.5 — two co-modification detectors:
    // 1. CoModMiss: a completed WU's FileScope touched pathA but not its high-confidence partner pathB.
    // 2. BoundaryViolation: a co-mod pair crosses architectural layer prefixes (e.g. src/UI + src/Domain).
    public async Task<IReadOnlyList<Finding>> DetectCoModPatternsAsync(CancellationToken ct = default)
    {
        if (coMod is null || string.IsNullOrEmpty(workspaceOptions?.SeedRepositoryPath))
            return [];

        var repositoryId = Path.GetFullPath(workspaceOptions.SeedRepositoryPath);
        var all = await coMod.GetAsync(repositoryId, ct).ConfigureAwait(false);
        if (all.Count == 0) return [];

        var existing = await findings.ListAsync(ct).ConfigureAwait(false);
        var openTitles = existing
            .Where(f => f.Source == FindingSource.Deterministic && f.Status == FindingStatus.Open)
            .Select(f => f.Title)
            .ToHashSet();

        var created = new List<Finding>();

        // ── Boundary violation detector ────────────────────────────────────────
        // Flag pairs where PathA and PathB appear to live in different architectural layers.
        foreach (var pattern in all.Where(p => p.Confidence >= BoundaryViolationConfidenceThreshold
                                               && p.CoModificationCount >= BoundaryViolationMinCount))
        {
            var layerA = DetectLayer(pattern.PathA);
            var layerB = DetectLayer(pattern.PathB);
            if (layerA is null || layerB is null || layerA == layerB) continue;

            var title = $"Cross-layer co-modification: {layerA} ↔ {layerB} ({pattern.CoModificationCount}× in {Math.Round(pattern.Confidence * 100)}% of WUs)";
            if (openTitles.Contains(title)) continue;

            created.Add(await findings.ProposeAsync(new Finding(
                FindingId: $"finding-{Guid.NewGuid():N}",
                Kind: FindingKind.KnowledgeGuideline,
                Source: FindingSource.Deterministic,
                Title: title,
                Summary: $"'{pattern.PathA}' ({layerA}) and '{pattern.PathB}' ({layerB}) were co-modified in " +
                    $"{pattern.CoModificationCount} work units ({Math.Round(pattern.Confidence * 100)}% confidence). " +
                    "Frequent cross-layer co-modification may indicate an eroding architectural boundary. " +
                    "Consider whether these files should be coupled or the boundary should be made explicit.",
                SupportingDataJson: JsonSerializer.Serialize(pattern, JsonSerializerOptions.Web),
                Status: FindingStatus.Open,
                CreatedAt: DateTimeOffset.UtcNow), ct).ConfigureAwait(false));
        }

        // ── Co-modification miss detector ─────────────────────────────────────
        // For each completed work unit with FileScope, check if a high-confidence co-mod partner
        // is absent from the scope — suggesting the planner under-specified the scope.
        if (workUnits is not null)
        {
            var completed = await workUnits.ListAsync(cancellationToken: ct).ConfigureAwait(false);
            foreach (var wu in completed.Where(w => w.Status == WorkUnitStatus.Completed
                                                    && w.FileScope.Count > 0))
            {
                var scopeSet = new HashSet<string>(wu.FileScope, StringComparer.Ordinal);
                var hints = all.Where(p => p.Confidence >= CoModMissConfidenceThreshold
                    && (scopeSet.Any(s => p.PathA.StartsWith(s, StringComparison.Ordinal))
                     || scopeSet.Any(s => p.PathB.StartsWith(s, StringComparison.Ordinal))));

                foreach (var hint in hints)
                {
                    // Determine which path is the partner (the one NOT in scope).
                    var inScope  = scopeSet.Any(s => hint.PathA.StartsWith(s, StringComparison.Ordinal)) ? hint.PathA : hint.PathB;
                    var partner  = inScope == hint.PathA ? hint.PathB : hint.PathA;
                    if (scopeSet.Any(s => partner.StartsWith(s, StringComparison.Ordinal))) continue;

                    var title = $"Likely missed co-modification: '{partner}' (WU {wu.WorkUnitId[..8]})";
                    if (openTitles.Contains(title)) continue;

                    created.Add(await findings.ProposeAsync(new Finding(
                        FindingId: $"finding-{Guid.NewGuid():N}",
                        Kind: FindingKind.PromptImprovement,
                        Source: FindingSource.Deterministic,
                        Title: title,
                        Summary: $"Work unit '{wu.WorkUnitId}' (goal: {wu.Goal}) included '{inScope}' in its scope " +
                            $"but not '{partner}', which is co-modified with it in {Math.Round(hint.Confidence * 100)}% " +
                            "of past work units. Consider whether the planner should include the partner in FileScope.",
                        SupportingDataJson: JsonSerializer.Serialize(new { wu.WorkUnitId, inScope, partner, hint.Confidence }, JsonSerializerOptions.Web),
                        Status: FindingStatus.Open,
                        CreatedAt: DateTimeOffset.UtcNow,
                        TargetStage: PipelineStage.Plan), ct).ConfigureAwait(false));
                }
            }
        }

        return created;
    }

    // Heuristic: infer architectural layer from the first path segment after "src/" or the
    // top-level directory. Returns null when the layer is ambiguous or single-segment.
    private static readonly string[] LayerNames =
        ["ui", "web", "frontend", "domain", "core", "data", "infrastructure", "infra", "api", "shared", "common"];

    private static string? DetectLayer(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var lower = part.ToLowerInvariant();
            if (LayerNames.Contains(lower)) return lower;
        }
        return parts.Length > 1 ? parts[0].ToLowerInvariant() : null;
    }
}
