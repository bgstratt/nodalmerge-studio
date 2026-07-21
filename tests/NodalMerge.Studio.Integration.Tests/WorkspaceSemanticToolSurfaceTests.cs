using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class WorkspaceSemanticToolSurfaceTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-semtools-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_rootPath);

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
    public async Task Semantic_definition_endpoint_and_dispatcher_tools_are_reachable()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();

        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var runtimeAssembly = app.Services.GetRequiredService<IAgentRuntimeService>().GetType().Assembly;
        var dispatcherType = runtimeAssembly
            .GetType("NodalMerge.Studio.AgentRuntime.McpToolDispatcher");
        Assert.NotNull(dispatcherType);
        var dispatcher = app.Services.GetService(dispatcherType!);
        Assert.NotNull(dispatcher);
        var dispatch = dispatcherType!.GetMethod("DispatchAsync");
        Assert.NotNull(dispatch);
        await SeedAsync(fileWorkspace);

        var client = app.GetTestClient();

        var restResponse = await client.PostAsJsonAsync(
            "/studio/workspace/symbol/definition?branchId=main",
            new { symbol = "IUserRepository" });
        Assert.Equal(HttpStatusCode.OK, restResponse.StatusCode);

        var restBody = await restResponse.Content.ReadFromJsonAsync<JsonElement>();
        var restLocations = restBody.GetProperty("locations");
        Assert.True(restLocations.GetArrayLength() >= 1);

        var referencesInput = JsonSerializer.SerializeToElement(new
        {
            branchId = "main",
            symbol = "IUserRepository"
        });
        var referencesTask = (Task<string>)dispatch!.Invoke(dispatcher, [
            McpToolNames.WorkspaceSymbolReferences,
            referencesInput,
            null!,
            CancellationToken.None,
            null!
        ])!;
        var referencesResultJson = await referencesTask;
        using var referencesDoc = JsonDocument.Parse(referencesResultJson);
        Assert.False(referencesDoc.RootElement.TryGetProperty("error", out _));
        Assert.True(referencesDoc.RootElement.GetProperty("locations").GetArrayLength() >= 2);

        var implementationInput = JsonSerializer.SerializeToElement(new
        {
            branchId = "main",
            symbol = "IUserRepository"
        });
        var implementationTask = (Task<string>)dispatch.Invoke(dispatcher, [
            McpToolNames.WorkspaceSymbolImplementation,
            implementationInput,
            null!,
            CancellationToken.None,
            null!
        ])!;
        var implementationResultJson = await implementationTask;
        using var implementationDoc = JsonDocument.Parse(implementationResultJson);
        Assert.False(implementationDoc.RootElement.TryGetProperty("error", out _));
        var implLocations = implementationDoc.RootElement.GetProperty("locations");
        Assert.Contains(implLocations.EnumerateArray(), loc =>
        {
            if (loc.TryGetProperty("path", out var lower))
                return lower.GetString() == "src/App/UserRepository.cs";
            if (loc.TryGetProperty("Path", out var upper))
                return upper.GetString() == "src/App/UserRepository.cs";
            return false;
        });
    }

    private static async Task SeedAsync(IFileWorkspaceService fileWorkspace)
    {
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "src/App/App.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        await fileWorkspace.WriteAsync("main", "src/App/IUserRepository.cs", """
            namespace Demo;

            public interface IUserRepository
            {
                string GetName();
            }
            """);
        await fileWorkspace.WriteAsync("main", "src/App/UserRepository.cs", """
            namespace Demo;

            public sealed class UserRepository : IUserRepository
            {
                public string GetName() => "ok";
            }
            """);
        await fileWorkspace.WriteAsync("main", "src/App/UserService.cs", """
            namespace Demo;

            public sealed class UserService(IUserRepository repository)
            {
                private readonly IUserRepository _repository = repository;

                public string Load() => _repository.GetName();
            }
            """);
    }
}
