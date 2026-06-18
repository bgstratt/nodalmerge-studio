using System.Net.Http.Json;
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
/// Slice 15b — work-unit creation used to be three independent implementations (MCP tool, REST
/// endpoint, agent-loop dispatcher) that had drifted: REST supported parentWorkUnitId/dependsOn/
/// fileScope, MCP/the dispatcher didn't, and both MCP and the dispatcher patched BranchId onto the
/// WorkUnit record *after* IOrchestratorService.CreateWorkUnitAsync had already generated and
/// seeded a real "work-{guid}" branch — orphaning that branch and leaving the caller's chosen
/// branch name unregistered with IBranchService entirely (status "unknown" despite being "in use").
/// These tests prove the shared IWorkUnitCommandService closes both gaps for every transport.
/// </summary>
[Trait("Category", "Integration")]
public class WorkUnitCommandConsolidationTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    [Fact]
    public async Task Explicit_branchId_is_registered_with_IBranchService_not_just_set_on_the_record()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var branches = app.Services.GetRequiredService<IBranchService>();

        var wu = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Implement payment validation", "studio", BranchId: "feature-payment-validation"));

        Assert.Equal("feature-payment-validation", wu.BranchId);
        var status = await branches.GetStatusAsync("feature-payment-validation");
        Assert.Equal("active", status.Status); // previously "unknown" — branch was never created under this name
    }

    [Fact]
    public async Task REST_endpoint_honors_BranchId_ParentWorkUnitId_DependsOn_and_FileScope()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var parentResponse = await client.PostAsJsonAsync("/studio/workunits", new { goal = "parent goal", owner = "test" });
        var parent = await parentResponse.Content.ReadFromJsonAsync<WorkUnit>();

        var childResponse = await client.PostAsJsonAsync("/studio/workunits", new
        {
            goal = "child goal",
            owner = "test",
            branchId = "child-branch",
            parentWorkUnitId = parent!.WorkUnitId,
            dependsOn = new[] { parent.WorkUnitId },
            fileScope = new[] { "src/Foo.cs" },
        });
        var child = await childResponse.Content.ReadFromJsonAsync<WorkUnit>();

        Assert.Equal("child-branch", child!.BranchId);
        Assert.Equal(parent.WorkUnitId, child.ParentWorkUnitId);
        Assert.Equal([parent.WorkUnitId], child.DependsOn);
        Assert.Equal(["src/Foo.cs"], child.FileScope);
    }

    [Fact]
    public async Task MCP_WorkUnitTools_now_supports_the_same_optional_params_as_REST()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var tools = ActivatorUtilities.CreateInstance<WorkUnitTools>(app.Services);
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var branches = app.Services.GetRequiredService<IBranchService>();

        var parentJson = await tools.CreateAsync("parent goal");
        var parentId = System.Text.Json.JsonDocument.Parse(parentJson).RootElement.GetProperty("workUnitId").GetString()!;

        await tools.CreateAsync(
            "child goal",
            branchId: "mcp-child-branch",
            parentWorkUnitId: parentId,
            dependsOn: [parentId],
            fileScope: ["src/Bar.cs"]);

        var children = await workUnits.GetChildrenAsync(parentId);
        var child = Assert.Single(children);
        Assert.Equal("mcp-child-branch", child.BranchId);
        Assert.Equal([parentId], child.DependsOn);
        Assert.Equal(["src/Bar.cs"], child.FileScope);

        var status = await branches.GetStatusAsync("mcp-child-branch");
        Assert.Equal("active", status.Status);
    }
}
