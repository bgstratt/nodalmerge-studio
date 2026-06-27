using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Phase 16 — Workspace as a first-class aggregate. Cardinality is 1 per Studio instance by
// design: WorkspaceId is always "workspace-default". The point of this phase is that every
// owned entity (Repository, WorkUnit) carries an explicit WorkspaceId rather than relying on
// WorkspaceOptions' singleton shape implicitly. See plans/phase-16-workspace-aggregate.md.
[Trait("Category", "Integration")]
public class WorkspaceAggregateTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    [Fact]
    public async Task GetOrCreateDefaultAsync_is_idempotent()
    {
        var app = BuildTestApp();
        var workspaces = app.Services.GetRequiredService<IWorkspaceRegistryService>();

        var first = await workspaces.GetOrCreateDefaultAsync();
        var second = await workspaces.GetOrCreateDefaultAsync();

        Assert.Equal(first.WorkspaceId, second.WorkspaceId);
        Assert.Equal(WorkspaceRegistryService.DefaultWorkspaceId, first.WorkspaceId);
    }

    [Fact]
    public async Task AttachRepositoryAsync_is_idempotent_and_does_not_duplicate()
    {
        var app = BuildTestApp();
        var workspaces = app.Services.GetRequiredService<IWorkspaceRegistryService>();

        var first = await workspaces.AttachRepositoryAsync("repo-abc");
        var second = await workspaces.AttachRepositoryAsync("repo-abc");

        Assert.Single(first.RepositoryIds);
        Assert.Single(second.RepositoryIds);
        Assert.Equal("repo-abc", second.RepositoryIds[0]);
    }

    [Fact]
    public async Task Registering_a_repository_attaches_it_to_the_default_workspace()
    {
        var app = BuildTestApp();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var workspaces = app.Services.GetRequiredService<IWorkspaceRegistryService>();

        var repo = await repositories.RegisterAsync(@"D:\Repos\Foo", "Foo project");

        var workspace = await workspaces.GetOrCreateDefaultAsync();
        Assert.Contains(repo.RepositoryId, workspace.RepositoryIds);
    }

    [Fact]
    public async Task A_created_WorkUnit_carries_the_default_WorkspaceId()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/studio/workunits", new
        {
            goal = "Add a feature",
            owner = "test",
        });
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var workUnitId = doc.GetProperty("workUnitId").GetString()!;

        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var workUnit = await workUnits.GetAsync(workUnitId);

        Assert.Equal(WorkspaceRegistryService.DefaultWorkspaceId, workUnit!.WorkspaceId);
    }

    [Fact]
    public async Task GET_workspace_capabilities_resolves_a_structured_snapshot()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.GetAsync("/studio/workspace/capabilities");
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(doc.GetProperty("build").GetBoolean());
        Assert.True(doc.GetProperty("test").GetBoolean());
        Assert.False(doc.GetProperty("docFetch").GetBoolean());
    }

    [Fact]
    public void WorkspaceCapabilityResolver_reflects_DocFetchTools_and_EnabledDomainAgents_flags()
    {
        var options = new WorkspaceOptions
        {
            DocFetchTools = true,
            EnabledDomainAgents = ["Security", "Architecture"],
        };

        var capabilities = WorkspaceCapabilityResolver.Resolve(options);

        Assert.True(capabilities.DocFetch);
        Assert.Equal(["Security", "Architecture"], capabilities.EnabledDomainAgents);
    }
}
