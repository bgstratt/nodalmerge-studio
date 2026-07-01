using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Slice 17a — nm_v1_projection_snapshot_*/nm_v1_projection_compare had no REST route at all
// (MCP/dispatcher only), breaking the "three transports call one service" convention every other
// command follows. These lock in the new REST routes against IProjectionSnapshotService directly.
[Trait("Category", "Integration")]
public class ProjectionSnapshotRestParityTests
{
    // Matches the host's ConfigureHttpJsonOptions (StudioServiceCollectionExtensions.cs): camelCase
    // property names (JsonSerializerOptions.Web) plus JsonStringEnumConverter for enum properties
    // — ReadFromJsonAsync's bare default options expect PascalCase names and numeric enums.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerOptions.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    [Fact]
    public async Task Capture_persists_a_snapshot_retrievable_via_Get()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");

        var client = app.GetTestClient();
        var captureResponse = await client.PostAsJsonAsync(
            "/studio/projections/snapshots", new { workUnitId = wu.WorkUnitId });
        captureResponse.EnsureSuccessStatusCode();
        var captured = await captureResponse.Content.ReadFromJsonAsync<ProjectionSnapshot>(JsonOptions);

        Assert.Equal(wu.WorkUnitId, captured!.WorkUnitId);

        var fetched = await client.GetFromJsonAsync<ProjectionSnapshot>(
            $"/studio/projections/snapshots/{captured.SnapshotId}", JsonOptions);
        Assert.Equal(captured.SnapshotId, fetched!.SnapshotId);
    }

    [Fact]
    public async Task Get_returns_404_for_an_unknown_snapshot()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/studio/projections/snapshots/no-such-snapshot");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task List_filters_by_workUnitId()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var wuA = await orchestrator.CreateWorkUnitAsync("goal A", "test");
        var wuB = await orchestrator.CreateWorkUnitAsync("goal B", "test");
        var client = app.GetTestClient();

        var snapA = (await (await client.PostAsJsonAsync(
            "/studio/projections/snapshots", new { workUnitId = wuA.WorkUnitId }))
            .Content.ReadFromJsonAsync<ProjectionSnapshot>(JsonOptions))!;
        await client.PostAsJsonAsync("/studio/projections/snapshots", new { workUnitId = wuB.WorkUnitId });

        var filtered = await client.GetFromJsonAsync<List<ProjectionSnapshot>>(
            $"/studio/projections/snapshots?workUnitId={wuA.WorkUnitId}", JsonOptions);

        Assert.Single(filtered!, s => s.SnapshotId == snapA.SnapshotId);
    }

    [Fact]
    public async Task Stale_reflects_artifact_invalidation()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-1", ArtifactType.Decision, wu.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wu.WorkUnitId, null));
        var client = app.GetTestClient();

        var snapshot = (await (await client.PostAsJsonAsync(
            "/studio/projections/snapshots", new { workUnitId = wu.WorkUnitId }))
            .Content.ReadFromJsonAsync<ProjectionSnapshot>(JsonOptions))!;

        var freshStaleness = await client.GetFromJsonAsync<ProjectionStaleness>(
            $"/studio/projections/snapshots/{snapshot.SnapshotId}/stale", JsonOptions);
        Assert.False(freshStaleness!.IsStale);

        await artifacts.InvalidateAsync("DEC-1", "library choice was wrong");

        var staleness = await client.GetFromJsonAsync<ProjectionStaleness>(
            $"/studio/projections/snapshots/{snapshot.SnapshotId}/stale", JsonOptions);
        Assert.True(staleness!.IsStale);
        Assert.Contains("DEC-1", staleness.StaleArtifactIds);
    }

    [Fact]
    public async Task Compare_returns_symmetric_diff_for_sibling_snapshots()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var wuA = await orchestrator.CreateWorkUnitAsync("React fork", "test");
        var wuB = await orchestrator.CreateWorkUnitAsync("Vue fork", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-REACT", ArtifactType.Decision, wuA.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wuA.WorkUnitId, null));
        await artifacts.RecordAsync(new ArtifactRef(
            "DEC-VUE", ArtifactType.Decision, wuB.WorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, wuB.WorkUnitId, null));
        var client = app.GetTestClient();

        var snapA = (await (await client.PostAsJsonAsync(
            "/studio/projections/snapshots", new { workUnitId = wuA.WorkUnitId }))
            .Content.ReadFromJsonAsync<ProjectionSnapshot>(JsonOptions))!;
        var snapB = (await (await client.PostAsJsonAsync(
            "/studio/projections/snapshots", new { workUnitId = wuB.WorkUnitId }))
            .Content.ReadFromJsonAsync<ProjectionSnapshot>(JsonOptions))!;

        var comparison = await client.GetFromJsonAsync<ProjectionComparison>(
            $"/studio/projections/snapshots/compare?a={snapA.SnapshotId}&b={snapB.SnapshotId}", JsonOptions);

        Assert.Contains(comparison!.OnlyInA, a => a.ArtifactId == "DEC-REACT");
        Assert.Contains(comparison.OnlyInB, a => a.ArtifactId == "DEC-VUE");
    }

    [Fact]
    public async Task Compare_returns_404_when_a_snapshot_is_unknown()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");
        var client = app.GetTestClient();

        var snap = (await (await client.PostAsJsonAsync(
            "/studio/projections/snapshots", new { workUnitId = wu.WorkUnitId }))
            .Content.ReadFromJsonAsync<ProjectionSnapshot>(JsonOptions))!;

        var response = await client.GetAsync(
            $"/studio/projections/snapshots/compare?a={snap.SnapshotId}&b=no-such-snapshot");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
