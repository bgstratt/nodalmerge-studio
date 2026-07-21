using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers IFileWorkspaceService.ReadManyAsync — batches the search-then-read-several-hits pattern
/// into one round trip. A missing path comes back as Found=false/Content=null in its own slot
/// rather than failing the whole call.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceReadManyTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-readmany-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_rootPath);

    private IFileWorkspaceService Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IFileWorkspaceService>();
    }

    [Fact]
    public async Task Multiple_existing_paths_are_all_returned_with_content()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.cs", "content-a");
        await fileWorkspace.WriteAsync("main", "b.cs", "content-b");

        var results = await fileWorkspace.ReadManyAsync("main", ["a.cs", "b.cs"]);

        Assert.Equal(2, results.Count);
        Assert.Equal("content-a", results[0].Content);
        Assert.True(results[0].Found);
        Assert.Equal("content-b", results[1].Content);
        Assert.True(results[1].Found);
    }

    [Fact]
    public async Task A_missing_path_comes_back_as_not_found_without_failing_the_others()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.cs", "content-a");

        var results = await fileWorkspace.ReadManyAsync("main", ["a.cs", "missing.cs"]);

        Assert.Equal(2, results.Count);
        Assert.True(results[0].Found);
        Assert.Equal("content-a", results[0].Content);
        Assert.False(results[1].Found);
        Assert.Null(results[1].Content);
    }

    [Fact]
    public async Task Empty_paths_list_returns_empty_results()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");

        var results = await fileWorkspace.ReadManyAsync("main", []);

        Assert.Empty(results);
    }
}
