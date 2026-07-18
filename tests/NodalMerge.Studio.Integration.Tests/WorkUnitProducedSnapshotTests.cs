using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/first-class-goals-and-materialization.md Phase 4b — when a work unit reaches Proposed, the
/// WorkUnitProducedSnapshotObserver mints a RepositorySnapshot of its branch state attributed with
/// the work-unit id and source "WorkUnitCompletion", so it can be materialized later.
/// </summary>
[Trait("Category", "Integration")]
public class WorkUnitProducedSnapshotTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-produced-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repoPath)) Directory.Delete(_repoPath, recursive: true);
    }

    [Fact]
    public async Task Proposed_workunit_mints_a_WorkUnitCompletion_snapshot_attributed_to_it()
    {
        Directory.CreateDirectory(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// seed content");

        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                // Give the observer (and snapshot service) a CAS — without a blob store it skips minting.
                services.AddSingleton<IBlobStoreProvider>(new InMemoryBlobStoreProvider());
            });
        await app.StartAsync();
        var client = app.GetTestClient();
        var snapshots = app.Services.GetRequiredService<IRepositorySnapshotService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var bus = app.Services.GetRequiredService<IParticipantEventBus>();

        var createResp = await client.PostAsJsonAsync("/studio/workunits",
            new { goal = "Build the thing", owner = "user", repositoryPath = _repoPath });
        createResp.EnsureSuccessStatusCode();
        var wuId = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync())
            .RootElement.GetProperty("workUnitId").GetString()!;

        var wu = await workUnits.GetAsync(wuId);
        var casRepoId = await repositories.ResolveCasIdentityAsync(wu!.RepositoryId);
        Assert.False(string.IsNullOrEmpty(casRepoId)); // precondition: the repo resolves a CAS identity

        // Drive the Proposed transition the observer listens for.
        bus.Publish(new WorkUnitStatusChangedEvent(wuId, WorkUnitStatus.Proposed, WorkUnitStatus.Proposed, DateTimeOffset.UtcNow));

        // The bus dispatches handlers fire-and-forget (Task.Run), so poll for the mint.
        RepositorySnapshot? produced = null;
        for (var i = 0; i < 60 && produced is null; i++)
        {
            await Task.Delay(50);
            var latest = await snapshots.GetLatestAsync(casRepoId!);
            if (latest is { Source: WorkUnitProducedSnapshotObserver.SnapshotSource } && latest.WorkUnitId == wuId)
                produced = latest;
        }

        Assert.NotNull(produced);
        Assert.Equal(wuId, produced!.WorkUnitId);
        Assert.Equal(WorkUnitProducedSnapshotObserver.SnapshotSource, produced.Source);

        // 4d — the materialization resolver surfaces the produced snapshot (and the goal's base).
        var anchors = JsonDocument.Parse(
            await client.GetStringAsync($"/studio/workunits/{wuId}/materialization")).RootElement;
        Assert.Equal(produced.SnapshotId, anchors.GetProperty("producedSnapshotId").GetString());
        // The root work unit is its own goal; the goal's base was stamped at auto-goal creation.
        Assert.Equal(wuId, anchors.GetProperty("goalId").GetString());
    }
}
