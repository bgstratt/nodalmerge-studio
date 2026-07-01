using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers DiffExternalPathAsync/ApplyExternalPathAsync — the new IFileWorkspaceService methods
/// that compare/mirror a branch against an arbitrary absolute directory (the live repository on
/// disk) rather than another branch id, added to fix the stale-"main" bug (see
/// RepositorySyncServiceTests for the higher-level sync behavior built on top of these).
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceExternalPathTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-extpath-{Guid.NewGuid():N}");
    private readonly string _externalPath = Path.Combine(Path.GetTempPath(), $"studio-extpath-repo-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
        if (Directory.Exists(_externalPath))
            Directory.Delete(_externalPath, recursive: true);
    }

    private IFileWorkspaceService Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
        return services.BuildServiceProvider().GetRequiredService<IFileWorkspaceService>();
    }

    private void WriteExternal(string relativePath, string content)
    {
        var full = Path.Combine(_externalPath, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public async Task Diff_reports_added_modified_and_deleted_relative_to_an_arbitrary_external_path()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "unchanged.cs", "// same");
        await fileWorkspace.WriteAsync("main", "to-modify.cs", "// old");
        await fileWorkspace.WriteAsync("main", "to-delete.cs", "// gone soon");

        WriteExternal("unchanged.cs", "// same");
        WriteExternal("to-modify.cs", "// new");
        WriteExternal("brand-new.cs", "// added");

        var diff = await fileWorkspace.DiffExternalPathAsync("main", _externalPath);

        Assert.Equal(["brand-new.cs"], diff.Added);
        Assert.Equal(["to-modify.cs"], diff.Modified);
        Assert.Equal(["to-delete.cs"], diff.Deleted);
        Assert.False(diff.IsEmpty);
        Assert.NotEmpty(diff.ExternalFingerprint);
    }

    [Fact]
    public async Task Apply_mirrors_external_path_into_the_branch_including_deletions()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "to-delete.cs", "// gone soon");

        WriteExternal("brand-new.cs", "// added");

        await fileWorkspace.ApplyExternalPathAsync("main", _externalPath);

        var files = await fileWorkspace.ListAsync("main");
        Assert.Contains("brand-new.cs", files);
        Assert.DoesNotContain("to-delete.cs", files);
        Assert.Equal("// added", await fileWorkspace.ReadAsync("main", "brand-new.cs"));
    }

    [Fact]
    public async Task Diff_and_apply_exclude_node_modules_bin_obj_and_git()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");

        WriteExternal("src/real.cs", "// real");
        WriteExternal("node_modules/pkg/index.js", "// dep");
        WriteExternal("bin/Debug/app.dll", "binary");
        WriteExternal("obj/Debug/app.cache", "cache");
        WriteExternal(".git/HEAD", "ref: refs/heads/main");

        var diff = await fileWorkspace.DiffExternalPathAsync("main", _externalPath);
        Assert.Equal(["src/real.cs"], diff.Added);

        await fileWorkspace.ApplyExternalPathAsync("main", _externalPath);
        var files = await fileWorkspace.ListAsync("main");
        Assert.Equal(["src/real.cs"], files);
    }

    [Fact]
    public async Task Two_diffs_with_no_changes_between_them_both_report_empty_with_the_same_fingerprint()
    {
        var fileWorkspace = Build();
        await fileWorkspace.InitBranchAsync("main");
        WriteExternal("a.cs", "// a");

        await fileWorkspace.ApplyExternalPathAsync("main", _externalPath);

        var first = await fileWorkspace.DiffExternalPathAsync("main", _externalPath);
        var second = await fileWorkspace.DiffExternalPathAsync("main", _externalPath);

        Assert.True(first.IsEmpty);
        Assert.True(second.IsEmpty);
        Assert.Equal(first.ExternalFingerprint, second.ExternalFingerprint);
    }
}
