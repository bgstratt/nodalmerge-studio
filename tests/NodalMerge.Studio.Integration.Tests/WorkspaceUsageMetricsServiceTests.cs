using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 14 — WorkspaceUsageMetricsService aggregates WorkspaceSearchExecuted/WorkspaceReadExecuted/
/// FileLeaseContended events (computed on demand, no persistence of its own) into the evidence
/// future Phase-12 leasing decisions are meant to hinge on.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceUsageMetricsServiceTests
{
    private static (IExecutionEventStream Events, IWorkspaceUsageMetricsService Metrics) Build()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        return (events, new WorkspaceUsageMetricsService(events));
    }

    [Fact]
    public async Task GetTopFileHitsAsync_ranks_paths_by_combined_search_and_read_hits()
    {
        var (events, metrics) = Build();

        await events.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkspaceSearchExecuted,
            new WorkspaceSearchExecutedPayload("Foo", ["a.cs", "b.cs"], 2, false));
        await events.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkspaceReadExecuted,
            new WorkspaceReadExecutedPayload(["a.cs"]));
        await events.AppendAsync("SES-1", "WU-2", ExecutionEventKind.WorkspaceReadExecuted,
            new WorkspaceReadExecutedPayload(["a.cs", "c.cs"]));

        var topHits = await metrics.GetTopFileHitsAsync();

        Assert.Equal("a.cs", topHits[0].Path);
        Assert.Equal(3, topHits[0].Hits); // 1 search match + 2 reads
        Assert.Contains(topHits, h => h.Path == "b.cs" && h.Hits == 1);
        Assert.Contains(topHits, h => h.Path == "c.cs" && h.Hits == 1);
    }

    [Fact]
    public async Task GetTopFileHitsAsync_respects_topN()
    {
        var (events, metrics) = Build();
        await events.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkspaceReadExecuted,
            new WorkspaceReadExecutedPayload(["a.cs"]));
        await events.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkspaceReadExecuted,
            new WorkspaceReadExecutedPayload(["b.cs"]));

        var topHits = await metrics.GetTopFileHitsAsync(topN: 1);

        Assert.Single(topHits);
    }

    [Fact]
    public async Task GetLeaseContentionHotSpotsAsync_tallies_per_path_counts_and_contending_work_units()
    {
        var (events, metrics) = Build();

        await events.AppendAsync("SES-1", "WU-1", ExecutionEventKind.FileLeaseContended,
            new FileLeaseContendedPayload("src/Shared.cs", "WU-1", "WU-0"));
        await events.AppendAsync("SES-1", "WU-2", ExecutionEventKind.FileLeaseContended,
            new FileLeaseContendedPayload("src/Shared.cs", "WU-2", "WU-0"));
        await events.AppendAsync("SES-1", "WU-3", ExecutionEventKind.FileLeaseContended,
            new FileLeaseContendedPayload("src/Other.cs", "WU-3", "WU-0"));

        var hotSpots = await metrics.GetLeaseContentionHotSpotsAsync();

        var shared = Assert.Single(hotSpots, h => h.Path == "src/Shared.cs");
        Assert.Equal(2, shared.ContentionCount);
        Assert.Contains("WU-1", shared.ContendingWorkUnitIds);
        Assert.Contains("WU-2", shared.ContendingWorkUnitIds);

        var other = Assert.Single(hotSpots, h => h.Path == "src/Other.cs");
        Assert.Equal(1, other.ContentionCount);
    }

    [Fact]
    public async Task GetSearchUsageAsync_scopes_by_workUnitId_and_tallies_matches_and_truncation()
    {
        var (events, metrics) = Build();

        await events.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkspaceSearchExecuted,
            new WorkspaceSearchExecutedPayload("Foo", ["a.cs", "b.cs"], 2, false));
        await events.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkspaceSearchExecuted,
            new WorkspaceSearchExecutedPayload("Bar", ["c.cs"], 1, true));
        await events.AppendAsync("SES-1", "WU-2", ExecutionEventKind.WorkspaceSearchExecuted,
            new WorkspaceSearchExecutedPayload("Baz", ["d.cs"], 1, false));

        var wu1Usage = await metrics.GetSearchUsageAsync("WU-1");
        Assert.Equal(2, wu1Usage.SearchCount);
        Assert.Equal(3, wu1Usage.TotalMatches);
        Assert.Equal(1, wu1Usage.TruncatedCount);

        var allUsage = await metrics.GetSearchUsageAsync();
        Assert.Equal(3, allUsage.SearchCount);
    }

    [Fact]
    public async Task GetSearchUsageAsync_since_filter_excludes_earlier_events()
    {
        var (events, metrics) = Build();
        await events.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkspaceSearchExecuted,
            new WorkspaceSearchExecutedPayload("Foo", ["a.cs"], 1, false));

        var cutoff = DateTimeOffset.UtcNow.AddMinutes(1);
        var usage = await metrics.GetSearchUsageAsync(since: cutoff);

        Assert.Equal(0, usage.SearchCount);
    }
}
