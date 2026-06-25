using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers IFileWorkspaceService.SearchAsync — content grep across a branch, as opposed to
/// ListAsync's filename-only matching. See the agent tool-surface improvement plan: this is the
/// primary new discovery primitive for Worker/Planner/Reviewer.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceSearchTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-search-{Guid.NewGuid():N}");

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
    public async Task Literal_match_returns_line_number_and_surrounding_context()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "src/UserService.cs",
            "line1\nline2\nvar user = await GetUserById(id);\nline4\nline5");

        var (matches, truncated) = await fileWorkspace.SearchAsync("main", "GetUserById");

        Assert.False(truncated);
        var match = Assert.Single(matches);
        Assert.Equal("src/UserService.cs", match.Path);
        Assert.Equal(3, match.Line);
        Assert.Equal(1, match.StartLine);
        Assert.Equal(5, match.EndLine);
        Assert.Contains("GetUserById", match.Snippet);
        Assert.Contains("line1", match.Snippet);
        Assert.Contains("line5", match.Snippet);
    }

    [Fact]
    public async Task Search_is_case_insensitive_by_default_and_can_be_made_case_sensitive()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.txt", "FooBar");

        var (insensitive, _) = await fileWorkspace.SearchAsync("main", "foobar");
        Assert.Single(insensitive);

        var (sensitive, _) = await fileWorkspace.SearchAsync("main", "foobar", caseSensitive: true);
        Assert.Empty(sensitive);
    }

    [Fact]
    public async Task Regex_mode_matches_a_pattern_instead_of_a_literal_string()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.txt", "GetUserById\nGetOrderById\nDeleteUser");

        var (matches, _) = await fileWorkspace.SearchAsync("main", @"Get\w+ById", regex: true);

        Assert.Equal(2, matches.Count);
        Assert.Contains(matches, m => m.Line == 1);
        Assert.Contains(matches, m => m.Line == 2);
    }

    [Fact]
    public async Task FilePattern_scopes_which_files_are_scanned()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "src/Foo.cs", "needle");
        await fileWorkspace.WriteAsync("main", "src/Foo.test.cs", "needle");

        var (matches, _) = await fileWorkspace.SearchAsync("main", "needle", filePattern: "*.test.cs");

        var match = Assert.Single(matches);
        Assert.Equal("src/Foo.test.cs", match.Path);
    }

    [Fact]
    public async Task MaxResults_truncates_and_reports_truncated_true()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.txt", string.Join('\n', Enumerable.Repeat("needle", 10)));

        var (matches, truncated) = await fileWorkspace.SearchAsync("main", "needle", maxResults: 3);

        Assert.Equal(3, matches.Count);
        Assert.True(truncated);
    }

    [Fact]
    public async Task Binary_files_are_skipped_without_throwing()
    {
        var (fileWorkspace, options) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "text.txt", "needle in a haystack");

        var binaryPath = Path.Combine(options.RootPath, "main", "binary.dat");
        await File.WriteAllBytesAsync(binaryPath, [0x4E, 0x65, 0x00, 0x65, 0x64, 0x6C, 0x65]); // contains a null byte

        var (matches, _) = await fileWorkspace.SearchAsync("main", "needle");

        Assert.Single(matches);
        Assert.Equal("text.txt", matches[0].Path);
    }

    [Fact]
    public async Task Files_over_MaxReadBytes_are_skipped()
    {
        var (fileWorkspace, options) = Build();
        options.MaxReadBytes = 10;
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "big.txt", "this file is longer than ten bytes and contains needle");

        var (matches, _) = await fileWorkspace.SearchAsync("main", "needle");

        Assert.Empty(matches);
    }

    [Fact]
    public async Task No_matches_returns_empty_not_truncated()
    {
        var (fileWorkspace, _) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "a.txt", "nothing relevant here");

        var (matches, truncated) = await fileWorkspace.SearchAsync("main", "doesnotexist");

        Assert.Empty(matches);
        Assert.False(truncated);
    }
}
