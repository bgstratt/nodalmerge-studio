using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Multi-repo Phase 2, Part B — a deliberate "switch active workspace" action
/// (POST /studio/workspace/switch, nm_v1_workspace_switch). The underlying sync mechanism
/// (IRepositorySyncService) already detects SyncReason.RepositorySwitch when the path changes;
/// these tests confirm the new standalone action actually reaches it and updates the global
/// default (WorkspaceOptions.SeedRepositoryPath) that repo-less goals/proposals fall back to.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceSwitchTests : IDisposable
{
    private readonly string _repoAPath = Path.Combine(Path.GetTempPath(), $"studio-switch-a-{Guid.NewGuid():N}");
    private readonly string _repoBPath = Path.Combine(Path.GetTempPath(), $"studio-switch-b-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repoAPath)) Directory.Delete(_repoAPath, recursive: true);
        if (Directory.Exists(_repoBPath)) Directory.Delete(_repoBPath, recursive: true);
    }

    [Fact]
    public async Task REST_switch_twice_between_two_repos_updates_sync_state_and_the_global_default()
    {
        Directory.CreateDirectory(_repoAPath);
        Directory.CreateDirectory(_repoBPath);
        await File.WriteAllTextAsync(Path.Combine(_repoAPath, "Program.cs"), "// repo A");
        await File.WriteAllTextAsync(Path.Combine(_repoBPath, "Program.cs"), "// repo B");

        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();
        var repositorySync = app.Services.GetRequiredService<IRepositorySyncService>();
        var options = app.Services.GetRequiredService<WorkspaceOptions>();

        var first = await client.PostAsJsonAsync("/studio/workspace/switch", new { repositoryPath = _repoAPath });
        first.EnsureSuccessStatusCode();
        Assert.Equal(_repoAPath, options.SeedRepositoryPath);

        var second = await client.PostAsJsonAsync("/studio/workspace/switch", new { repositoryPath = _repoBPath });
        second.EnsureSuccessStatusCode();
        Assert.Equal(_repoBPath, options.SeedRepositoryPath);

        var state = await repositorySync.GetStateAsync("main");
        Assert.NotNull(state);
        Assert.Equal(_repoBPath, state!.RepositoryPath);
    }

    [Fact]
    public async Task REST_switch_with_neither_repositoryId_nor_repositoryPath_returns_400()
    {
        await using var app = StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/studio/workspace/switch", new { });
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task MCP_workspace_switch_resolves_RepositoryId_and_syncs()
    {
        Directory.CreateDirectory(_repoAPath);
        await File.WriteAllTextAsync(Path.Combine(_repoAPath, "Program.cs"), "// repo A via mcp");

        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var options = app.Services.GetRequiredService<WorkspaceOptions>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var repository = await repositories.RegisterAsync(_repoAPath, "repo A");

        var tools = ActivatorUtilities.CreateInstance<WorkspaceTools>(app.Services);
        var json = await tools.SwitchAsync(repositoryId: repository.RepositoryId);
        JsonDocument.Parse(json);

        Assert.Equal(_repoAPath, options.SeedRepositoryPath);
        Assert.Equal("// repo A via mcp", await fileWorkspace.ReadAsync("main", "Program.cs"));
    }
}
