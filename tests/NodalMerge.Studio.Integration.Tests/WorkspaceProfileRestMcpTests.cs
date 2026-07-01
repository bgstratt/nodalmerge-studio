using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// Phase 9d — REST surface for the new workspace profile tools (GET /studio/workspace/profile,
/// POST .../profile/rescan) and a real-HTTP round trip for the run/run-stop endpoints wired in
/// Phase 9c (which were previously only exercised via direct service calls, not actual REST).
/// branchId is a query param, not a route segment — same convention as build/test/exec/run
/// (StudioRestEndpoints.cs:399-402: branch ids can contain a literal '/').
///
/// Found along the way: AddInMemoryStorage()'s WorkspaceOptions registration uses
/// TryAddSingleton specifically so a caller-supplied WorkspaceOptions wins — but going through
/// the full StudioWebApplication.Build pipeline, AddStudioServices already registers a
/// (non-Try) production WorkspaceOptions first, so the TryAdd silently no-ops and every REST
/// test sharing this BuildTestApp() pattern would otherwise collide on the single default
/// %TEMP%\studio-workspace directory. Re-registering WorkspaceOptions with a real AddSingleton
/// *after* AddInMemoryStorage (last-registered-wins for non-keyed DI resolution) is what
/// actually isolates each test here.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceProfileRestMcpTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-profile-rest-{Guid.NewGuid():N}");

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
    public async Task Profile_endpoint_detects_roots_over_real_http()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "backend/Host.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var client = app.GetTestClient();
        var profile = await client.GetFromJsonAsync<WorkspaceProfile>("/studio/workspace/profile?branchId=main");

        Assert.NotNull(profile);
        var root = Assert.Single(profile!.Roots);
        Assert.Equal("backend", root.RelativePath);
        Assert.Equal("dotnet", root.Stack);
    }

    [Fact]
    public async Task Rescan_endpoint_bypasses_cache_over_real_http()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "backend/Host.csproj",
            "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");

        var client = app.GetTestClient();
        var first = await client.GetFromJsonAsync<WorkspaceProfile>("/studio/workspace/profile?branchId=main");
        Assert.Single(first!.Roots);

        await fileWorkspace.WriteAsync("main", "frontend/package.json", """{"scripts":{}}""");

        // Still cached — the GET endpoint doesn't force recompute.
        var stillCached = await client.GetFromJsonAsync<WorkspaceProfile>("/studio/workspace/profile?branchId=main");
        Assert.Single(stillCached!.Roots);

        var rescanResponse = await client.PostAsync("/studio/workspace/profile/rescan?branchId=main", null);
        Assert.Equal(HttpStatusCode.OK, rescanResponse.StatusCode);
        var rescanned = await rescanResponse.Content.ReadFromJsonAsync<WorkspaceProfile>();
        Assert.Equal(2, rescanned!.Roots.Count);
    }

    [Fact]
    public async Task Build_endpoint_with_rootPath_only_builds_that_root_over_real_http()
    {
        // Phase 9f — the Merge Review tab's per-root Build button must scope to one root, not
        // rebuild every detected project just because the repo happens to have more than one.
        await using var app = BuildTestApp();
        await app.StartAsync();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "alpha/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await fileWorkspace.WriteAsync("main", "alpha/Program.cs", "System.Console.WriteLine(\"alpha\");");
        await fileWorkspace.WriteAsync("main", "beta/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await fileWorkspace.WriteAsync("main", "beta/Program.cs", "System.Console.WriteLine(\"beta\");");

        var client = app.GetTestClient();
        var response = await client.PostAsJsonAsync(
            "/studio/workspace/build?branchId=main", new { rootPath = "alpha", timeoutSeconds = 120 });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<BranchExecutionResult>();
        var build = Assert.Single(result!.Builds);
        Assert.Equal("alpha", build.ProjectRoot);
        Assert.True(build.Success, build.StdErr);
    }

    [Fact]
    public async Task Run_and_stop_round_trip_over_real_http()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "alpha/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk.Web">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        await fileWorkspace.WriteAsync("main", "alpha/Program.cs", """
            while (true)
            {
                System.Console.WriteLine("tick");
                System.Threading.Thread.Sleep(200);
            }
            """);

        var client = app.GetTestClient();

        var runResponse = await client.PostAsJsonAsync("/studio/workspace/run?branchId=main", new { });
        Assert.Equal(HttpStatusCode.OK, runResponse.StatusCode);
        var results = await runResponse.Content.ReadFromJsonAsync<List<BuildResult>>();
        var result = Assert.Single(results!);
        Assert.True(result.Running);
        Assert.NotNull(result.Pid);

        var stopResponse = await client.PostAsJsonAsync("/studio/workspace/run/stop?branchId=main", new { });
        Assert.Equal(HttpStatusCode.OK, stopResponse.StatusCode);
        var stopBody = await stopResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, stopBody.GetProperty("stopped").GetInt32());
    }
}
