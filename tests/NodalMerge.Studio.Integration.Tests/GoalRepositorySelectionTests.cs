using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Multi-repo Phase 1, Part B — repository selection on the simple goal-creation surface
/// (POST /studio/goals, nm_v1_goal_create). The underlying sync/switch mechanism
/// (IRepositorySyncService) already works and is covered by WorkUnitCommandServiceRepositorySyncTests;
/// these tests confirm the goal-creation entry points actually thread RepositoryId/RepositoryPath/
/// NewRepositoryPath through to it, since previously they didn't expose those fields at all.
/// </summary>
[Trait("Category", "Integration")]
public class GoalRepositorySelectionTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-goal-reposelect-{Guid.NewGuid():N}");
    private readonly string _newRepoPath = Path.Combine(Path.GetTempPath(), $"studio-goal-reposelect-new-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repoPath)) Directory.Delete(_repoPath, recursive: true);
        if (Directory.Exists(_newRepoPath)) Directory.Delete(_newRepoPath, recursive: true);
    }

    [Fact]
    public async Task REST_POST_goals_with_RepositoryPath_syncs_main_and_seeds_the_goals_branch()
    {
        Directory.CreateDirectory(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// from repo");

        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var response = await client.PostAsJsonAsync("/studio/goals", new
        {
            goal = "Add a feature",
            repositoryPath = _repoPath,
        });
        response.EnsureSuccessStatusCode();

        var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var branchId = body.GetProperty("branchId").GetString()!;

        Assert.Equal("// from repo", await fileWorkspace.ReadAsync(branchId, "Program.cs"));
    }

    [Fact]
    public async Task REST_POST_goals_with_NewRepositoryPath_creates_and_uses_a_fresh_repo()
    {
        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();

        var response = await client.PostAsJsonAsync("/studio/goals", new
        {
            goal = "Start a new project",
            newRepositoryPath = _newRepoPath,
            newRepositoryLabel = "brand new",
        });
        response.EnsureSuccessStatusCode();

        Assert.True(Directory.Exists(Path.Combine(_newRepoPath, ".git")));

        var all = await repositories.ListAsync();
        Assert.Contains(all, r => r.Path == _newRepoPath && r.Label == "brand new");
    }

    [Fact]
    public async Task MCP_goal_create_with_RepositoryPath_syncs_main_and_seeds_the_goals_branch()
    {
        Directory.CreateDirectory(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// from repo via mcp");

        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());
        var tools = ActivatorUtilities.CreateInstance<GoalTools>(app.Services);
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var json = await tools.CreateAsync("Add a feature via mcp", repositoryPath: _repoPath);
        var doc = JsonDocument.Parse(json).RootElement;
        var branchId = doc.GetProperty("data").GetProperty("branchId").GetString()!;

        Assert.Equal("// from repo via mcp", await fileWorkspace.ReadAsync(branchId, "Program.cs"));
    }

    [Fact]
    public async Task MCP_goal_create_with_NewRepositoryPath_creates_and_uses_a_fresh_repo()
    {
        var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());
        var tools = ActivatorUtilities.CreateInstance<GoalTools>(app.Services);
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();

        var json = await tools.CreateAsync(
            "Start a new project via mcp", newRepositoryPath: _newRepoPath, newRepositoryLabel: "brand new via mcp");
        JsonDocument.Parse(json); // sanity-check the tool returned valid JSON, not an error envelope

        Assert.True(Directory.Exists(Path.Combine(_newRepoPath, ".git")));

        var all = await repositories.ListAsync();
        Assert.Contains(all, r => r.Path == _newRepoPath && r.Label == "brand new via mcp");
    }
}
