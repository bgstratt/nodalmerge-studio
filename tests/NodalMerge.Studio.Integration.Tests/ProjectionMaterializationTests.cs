using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Slice 20 — IProjectionSnapshotService.MaterializeAsync runs a build/test execution against a
// work unit's branch and immediately captures a snapshot of the result as one atomic call, so the
// captured Payload.Execution always reflects the run that was just requested rather than whatever
// happened to be "latest" for the branch by the time of a second, separate capture call.
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class ProjectionMaterializationTests
{
    private static (IOrchestratorService orchestrator, IProjectionSnapshotService snapshots, IServiceProvider services) BuildServices()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());
        return (
            app.Services.GetRequiredService<IOrchestratorService>(),
            app.Services.GetRequiredService<IProjectionSnapshotService>(),
            app.Services);
    }

    [Fact]
    public async Task MaterializeAsync_runs_the_requested_execution_and_captures_a_matching_snapshot()
    {
        var (orchestrator, snapshots, _) = BuildServices();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");

        var materialization = await snapshots.MaterializeAsync(
            wu.WorkUnitId, new WorkspaceExecutionRequest(Build: true, BuildCommand: "exit 0", TimeoutSeconds: 30));

        Assert.True(materialization.Execution.AllSucceeded);
        Assert.Equal(wu.WorkUnitId, materialization.Snapshot.WorkUnitId);
        Assert.NotNull(materialization.Snapshot.Payload.Execution);
        Assert.Equal(materialization.Execution.AllSucceeded, materialization.Snapshot.Payload.Execution!.AllSucceeded);
        Assert.Equal(materialization.Execution.ExecutedAt, materialization.Snapshot.Payload.Execution!.ExecutedAt);
    }

    [Fact]
    public async Task MaterializeAsync_reflects_the_just_run_execution_not_a_stale_prior_one()
    {
        var (orchestrator, snapshots, _) = BuildServices();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");

        var first = await snapshots.MaterializeAsync(
            wu.WorkUnitId, new WorkspaceExecutionRequest(Build: true, BuildCommand: "exit 0", TimeoutSeconds: 30));

        var second = await snapshots.MaterializeAsync(
            wu.WorkUnitId, new WorkspaceExecutionRequest(Build: true, BuildCommand: "exit 1", TimeoutSeconds: 30));

        Assert.True(first.Execution.AllSucceeded);
        Assert.False(second.Execution.AllSucceeded);
        Assert.False(second.Snapshot.Payload.Execution!.AllSucceeded);
        Assert.True(second.Execution.ExecutedAt >= first.Execution.ExecutedAt);
    }

    [Fact]
    public async Task MaterializeAsync_throws_for_an_unknown_work_unit()
    {
        var (_, snapshots, _) = BuildServices();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => snapshots.MaterializeAsync("no-such-work-unit"));
    }

    [Fact]
    public async Task REST_POST_projections_materialize_returns_404_for_an_unknown_work_unit_and_200_with_a_real_one()
    {
        await using var app = StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");

        var badResponse = await client.PostAsJsonAsync("/studio/projections/materialize", new
        {
            workUnitId = "no-such-work-unit",
            build = true,
            buildCommand = "exit 0",
        });
        Assert.Equal(HttpStatusCode.NotFound, badResponse.StatusCode);

        var goodResponse = await client.PostAsJsonAsync("/studio/projections/materialize", new
        {
            workUnitId = wu.WorkUnitId,
            build = true,
            buildCommand = "exit 0",
        });
        Assert.Equal(HttpStatusCode.OK, goodResponse.StatusCode);

        var json = await goodResponse.Content.ReadAsStringAsync();
        var body = System.Text.Json.JsonDocument.Parse(json).RootElement;
        Assert.True(body.TryGetProperty("execution", out _));
        Assert.True(body.TryGetProperty("snapshot", out var snapshotElement));
        Assert.Equal(wu.WorkUnitId, snapshotElement.GetProperty("workUnitId").GetString());
    }
}
