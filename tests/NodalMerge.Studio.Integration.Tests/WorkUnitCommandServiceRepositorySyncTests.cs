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

/// <summary>
/// Covers the WorkUnitCommandService.CreateAsync wiring from the Repository-Sync plan: only a
/// fresh top-level goal (no ParentWorkUnitId, no explicit SeedFromBranchId) with a non-empty
/// RepositoryPath triggers IRepositorySyncService.SyncBranchFromRepositoryAsync against "main"
/// before the work unit's own branch is seeded, and every such goal's Metadata is stamped with
/// the workspaceGeneration it was planned against (see RepositorySyncServiceTests for the lower-
/// level sync behavior itself).
/// </summary>
[Trait("Category", "Integration")]
public class WorkUnitCommandServiceRepositorySyncTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-wucs-reposync-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_repoPath);

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

    [Fact]
    public async Task Fresh_top_level_goal_with_a_RepositoryId_resolves_the_registered_path_and_persists_the_id()
    {
        WriteRepoFile("Program.cs", "// v1");
        await using var app = BuildTestApp();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var repository = await repositories.RegisterAsync(_repoPath, "test repo");

        var workUnit = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Add compliments endpoint", "test", RepositoryId: repository.RepositoryId));

        Assert.Equal(repository.RepositoryId, workUnit.RepositoryId);
        Assert.NotNull(workUnit.Metadata);
        Assert.Equal("1", workUnit.Metadata!["workspaceGeneration"]);

        var content = await fileWorkspace.ReadAsync(workUnit.BranchId, "Program.cs");
        Assert.Equal("// v1", content);
    }

    [Fact]
    public async Task CreateAsync_throws_for_an_unknown_RepositoryId()
    {
        await using var app = BuildTestApp();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();

        await Assert.ThrowsAsync<KeyNotFoundException>(() => workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("goal", "test", RepositoryId: "no-such-repo")));
    }

    [Fact]
    public async Task Two_fresh_top_level_goals_targeting_different_registered_repos_each_get_their_own_content()
    {
        var repoAPath = Path.Combine(Path.GetTempPath(), $"studio-wucs-reposync-a-{Guid.NewGuid():N}");
        var repoBPath = Path.Combine(Path.GetTempPath(), $"studio-wucs-reposync-b-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repoAPath);
            Directory.CreateDirectory(repoBPath);
            File.WriteAllText(Path.Combine(repoAPath, "Program.cs"), "// repo A");
            File.WriteAllText(Path.Combine(repoBPath, "Program.cs"), "// repo B");

            await using var app = BuildTestApp();
            var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
            var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
            var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
            var repoA = await repositories.RegisterAsync(repoAPath, "repo A");
            var repoB = await repositories.RegisterAsync(repoBPath, "repo B");

            var workUnitA = await workUnitCommands.CreateAsync(
                new WorkUnitCreateCommand("Goal against repo A", "test", RepositoryId: repoA.RepositoryId));
            var workUnitB = await workUnitCommands.CreateAsync(
                new WorkUnitCreateCommand("Goal against repo B", "test", RepositoryId: repoB.RepositoryId));

            Assert.Equal("// repo A", await fileWorkspace.ReadAsync(workUnitA.BranchId, "Program.cs"));
            Assert.Equal("// repo B", await fileWorkspace.ReadAsync(workUnitB.BranchId, "Program.cs"));
        }
        finally
        {
            if (Directory.Exists(repoAPath)) Directory.Delete(repoAPath, recursive: true);
            if (Directory.Exists(repoBPath)) Directory.Delete(repoBPath, recursive: true);
        }
    }

    [Fact]
    public async Task REST_POST_workunits_with_an_unknown_RepositoryId_returns_404_and_a_known_one_returns_200_with_RepositoryId_set()
    {
        WriteRepoFile("Program.cs", "// v1");
        await using var app = StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();

        var badResponse = await client.PostAsJsonAsync("/studio/workunits", new
        {
            goal = "goal against unknown repo",
            owner = "test",
            repositoryId = "no-such-repo",
        });
        Assert.Equal(HttpStatusCode.NotFound, badResponse.StatusCode);

        var repository = await repositories.RegisterAsync(_repoPath, "test repo");
        var goodResponse = await client.PostAsJsonAsync("/studio/workunits", new
        {
            goal = "goal against registered repo",
            owner = "test",
            repositoryId = repository.RepositoryId,
        });
        Assert.Equal(HttpStatusCode.OK, goodResponse.StatusCode);

        var json = await goodResponse.Content.ReadAsStringAsync();
        var body = System.Text.Json.JsonDocument.Parse(json).RootElement;
        Assert.Equal(repository.RepositoryId, body.GetProperty("repositoryId").GetString());
    }
}
