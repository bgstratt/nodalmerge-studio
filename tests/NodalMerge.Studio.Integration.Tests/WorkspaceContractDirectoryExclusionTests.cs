using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/harness-hosting-architecture.md Phase A.5 — locks in that `.workspace/` (the harness
/// contract directory) never appears in a diff harvest or file listing. FileSystemWorkspaceService
/// .IsHidden already treats every dot-prefixed path segment as hidden (unlike
/// WorkspacePathFilter.IsIgnoredDirSegment, used by the CAS snapshot walk — see
/// RepositoryImportServiceTests' sibling exclusion test), so this is a regression test locking in
/// an existing-by-coincidence behavior rather than a new code change.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceContractDirectoryExclusionTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-wscontract-{Guid.NewGuid():N}");

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
    public async Task ListAsync_never_returns_workspace_contract_files()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "real-source.cs", "// real content");

        var branchDir = Path.Combine(_rootPath, "main");
        Directory.CreateDirectory(Path.Combine(branchDir, ".workspace", "decisions"));
        await File.WriteAllTextAsync(Path.Combine(branchDir, ".workspace", "manifest.json"), "{}");
        await File.WriteAllTextAsync(Path.Combine(branchDir, ".workspace", "decisions", "0001.md"), "# decision");

        var files = await fileWorkspace.ListAsync("main");

        Assert.Contains("real-source.cs", files);
        Assert.DoesNotContain(files, f => f.Contains(".workspace"));
    }

    [Fact]
    public async Task DiffAsync_never_surfaces_workspace_contract_files_as_changes()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.InitBranchAsync("feature", seedFromBranchId: "main");
        await fileWorkspace.WriteAsync("feature", "real-source.cs", "// real content");

        var branchDir = Path.Combine(_rootPath, "feature");
        Directory.CreateDirectory(Path.Combine(branchDir, ".workspace"));
        await File.WriteAllTextAsync(Path.Combine(branchDir, ".workspace", "manifest.json"), "{}");

        var diffJson = await fileWorkspace.DiffAsync("feature", "main");

        Assert.Contains("real-source.cs", diffJson);
        Assert.DoesNotContain(".workspace", diffJson);
    }
}
