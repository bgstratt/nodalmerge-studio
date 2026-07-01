using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Cross-repo file reference — a goal/work unit can carry read-only {repositoryId, path} pointers
/// into a *different* registered repository (WorkUnit.ReferenceFiles), distinct from FileScope
/// (which gates writes). Content is resolved lazily via IRepositoryRegistryService.ReadFileAsync/
/// ListFilesAsync, not eagerly snapshotted at creation time.
/// </summary>
[Trait("Category", "Integration")]
public class CrossRepoFileReferenceTests : IDisposable
{
    private readonly string _otherRepoPath = Path.Combine(Path.GetTempPath(), $"studio-xref-other-{Guid.NewGuid():N}");

    public CrossRepoFileReferenceTests()
    {
        Directory.CreateDirectory(_otherRepoPath);
        Directory.CreateDirectory(Path.Combine(_otherRepoPath, "src"));
        File.WriteAllText(Path.Combine(_otherRepoPath, "src", "Example.cs"), "// example style content");
    }

    public void Dispose()
    {
        if (Directory.Exists(_otherRepoPath)) Directory.Delete(_otherRepoPath, recursive: true);
    }

    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [], configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    // ── IRepositoryRegistryService.ReadFileAsync / ListFilesAsync ──────────

    [Fact]
    public async Task ReadFileAsync_returns_content_for_a_registered_repository()
    {
        var app = BuildTestApp();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");

        var content = await repositories.ReadFileAsync(repo.RepositoryId, "src/Example.cs");
        Assert.Equal("// example style content", content);
    }

    [Fact]
    public async Task ReadFileAsync_returns_null_for_a_path_traversal_attempt()
    {
        var app = BuildTestApp();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");

        var content = await repositories.ReadFileAsync(repo.RepositoryId, "../../../../etc/passwd");
        Assert.Null(content);
    }

    [Fact]
    public async Task ListFilesAsync_lists_relative_paths_and_rejects_traversal()
    {
        var app = BuildTestApp();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");

        var files = await repositories.ListFilesAsync(repo.RepositoryId);
        Assert.Contains("src/Example.cs", files);

        var escaped = await repositories.ListFilesAsync(repo.RepositoryId, subPath: "../../../../");
        Assert.Empty(escaped);
    }

    // ── REST: GET /studio/repositories, .../files, .../file ────────────────

    [Fact]
    public async Task REST_repositories_list_files_and_file_round_trip()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");

        var listResponse = await client.GetAsync("/studio/repositories");
        listResponse.EnsureSuccessStatusCode();
        var listed = await listResponse.Content.ReadFromJsonAsync<List<RepositoryV1>>();
        Assert.Contains(listed!, r => r.RepositoryId == repo.RepositoryId);

        var filesResponse = await client.GetAsync($"/studio/repositories/{repo.RepositoryId}/files");
        filesResponse.EnsureSuccessStatusCode();
        var filesDoc = JsonDocument.Parse(await filesResponse.Content.ReadAsStringAsync());
        var files = filesDoc.RootElement.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("src/Example.cs", files);

