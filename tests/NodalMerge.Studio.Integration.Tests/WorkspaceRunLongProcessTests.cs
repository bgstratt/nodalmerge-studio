using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 9c — covers the real bug in the pre-9c RunAsync: it always ran the command through the
/// same blocking Process.Start + WaitForExitAsync(timeout) path build/test use, so a dev server
/// (which never exits on its own) just blocked until the 120s default timeout killed it and
/// returned whatever stdout happened to buffer — never actually left anything running. RunAsync
/// now detects long-running roots (via WorkspaceProfile.IsLongRunning) and starts them detached
/// through RunningProcessRegistry instead.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceRunLongProcessTests : IDisposable
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-run-longproc-{Guid.NewGuid():N}");

    // Sdk="Microsoft.NET.Sdk.Web" marks this as a long-running root per WorkspaceProfileService's
    // detection — the loop body itself is what makes it *actually* long-running for this test.
    private const string WebCsproj = """
        <Project Sdk="Microsoft.NET.Sdk.Web">
          <PropertyGroup>
            <OutputType>Exe</OutputType>
            <TargetFramework>net10.0</TargetFramework>
          </PropertyGroup>
        </Project>
        """;

    private const string LoopForeverProgram = """
        while (true)
        {
            System.Console.WriteLine("tick");
            System.Threading.Thread.Sleep(200);
        }
        """;

    public void Dispose()
    {
        if (Directory.Exists(_rootPath))
            Directory.Delete(_rootPath, recursive: true);
    }

    private (IFileWorkspaceService FileWorkspace, IWorkspaceExecutionCommandService Cmd) Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IFileWorkspaceService>(), provider.GetRequiredService<IWorkspaceExecutionCommandService>());
    }

    [Fact]
    public async Task Auto_detected_long_running_root_returns_immediately_and_can_be_stopped()
    {
        var (fileWorkspace, cmd) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "alpha/App.csproj", WebCsproj);
        await fileWorkspace.WriteAsync("main", "alpha/Program.cs", LoopForeverProgram);

        var sw = Stopwatch.StartNew();
        var results = await cmd.RunAsync("main");
        sw.Stop();

        var result = Assert.Single(results);
        Assert.True(result.Running);
        Assert.NotNull(result.Pid);
        Assert.Equal("alpha", result.ProjectRoot);
        // The loop never exits on its own — if RunAsync blocked waiting for it, this would hang
        // for the full default timeout (120s) instead of returning almost immediately.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"RunAsync should return immediately, took {sw.Elapsed}");

        var stopped = await cmd.StopAsync("main");
        Assert.Equal(1, stopped);

        await Task.Delay(500);
        Assert.False(IsStillRunning(result.Pid!.Value));
    }

    [Fact]
    public async Task Re_running_the_same_root_replaces_the_previous_process()
    {
        var (fileWorkspace, cmd) = Build();
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "alpha/App.csproj", WebCsproj);
        await fileWorkspace.WriteAsync("main", "alpha/Program.cs", LoopForeverProgram);

        var first = Assert.Single(await cmd.RunAsync("main"));
        var firstPid = first.Pid!.Value;

        var second = Assert.Single(await cmd.RunAsync("main"));
        var secondPid = second.Pid!.Value;

        Assert.NotEqual(firstPid, secondPid);

        await Task.Delay(500);
        Assert.False(IsStillRunning(firstPid), "starting a new run for the same root should stop the old one, not leak it");

        await cmd.StopAsync("main");
    }

    [Fact]
    public async Task Explicit_run_command_override_still_runs_once_and_blocks()
    {
        var (fileWorkspace, cmd) = Build();
        await fileWorkspace.InitBranchAsync("main");

        var results = await cmd.RunAsync("main", runCommand: "dotnet --version", timeoutSeconds: 30);

        var result = Assert.Single(results);
        Assert.False(result.Running);
        Assert.Null(result.Pid);
        Assert.True(result.Success);
    }

    private static bool IsStillRunning(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }
}
