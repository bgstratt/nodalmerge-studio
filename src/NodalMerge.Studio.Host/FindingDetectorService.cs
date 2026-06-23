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
    IStudioNodeStore nodeStore)
{
    private const int MinSampleSize = 5;
    private const double WinRateThreshold = 0.70;
    private const int MinRejectedProposals = 5;
    private const double MissingEvidenceThreshold = 0.50;
    private const int MinDistinctWorkUnitsForSteeringKeyword = 3;

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
}
