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
public class WorkUnitProducedSnapshotTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-produced-{Guid.NewGuid():N}");
    private readonly string _materializePath = Path.Combine(Path.GetTempPath(), $"studio-produced-out-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_repoPath, _materializePath);

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

    // Regression: the produced-snapshot mint used to read each branch file as a UTF-8 string, which
    // (a) corrupted binaries via the round-trip and (b) threw on files over MaxReadBytes (512 KB),
    // aborting the whole mint. A binary file and a >512 KB file must now survive byte-for-byte from
    // branch → produced snapshot → materialize.
    [Fact]
    public async Task Produced_snapshot_preserves_binary_and_large_files_byte_for_byte()
    {
        Directory.CreateDirectory(_repoPath);
        Directory.CreateDirectory(Path.Combine(_repoPath, "assets"));
        Directory.CreateDirectory(Path.Combine(_repoPath, "data"));
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// seed content");

        // A small binary whose bytes are deliberately invalid UTF-8 (PNG magic + 0xFF/0xFE/0x80…),
        // so a UTF-8 round-trip would replace them with U+FFFD and change both content and length.
        var pngBytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xFF, 0xFE, 0x80, 0x00, 0x01, 0xC0, 0xFF };
        await File.WriteAllBytesAsync(Path.Combine(_repoPath, "assets", "logo.png"), pngBytes);

        // A >512 KB file (MaxReadBytes = 512 KB): the old text ReadAsync threw on this outright.
        var largeBytes = new byte[700 * 1024];
        for (var i = 0; i < largeBytes.Length; i++) largeBytes[i] = (byte)(i * 31 + 7);
        await File.WriteAllBytesAsync(Path.Combine(_repoPath, "data", "blob.bin"), largeBytes);

        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
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
        Assert.False(string.IsNullOrEmpty(casRepoId));

        bus.Publish(new WorkUnitStatusChangedEvent(wuId, WorkUnitStatus.Proposed, WorkUnitStatus.Proposed, DateTimeOffset.UtcNow));

        RepositorySnapshot? produced = null;
        for (var i = 0; i < 60 && produced is null; i++)
        {
            await Task.Delay(50);
            var latest = await snapshots.GetLatestAsync(casRepoId!);
            if (latest is { Source: WorkUnitProducedSnapshotObserver.SnapshotSource } && latest.WorkUnitId == wuId)
                produced = latest;
        }
        Assert.NotNull(produced); // pre-fix, the >512 KB file threw and no produced snapshot was ever minted.

        // Materialize the produced snapshot to a fresh dir and assert byte-exact round-trip.
        var matResp = await client.PostAsync(
            $"/studio/repository-snapshots/{produced!.SnapshotId}/materialize?targetPath={Uri.EscapeDataString(_materializePath)}",
            content: null);
        matResp.EnsureSuccessStatusCode();

        var producedPng = await File.ReadAllBytesAsync(Path.Combine(_materializePath, "assets", "logo.png"));
        Assert.Equal(pngBytes, producedPng);

        var producedBlob = await File.ReadAllBytesAsync(Path.Combine(_materializePath, "data", "blob.bin"));
        Assert.Equal(largeBytes, producedBlob);
    }
}
