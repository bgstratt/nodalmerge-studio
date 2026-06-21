using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 9b — covers the real bug in the pre-profile WorkspaceExecutionService: every detected
/// build system ran with WorkingDirectory pinned to the branch root, so a repo with more than one
/// project and no shared solution file tying them together would "detect" dotnet (recursively) but
/// fail to build, because `dotnet build` run from a directory with no project/solution file errors
/// out. ExecuteAsync now resolves commands per WorkspaceProfile root and runs each from its own
/// directory — this exercises that with real `dotnet build` invocations, not a mocked process.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceExecutionPerRootTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-exec-perroot-{Guid.NewGuid():N}");

    private const string Csproj = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private (IFileWorkspaceService FileWorkspace, IWorkspaceExecutionService Execution) Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IFileWorkspaceService>(), provider.GetRequiredService<IWorkspaceExecutionService>());
    }

    [Fact]
    public async Task Build_runs_each_detected_root_from_its_own_directory()
    {
        var (fileWorkspace, execution) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "alpha/App.csproj", Csproj);
        await fileWorkspace.WriteAsync("main", "alpha/Program.cs", "System.Console.WriteLine(\"alpha\");");
        await fileWorkspace.WriteAsync("main", "beta/App.csproj", Csproj);
        await fileWorkspace.WriteAsync("main", "beta/Program.cs", "System.Console.WriteLine(\"beta\");");

        var result = await execution.ExecuteAsync(
            "main", new WorkspaceExecutionRequest(Build: true, TimeoutSeconds: 120));

        Assert.Equal(2, result.Builds.Count);
        Assert.All(result.Builds, b => Assert.True(b.Success, $"{b.ProjectRoot}: {b.StdErr}"));
        Assert.Contains(result.Builds, b => b.ProjectRoot == "alpha");
        Assert.Contains(result.Builds, b => b.ProjectRoot == "beta");
        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public async Task Build_with_no_detected_roots_returns_empty_results_not_an_exception()
    {
        // Known characteristic, not a bug: with zero build targets, AllSucceeded is vacuously true
        // (Linq .All on an empty sequence) — matches the pre-Phase-9 behavior when no build command
        // could be resolved at all. A self-verify worker (Phase 9i) asking to build a root with no
        // BuildCommand gets "succeeded, 0 builds" rather than a hard failure — documented here so
        // it doesn't get rediscovered as a surprise later.
        var (fileWorkspace, execution) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "README.md", "# Just docs, no project markers.");

        var result = await execution.ExecuteAsync(
            "main", new WorkspaceExecutionRequest(Build: true, Test: true, TimeoutSeconds: 30));

        Assert.Empty(result.Builds);
        Assert.Empty(result.Tests);
        Assert.True(result.AllSucceeded);
    }

    [Fact]
    public async Task Rule_file_only_branch_root_is_skipped_during_build_not_treated_as_a_target()
    {
        // Phase 9h's synthetic "" root (Stack="none", BuildCommand=null) exists purely to carry a
        // top-level AGENTS.md when no buildable project sits at the branch root — it must never
        // show up as a build target itself.
        var (fileWorkspace, execution) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "alpha/App.csproj", Csproj);
        await fileWorkspace.WriteAsync("main", "alpha/Program.cs", "System.Console.WriteLine(\"alpha\");");
        await fileWorkspace.WriteAsync("main", "AGENTS.md", "Whole-repo conventions.");

        var result = await execution.ExecuteAsync(
            "main", new WorkspaceExecutionRequest(Build: true, TimeoutSeconds: 120));

        var build = Assert.Single(result.Builds);
        Assert.Equal("alpha", build.ProjectRoot);
        Assert.True(build.Success, build.StdErr);
    }

    [Fact]
    public async Task RootPath_filter_limits_auto_detected_build_to_one_root()
    {
        // Phase 9f — per-root UI controls need to build/test one root without touching the others.
        var (fileWorkspace, execution) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "alpha/App.csproj", Csproj);
        await fileWorkspace.WriteAsync("main", "alpha/Program.cs", "System.Console.WriteLine(\"alpha\");");
        await fileWorkspace.WriteAsync("main", "beta/App.csproj", Csproj);
        await fileWorkspace.WriteAsync("main", "beta/Program.cs", "System.Console.WriteLine(\"beta\");");

        var result = await execution.ExecuteAsync(
            "main", new WorkspaceExecutionRequest(Build: true, TimeoutSeconds: 120, RootPath: "alpha"));

        var build = Assert.Single(result.Builds);
        Assert.Equal("alpha", build.ProjectRoot);
        Assert.True(build.Success, build.StdErr);
    }

    [Fact]
    public async Task Explicit_build_command_override_still_runs_once_at_branch_root()
    {
        // Level 1 (explicit BuildCommand) must keep today's behavior byte-for-byte: a single
        // command at the branch root, no profile lookup, regardless of how many roots exist.
        var (fileWorkspace, execution) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "alpha/App.csproj", Csproj);
        await fileWorkspace.WriteAsync("main", "beta/App.csproj", Csproj);

        var result = await execution.ExecuteAsync(
            "main", new WorkspaceExecutionRequest(Build: true, BuildCommand: "dotnet --version", TimeoutSeconds: 60));

        var build = Assert.Single(result.Builds);
        Assert.Equal("", build.ProjectRoot);
        Assert.True(build.Success);
    }
}
