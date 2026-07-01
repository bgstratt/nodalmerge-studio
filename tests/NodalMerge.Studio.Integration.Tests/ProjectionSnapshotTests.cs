using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// First concrete slice of the Projection-as-persisted-object refactor — captures
// ProjectionManager's otherwise-ephemeral AgentWorkspace resolution as an immutable snapshot keyed
// off WorkUnitId, detects staleness via the artifact-invalidation cascade (ArtifactInvalidationTests),
// and compares sibling snapshots with a symmetric diff (distinct from ProjectionDelta's temporal,
// same-work-unit semantics).
[Trait("Category", "Integration")]
public class ProjectionSnapshotTests
{
    private static (IOrchestratorService orchestrator, IArtifactLineageService artifacts, IProjectionSnapshotService snapshots, IServiceProvider services) BuildServices()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());
        return (
            app.Services.GetRequiredService<IOrchestratorService>(),
            app.Services.GetRequiredService<IArtifactLineageService>(),
            app.Services.GetRequiredService<IProjectionSnapshotService>(),
            app.Services);
    }

    [Fact]
    public async Task CaptureAsync_persists_a_snapshot_matching_the_resolved_projection()
    {
        var (orchestrator, artifacts, snapshots, _) = BuildServices();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-1", ArtifactType.Decision, wu.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wu.WorkUnitId, null));

        var snapshot = await snapshots.CaptureAsync(wu.WorkUnitId);

        Assert.Equal(wu.WorkUnitId, snapshot.WorkUnitId);
        Assert.Contains(snapshot.Payload.Artifacts.Artifacts, a => a.ArtifactId == "DEC-1");
        Assert.Contains(snapshot.Payload.Artifacts.Artifacts, a => a.ArtifactId == wu.WorkUnitId); // the Goal artifact itself
    }

    [Fact]
    public async Task GetAsync_and_ListAsync_round_trip_filtered_and_unfiltered()
    {
        var (orchestrator, _, snapshots, _) = BuildServices();
        var wuA = await orchestrator.CreateWorkUnitAsync("goal A", "test");
        var wuB = await orchestrator.CreateWorkUnitAsync("goal B", "test");

        var snapA = await snapshots.CaptureAsync(wuA.WorkUnitId);
        var snapB = await snapshots.CaptureAsync(wuB.WorkUnitId);

        Assert.Equal(snapA, await snapshots.GetAsync(snapA.SnapshotId));

        var allForA = await snapshots.ListAsync(wuA.WorkUnitId);
        Assert.Single(allForA, s => s.SnapshotId == snapA.SnapshotId);

        var all = await snapshots.ListAsync();
        Assert.Contains(all, s => s.SnapshotId == snapA.SnapshotId);
        Assert.Contains(all, s => s.SnapshotId == snapB.SnapshotId);
    }

    [Fact]
    public async Task CheckStaleAsync_is_false_until_a_referenced_artifact_is_invalidated()
    {
        var (orchestrator, artifacts, snapshots, _) = BuildServices();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-1", ArtifactType.Decision, wu.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wu.WorkUnitId, null));

        var snapshot = await snapshots.CaptureAsync(wu.WorkUnitId);
        Assert.False((await snapshots.CheckStaleAsync(snapshot.SnapshotId)).IsStale);

        await artifacts.InvalidateAsync("DEC-1", "library choice was wrong");

        var staleness = await snapshots.CheckStaleAsync(snapshot.SnapshotId);
        Assert.True(staleness.IsStale);
        Assert.Contains("DEC-1", staleness.StaleArtifactIds);
    }

    [Fact]
    public async Task CompareAsync_treats_sibling_only_artifacts_as_OnlyInA_or_OnlyInB_not_added_removed()
    {
        var (orchestrator, artifacts, snapshots, _) = BuildServices();
        var wuA = await orchestrator.CreateWorkUnitAsync("React fork", "test");
        var wuB = await orchestrator.CreateWorkUnitAsync("Vue fork", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-REACT", ArtifactType.Decision, wuA.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wuA.WorkUnitId, null));
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-VUE", ArtifactType.Decision, wuB.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wuB.WorkUnitId, null));

        var snapA = await snapshots.CaptureAsync(wuA.WorkUnitId);
        var snapB = await snapshots.CaptureAsync(wuB.WorkUnitId);

        var comparison = await snapshots.CompareAsync(snapA.SnapshotId, snapB.SnapshotId);

        Assert.Contains(comparison.OnlyInA, a => a.ArtifactId == "DEC-REACT");
        Assert.Contains(comparison.OnlyInB, a => a.ArtifactId == "DEC-VUE");
        Assert.DoesNotContain(comparison.OnlyInA, a => a.ArtifactId == "DEC-VUE");
        Assert.DoesNotContain(comparison.OnlyInB, a => a.ArtifactId == "DEC-REACT");
    }

    [Fact]
    public async Task CompareAsync_surfaces_status_divergence_for_a_shared_artifact_id()
    {
        var (orchestrator, artifacts, snapshots, _) = BuildServices();
        var wuA = await orchestrator.CreateWorkUnitAsync("goal A", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-SHARED", ArtifactType.Decision, wuA.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wuA.WorkUnitId, null));

        // Two captures of the SAME work unit, before and after the artifact is invalidated — gives
        // a shared artifact id with diverging status across the two snapshots, exercising
        // DifferingStatus without needing two separate work units for this case.
        var snapA = await snapshots.CaptureAsync(wuA.WorkUnitId);
        await artifacts.InvalidateAsync("DEC-SHARED", "no longer applies");
        var snapB = await snapshots.CaptureAsync(wuA.WorkUnitId);

        var comparison = await snapshots.CompareAsync(snapA.SnapshotId, snapB.SnapshotId);

        var divergence = Assert.Single(comparison.DifferingStatus, d => d.ArtifactId == "DEC-SHARED");
        Assert.Equal(ArtifactStatus.Active, divergence.StatusA);
        Assert.Equal(ArtifactStatus.Invalidated, divergence.StatusB);
    }

    [Fact]
    public async Task MCP_round_trip_capture_get_compare()
    {
        var (orchestrator, artifacts, _, services) = BuildServices();
        var projectionTools = ActivatorUtilities.CreateInstance<ProjectionTools>(services);
        var wuA = await orchestrator.CreateWorkUnitAsync("goal A", "test");
        var wuB = await orchestrator.CreateWorkUnitAsync("goal B", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-A", ArtifactType.Decision, wuA.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wuA.WorkUnitId, null));

        var captureAJson = await projectionTools.SnapshotCaptureAsync(wuA.WorkUnitId);
        var captureBJson = await projectionTools.SnapshotCaptureAsync(wuB.WorkUnitId);
        var snapshotIdA = System.Text.Json.JsonDocument.Parse(captureAJson).RootElement.GetProperty("data").GetProperty("snapshotId").GetString()!;
        var snapshotIdB = System.Text.Json.JsonDocument.Parse(captureBJson).RootElement.GetProperty("data").GetProperty("snapshotId").GetString()!;

        var getJson = await projectionTools.SnapshotGetAsync(snapshotIdA);
        var getDoc = System.Text.Json.JsonDocument.Parse(getJson).RootElement;
        Assert.Equal(wuA.WorkUnitId, getDoc.GetProperty("data").GetProperty("WorkUnitId").GetString());

        var compareJson = await projectionTools.CompareAsync(snapshotIdA, snapshotIdB);
        var compareDoc = System.Text.Json.JsonDocument.Parse(compareJson).RootElement;
        var onlyInA = compareDoc.GetProperty("data").GetProperty("OnlyInA").EnumerateArray()
            .Select(e => e.GetProperty("ArtifactId").GetString()).ToList();
        Assert.Contains("DEC-A", onlyInA);
    }
}
