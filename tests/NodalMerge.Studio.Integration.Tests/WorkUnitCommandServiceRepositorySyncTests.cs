using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the WorkUnitCommandService.CreateAsync wiring from the Repository-Sync plan: only a
/// fresh top-level goal (no ParentWorkUnitId, no explicit SeedFromBranchId) with a non-empty
/// RepositoryPath triggers IRepositorySyncService.SyncBranchFromRepositoryAsync against "main"
/// before the work unit's own branch is seeded, and every such goal's Metadata is stamped with
/// the workspaceGeneration it was planned against (see RepositorySyncServiceTests for the lower-
/// level sync behavior itself).
/// </summary>
[Trait("Category", "Integration")]
public class WorkUnitCommandServiceRepositorySyncTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-wucs-reposync-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repoPath))
            Directory.Delete(_repoPath, recursive: true);
    }

    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

    private void WriteRepoFile(string relativePath, string content)
    {
        var full = Path.Combine(_repoPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Fresh_top_level_goal_with_a_RepositoryPath_records_a_system_owned_ExternalChangeset_and_stamps_metadata()
    {
        WriteRepoFile("Program.cs", "// v1");
        await using var app = BuildTestApp();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var repositorySync = app.Services.GetRequiredService<IRepositorySyncService>();

        var workUnit = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Add compliments endpoint", "test", RepositoryPath: _repoPath));

        Assert.NotNull(workUnit.Metadata);
        Assert.Equal("1", workUnit.Metadata!["workspaceGeneration"]);
        Assert.True(workUnit.Metadata.ContainsKey("lastExternalChangesetId"));

        var state = await repositorySync.GetStateAsync("main");
        Assert.NotNull(state);
        Assert.Equal(workUnit.Metadata["lastExternalChangesetId"], state!.LatestExternalChangesetId);

        var artifact = await artifacts.GetAsync(state.LatestExternalChangesetId!);
        Assert.NotNull(artifact);
        Assert.Equal(ArtifactType.ExternalChangeset, artifact!.Type);
        Assert.Null(artifact.OwnedByWorkUnitId);
    }

    [Fact]
    public async Task Second_goal_with_no_disk_changes_adds_no_new_artifact_but_still_stamps_the_prior_generation()
    {
        WriteRepoFile("Program.cs", "// v1");
        await using var app = BuildTestApp();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var repositorySync = app.Services.GetRequiredService<IRepositorySyncService>();

        var first = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("First goal", "test", RepositoryPath: _repoPath));
        var firstChangesetId = first.Metadata!["lastExternalChangesetId"];

        var second = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Second goal", "test", RepositoryPath: _repoPath));

        Assert.Equal("1", second.Metadata!["workspaceGeneration"]);
        Assert.Equal(firstChangesetId, second.Metadata["lastExternalChangesetId"]);

        var state = await repositorySync.GetStateAsync("main");
        Assert.Equal(1, state!.Generation);
    }

    [Fact]
    public async Task Child_work_unit_never_triggers_a_sync_even_when_RepositoryPath_is_supplied()
    {
        WriteRepoFile("Program.cs", "// v1");
        await using var app = BuildTestApp();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var repositorySync = app.Services.GetRequiredService<IRepositorySyncService>();

        var parent = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Parent goal", "test", RepositoryPath: _repoPath));

        // Drift the repo after the parent's sync — a child created against it must not re-sync.
        WriteRepoFile("Program.cs", "// v2 — drifted after parent sync");

        var child = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Child goal", "test", RepositoryPath: _repoPath, ParentWorkUnitId: parent.WorkUnitId));

        Assert.True(child.Metadata is null || !child.Metadata.ContainsKey("workspaceGeneration"));

        var state = await repositorySync.GetStateAsync("main");
        Assert.Equal(1, state!.Generation); // unchanged — the drift on disk was never synced
    }
}
