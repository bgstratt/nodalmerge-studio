using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 9e — the AgentWorkspace projection (what an in-flight Worker/Planner/Orchestrator agent
/// actually sees) now includes the detected project roots, so an agent can tell a repo holds more
/// than one project before deciding where a file belongs, instead of only finding out via
/// nm_v1_workspace_list with no stack/boundary context at all.
/// </summary>
[Trait("Category", "Integration")]
public class AgentWorkspaceProjectionProfileTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-projection-profile-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
            });

    [Fact]
    public async Task AgentWorkspace_projection_includes_detected_roots()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var workUnits = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var projections = app.Services.GetRequiredService<IProjectionManager>();

        var wu = await workUnits.CreateAsync(new WorkUnitCreateCommand(
            Goal: "Update the compliment endpoint", Owner: "test-owner", BranchId: "wu-profile-test"));

        await fileWorkspace.WriteAsync(wu.BranchId, "backend/Host.csproj", "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>");
        await fileWorkspace.WriteAsync(wu.BranchId, "frontend/package.json", """{"scripts":{"build":"vite build"}}""");

        var result = await projections.GetAsync(new ProjectionRequest(
            ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: wu.WorkUnitId));

        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(
            result.DataJson, JsonSerializerOptions.Web);

        Assert.NotNull(payload!.Roots);
        Assert.Equal(2, payload.Roots!.Count);
        Assert.Contains(payload.Roots, r => r.RelativePath == "backend" && r.Stack == "dotnet");
        Assert.Contains(payload.Roots, r => r.RelativePath == "frontend" && r.Stack == "npm");
    }
}
