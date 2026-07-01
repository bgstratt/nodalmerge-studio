using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers IFileWorkspaceService.ReplaceAsync — a targeted edit, as opposed to WriteAsync's
/// full-file replacement. Semantics mirror Claude Code's Edit tool: expectedMatches defaults to 1,
/// so oldText must be unique in the file unless the caller explicitly says otherwise.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceReplaceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-replace-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private (IFileWorkspaceService FileWorkspace, WorkspaceOptions Options) Build()
    {
        var options = new WorkspaceOptions { RootPath = _rootPath };
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(options);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IFileWorkspaceService>(), options);
    }

    [Fact]
    public async Task A_unique_match_is_replaced_and_returns_a_diff()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.cs", "var x = GetUserById(id);");

        var result = await fileWorkspace.ReplaceAsync("main", "a.cs", "GetUserById", "FindUserById");

        Assert.Equal(1, result.Matches);
        Assert.Equal("var x = GetUserById(id);".Length, result.OldLength);
        Assert.Equal("var x = FindUserById(id);".Length, result.NewLength);
        Assert.Contains("@@ line 1 @@", result.Diff);
        Assert.Contains("- GetUserById", result.Diff);
        Assert.Contains("+ FindUserById", result.Diff);

        var content = await fileWorkspace.ReadAsync("main", "a.cs");
        Assert.Equal("var x = FindUserById(id);", content);
    }

    [Fact]
    public async Task Zero_matches_throws_without_writing()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.cs", "nothing to see here");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fileWorkspace.ReplaceAsync("main", "a.cs", "GetUserById", "FindUserById"));

        Assert.Contains("found 0", ex.Message);
        Assert.Equal("nothing to see here", await fileWorkspace.ReadAsync("main", "a.cs"));
    }

    [Fact]
    public async Task Ambiguous_match_throws_with_the_actual_count_without_writing()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.cs", "Foo();\nFoo();\nFoo();");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fileWorkspace.ReplaceAsync("main", "a.cs", "Foo()", "Bar()"));

        Assert.Contains("found 3", ex.Message);
        Assert.Equal("Foo();\nFoo();\nFoo();", await fileWorkspace.ReadAsync("main", "a.cs"));
    }

    [Fact]
    public async Task Explicit_expectedMatches_replaces_every_intended_occurrence()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.cs", "Foo();\nFoo();\nFoo();");

        var result = await fileWorkspace.ReplaceAsync("main", "a.cs", "Foo()", "Bar()", expectedMatches: 3);

        Assert.Equal(3, result.Matches);
        Assert.Equal("Bar();\nBar();\nBar();", await fileWorkspace.ReadAsync("main", "a.cs"));
    }

    [Fact]
    public async Task Missing_file_throws()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => fileWorkspace.ReplaceAsync("main", "does-not-exist.cs", "x", "y"));
    }
}
