using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Capability-gap fix — informational registry of known repository paths. Does not change which
// repository is actually seeded (WorkspaceOptions.SeedRepositoryPath); see RepositoryRegistryService.
[Trait("Category", "Integration")]
public class RepositoryRegistryTests
{
    private static (IRepositoryRegistryService registry, IServiceProvider services) BuildServices()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());
        return (app.Services.GetRequiredService<IRepositoryRegistryService>(), app.Services);
    }

    [Fact]
    public async Task RegisterAsync_then_ListAsync_round_trips()
    {
        var (registry, _) = BuildServices();

        var registered = await registry.RegisterAsync(@"D:\Repos\Foo", "Foo project");

        var all = await registry.ListAsync();
        var found = Assert.Single(all);
        Assert.Equal(registered.RepositoryId, found.RepositoryId);
        Assert.Equal(@"D:\Repos\Foo", found.Path);
        Assert.Equal("Foo project", found.Label);
    }

    [Fact]
    public async Task RegisterAsync_is_idempotent_by_normalized_path()
    {
        var (registry, _) = BuildServices();

        var first = await registry.RegisterAsync(@"D:\Repos\Foo", "Foo project");
        var second = await registry.RegisterAsync("D:/Repos/Foo/", "Foo project, re-registered");

        Assert.Equal(first.RepositoryId, second.RepositoryId);
        var all = await registry.ListAsync();
        Assert.Single(all);
    }

    [Fact]
    public async Task MCP_RepositoryTools_register_and_list_round_trip()
    {
        var (registry, services) = BuildServices();
        var tools = ActivatorUtilities.CreateInstance<RepositoryTools>(services);

        var registerJson = await tools.RegisterAsync(@"D:\Repos\Bar", "Bar project");
        var registerDoc = JsonDocument.Parse(registerJson).RootElement;
        var repositoryId = registerDoc.GetProperty("data").GetProperty("repositoryId").GetString();

        var listJson = await tools.ListAsync();
        var listDoc = JsonDocument.Parse(listJson).RootElement;
        // ListAsync returns the RepositoryV1 records directly (not an anonymous lowercase-property
        // wrapper like RegisterAsync), so System.Text.Json's default serialization keeps the
        // record's PascalCase property names.
        var ids = listDoc.GetProperty("data").EnumerateArray()
            .Select(e => e.GetProperty("RepositoryId").GetString()).ToList();

        Assert.Contains(repositoryId, ids);

        var direct = await registry.ListAsync();
        Assert.Single(direct);
    }
}