        var fileResponse = await client.GetAsync($"/studio/repositories/{repo.RepositoryId}/file?path=src/Example.cs");
        fileResponse.EnsureSuccessStatusCode();
        var fileDoc = JsonDocument.Parse(await fileResponse.Content.ReadAsStringAsync());
        Assert.Equal("// example style content", fileDoc.RootElement.GetProperty("content").GetString());
    }

    [Fact]
    public async Task REST_file_endpoint_returns_404_for_a_missing_file()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");

        var response = await client.GetAsync($"/studio/repositories/{repo.RepositoryId}/file?path=does-not-exist.cs");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── MCP: RepositoryTools.ReadFileAsync / ListFilesAsync ────────────────

    [Fact]
    public async Task MCP_RepositoryTools_read_file_and_list_files_round_trip()
    {
        var app = BuildTestApp();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");
        var tools = ActivatorUtilities.CreateInstance<RepositoryTools>(app.Services);

        var readJson = await tools.ReadFileAsync(repo.RepositoryId, "src/Example.cs");
        var readDoc = JsonDocument.Parse(readJson).RootElement;
        Assert.Equal("// example style content", readDoc.GetProperty("data").GetProperty("content").GetString());

        var listJson = await tools.ListFilesAsync(repo.RepositoryId);
        var listDoc = JsonDocument.Parse(listJson).RootElement;
        var files = listDoc.GetProperty("data").GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("src/Example.cs", files);
    }

    // ── WorkUnit.ReferenceFiles round-trips through creation surfaces ──────

    [Fact]
    public async Task REST_workunits_create_with_referenceFiles_round_trips_onto_the_WorkUnit()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");

        var response = await client.PostAsJsonAsync("/studio/workunits", new
        {
            goal = "Add a feature, matching the style of the referenced example",
            owner = "test",
            referenceFiles = new[] { new { repositoryId = repo.RepositoryId, path = "src/Example.cs" } },
        });
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var referenceFiles = doc.GetProperty("referenceFiles");

        Assert.Equal(1, referenceFiles.GetArrayLength());
        Assert.Equal(repo.RepositoryId, referenceFiles[0].GetProperty("repositoryId").GetString());
        Assert.Equal("src/Example.cs", referenceFiles[0].GetProperty("path").GetString());
    }

    [Fact]
    public async Task REST_workunits_create_with_an_unknown_referenceFiles_repositoryId_fails_fast()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/studio/workunits", new
        {
            goal = "Add a feature",
            owner = "test",
            referenceFiles = new[] { new { repositoryId = "repo-does-not-exist", path = "src/Example.cs" } },
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task REST_goals_create_with_referenceFiles_round_trips_onto_the_WorkUnit()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");

        var response = await client.PostAsJsonAsync("/studio/goals", new
        {
            goal = "Add a feature, matching the style of the referenced example",
            referenceFiles = new[] { new { repositoryId = repo.RepositoryId, path = "src/Example.cs" } },
        });
        response.EnsureSuccessStatusCode();
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        var workUnitId = doc.GetProperty("workUnitId").GetString()!;

        var workUnit = await workUnits.GetAsync(workUnitId);
        Assert.NotNull(workUnit!.ReferenceFiles);
        Assert.Single(workUnit.ReferenceFiles!);
        Assert.Equal(repo.RepositoryId, workUnit.ReferenceFiles![0].RepositoryId);
    }

    [Fact]
    public async Task MCP_WorkUnitTools_create_with_referenceFiles_round_trips_onto_the_WorkUnit()
    {
        var app = BuildTestApp();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");
        var tools = ActivatorUtilities.CreateInstance<WorkUnitTools>(app.Services);

        var json = await tools.CreateAsync(
            "Add a feature, matching the style of the referenced example",
            referenceFiles: [new FileReferenceV1(repo.RepositoryId, "src/Example.cs")]);
        var doc = JsonDocument.Parse(json).RootElement;
        var workUnitId = doc.GetProperty("data").GetProperty("workUnitId").GetString()!;

        var workUnit = await workUnits.GetAsync(workUnitId);
        Assert.NotNull(workUnit!.ReferenceFiles);
        Assert.Equal(repo.RepositoryId, workUnit.ReferenceFiles![0].RepositoryId);
    }

    [Fact]
    public async Task Dispatcher_workunit_create_with_referenceFiles_and_repository_read_file_round_trip()
    {
        var app = BuildTestApp();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var repo = await repositories.RegisterAsync(_otherRepoPath, "other");

        var runtimeAssembly = app.Services.GetRequiredService<IAgentRuntimeService>().GetType().Assembly;
        var dispatcherType = runtimeAssembly.GetType("NodalMerge.Studio.AgentRuntime.McpToolDispatcher");
        Assert.NotNull(dispatcherType);
        var dispatcher = app.Services.GetService(dispatcherType!);
        Assert.NotNull(dispatcher);
        var dispatch = dispatcherType!.GetMethod("DispatchAsync", BindingFlags.Instance | BindingFlags.Public);
        Assert.NotNull(dispatch);

        var createInput = JsonSerializer.SerializeToElement(new
        {
            goal = "Add a feature, matching the style of the referenced example",
            owner = "test",
            referenceFiles = new[] { new { repositoryId = repo.RepositoryId, path = "src/Example.cs" } },
        });
        var createTask = (Task<string>)dispatch!.Invoke(dispatcher, [
            McpToolNames.WorkUnitCreate, createInput, null!, CancellationToken.None, null!
        ])!;
        var createDoc = JsonDocument.Parse(await createTask).RootElement;
        var workUnitId = createDoc.GetProperty("workUnitId").GetString()!;

        var workUnit = await workUnits.GetAsync(workUnitId);
        Assert.NotNull(workUnit!.ReferenceFiles);
        Assert.Equal(repo.RepositoryId, workUnit.ReferenceFiles![0].RepositoryId);

        var readInput = JsonSerializer.SerializeToElement(new { repositoryId = repo.RepositoryId, path = "src/Example.cs" });
        var readTask = (Task<string>)dispatch!.Invoke(dispatcher, [
            McpToolNames.RepositoryReadFile, readInput, null!, CancellationToken.None, null!
        ])!;
        var readDoc = JsonDocument.Parse(await readTask).RootElement;
        Assert.Equal("// example style content", readDoc.GetProperty("content").GetString());

        var listInput = JsonSerializer.SerializeToElement(new { repositoryId = repo.RepositoryId });
        var listTask = (Task<string>)dispatch!.Invoke(dispatcher, [
            McpToolNames.RepositoryListFiles, listInput, null!, CancellationToken.None, null!
        ])!;
        var listDoc = JsonDocument.Parse(await listTask).RootElement;
        var files = listDoc.GetProperty("files").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("src/Example.cs", files);
    }
}
