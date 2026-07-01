using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 9a — covers the real bugs found in WorkspaceExecutionService's flat, repo-root-only
/// detection: package.json detection was non-recursive (a nested-only package.json was never
/// found at all), and even recursively-detected build systems ran from the wrong working
/// directory. WorkspaceProfileService groups marker files by their own containing directory
/// instead, so each sub-project gets its own correctly-scoped root.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceProfileServiceTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-profile-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private (IFileWorkspaceService FileWorkspace, IWorkspaceProfileService Profiles) Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IFileWorkspaceService>(), provider.GetRequiredService<IWorkspaceProfileService>());
    }

    [Fact]
    public async Task Detects_dotnet_and_npm_roots_with_no_root_level_markers()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "backend/Host.csproj", WebCsproj);
        await fileWorkspace.WriteAsync("main", "frontend/package.json", """{"scripts":{"build":"vite build"}}""");

        var profile = await profiles.GetOrDetectAsync("main");

        Assert.Equal(2, profile.Roots.Count);
        var backend = Assert.Single(profile.Roots, r => r.RelativePath == "backend");
        Assert.Equal("dotnet", backend.Stack);
        Assert.Equal("dotnet build", backend.BuildCommand);
        Assert.Equal("dotnet run", backend.RunCommand);
        Assert.True(backend.IsLongRunning);

        var frontend = Assert.Single(profile.Roots, r => r.RelativePath == "frontend");
        Assert.Equal("npm", frontend.Stack);
        Assert.Equal("npm run build", frontend.BuildCommand);
    }

    [Fact]
    public async Task Solution_anchor_folds_nested_csproj_into_a_single_root()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "Solution.slnx", "<Solution/>");
        await fileWorkspace.WriteAsync("main", "src/A/A.csproj", PlainCsproj);
        await fileWorkspace.WriteAsync("main", "src/B/B.csproj", PlainCsproj);
        await fileWorkspace.WriteAsync("main", "src/C/C.csproj", PlainCsproj);

        var profile = await profiles.GetOrDetectAsync("main");

        var root = Assert.Single(profile.Roots);
        Assert.Equal("", root.RelativePath);
        Assert.Equal("dotnet", root.Stack);
        // Three nested projects, none directly in the root dir alongside the .slnx — ambiguous.
        Assert.Null(root.RunCommand);
    }

    [Fact]
    public async Task Single_project_alongside_its_own_solution_file_still_resolves_run_command()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "App.sln", "");
        await fileWorkspace.WriteAsync("main", "App.csproj", WebCsproj);

        var profile = await profiles.GetOrDetectAsync("main");

        var root = Assert.Single(profile.Roots);
        Assert.Equal("dotnet run", root.RunCommand);
        Assert.True(root.IsLongRunning);
    }

    [Fact]
    public async Task Npm_dev_script_marks_root_as_long_running_run_command()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "package.json",
            """{"scripts":{"build":"vite build","test":"vitest","dev":"vite dev"}}""");

        var profile = await profiles.GetOrDetectAsync("main");

        var root = Assert.Single(profile.Roots);
        Assert.Equal("npm", root.Stack);
        Assert.Equal("npm run build", root.BuildCommand);
        Assert.Equal("npm test", root.TestCommand);
        Assert.Equal("npm run dev", root.RunCommand);
        Assert.True(root.IsLongRunning);
    }

    [Fact]
    public async Task Ignores_package_json_inside_node_modules()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "frontend/package.json", """{"scripts":{"build":"vite build"}}""");
        await fileWorkspace.WriteAsync("main", "frontend/node_modules/some-pkg/package.json", """{"scripts":{"build":"x"}}""");

        var profile = await profiles.GetOrDetectAsync("main");

        var root = Assert.Single(profile.Roots);
        Assert.Equal("frontend", root.RelativePath);
    }

    [Fact]
    public async Task No_recognized_project_files_yields_empty_roots_without_crashing()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "README.md", "# Just docs, no project markers.");

        var profile = await profiles.GetOrDetectAsync("main");

        Assert.Empty(profile.Roots);
    }

    [Fact]
    public async Task RescanAsync_bypasses_cache_and_picks_up_new_roots()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "backend/Host.csproj", PlainCsproj);

        var first = await profiles.GetOrDetectAsync("main");
        Assert.Single(first.Roots);

        await fileWorkspace.WriteAsync("main", "frontend/package.json", """{"scripts":{}}""");

        // Cached — the new root isn't picked up without a rescan.
        var stillCached = await profiles.GetOrDetectAsync("main");
        Assert.Single(stillCached.Roots);

        var rescanned = await profiles.RescanAsync("main");
        Assert.Equal(2, rescanned.Roots.Count);

        // The cache is refreshed by RescanAsync too.
        var afterRescan = await profiles.GetOrDetectAsync("main");
        Assert.Equal(2, afterRescan.Roots.Count);
    }

    [Fact]
    public async Task Per_root_rule_file_is_attached_and_first_match_wins()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "backend/Host.csproj", PlainCsproj);
        await fileWorkspace.WriteAsync("main", "backend/AGENTS.md", "Backend rules.");
        // AGENTS.md wins over CLAUDE.md when both exist in the same root.
        await fileWorkspace.WriteAsync("main", "backend/CLAUDE.md", "Should be ignored.");

        var profile = await profiles.GetOrDetectAsync("main");

        var backend = Assert.Single(profile.Roots);
        Assert.Equal("Backend rules.", backend.RuleFileContent);
    }

    [Fact]
    public async Task Branch_root_rule_file_is_surfaced_even_with_no_buildable_project_there()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "backend/Host.csproj", PlainCsproj);
        await fileWorkspace.WriteAsync("main", "frontend/package.json", """{"scripts":{}}""");
        await fileWorkspace.WriteAsync("main", "AGENTS.md", "Whole-repo conventions.");

        var profile = await profiles.GetOrDetectAsync("main");

        Assert.Equal(3, profile.Roots.Count);
        var repoRoot = Assert.Single(profile.Roots, r => r.RelativePath == "");
        Assert.Equal("none", repoRoot.Stack);
        Assert.Equal("Whole-repo conventions.", repoRoot.RuleFileContent);
        Assert.All(profile.Roots.Where(r => r.RelativePath != ""), r => Assert.Null(r.RuleFileContent));
    }

    [Fact]
    public async Task Rule_file_content_over_4000_chars_is_truncated()
    {
        var (fileWorkspace, profiles) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "backend/Host.csproj", PlainCsproj);
        await fileWorkspace.WriteAsync("main", "backend/AGENTS.md", new string('x', 5000));

        var profile = await profiles.GetOrDetectAsync("main");

        var backend = Assert.Single(profile.Roots);
        Assert.Equal(4000, backend.RuleFileContent!.Length);
    }

    private const string PlainCsproj = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
    private const string WebCsproj = "<Project Sdk=\"Microsoft.NET.Sdk.Web\"></Project>";
}
