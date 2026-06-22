using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

// Manual-only ("Detect Findings" button) — never scheduled, never chained automatically after a
// run. Lives in Host rather than Storage or Projections because it needs both IProjectionManager
// (Projections, which already depends on Storage) and IFindingService (Storage) — putting it in
// either of those would create a circular project reference.
public sealed class FindingDetectorService(IProjectionManager projections, IFindingService findings)
{
    private const int MinSampleSize = 5;
    private const double WinRateThreshold = 0.70;

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
}
