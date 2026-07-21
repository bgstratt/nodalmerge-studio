using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers a real bug found while debugging why a worker agent's branch had none of its project's
/// real source files: FileSystemWorkspaceService.InitBranchAsync used to no-op on
/// Directory.Exists(branchDir) alone. WorkspaceOptions.SeedRepositoryPath is set in-memory, once,
/// by the first WorkUnit created with a repositoryPath (InMemoryWorkUnitService.CreateWorkUnitAsync)
/// — so if main's directory was ever created before that happened (e.g. an earlier host run), it
/// stayed empty forever and every descendant branch inherited nothing.
/// </summary>
[Trait("Category", "Integration")]
public class FileSystemWorkspaceServiceSeedingTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-seeding-{Guid.NewGuid():N}");
    private readonly string _seedRepo = Path.Combine(Path.GetTempPath(), $"studio-seedrepo-{Guid.NewGuid():N}");

    public FileSystemWorkspaceServiceSeedingTests()
    {
        Directory.CreateDirectory(_seedRepo);
        File.WriteAllText(Path.Combine(_seedRepo, "Program.cs"), "// real source");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_rootPath, _seedRepo);

    private (IFileWorkspaceService FileWorkspace, WorkspaceOptions Options) Build(string? seedRepositoryPath = null)
    {
        var options = new WorkspaceOptions { RootPath = _rootPath, SeedRepositoryPath = seedRepositoryPath };
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton(options);
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IFileWorkspaceService>(), options);
    }

    [Fact]
    public async Task Main_can_be_seeded_after_its_directory_was_already_created_empty()
    {
        var (fileWorkspace, options) = Build();

        // main gets created before any repositoryPath is known.
        await fileWorkspace.InitBranchAsync("main");
        Assert.Empty(await fileWorkspace.ListAsync("main"));

        // A WorkUnit is later created with a real repositoryPath, setting the global option...
        options.SeedRepositoryPath = _seedRepo;

        // ...and a subsequent InitBranchAsync("main") (from another WorkUnit's branch-creation
        // chain) must now actually seed it instead of permanently no-op'ing.
        await fileWorkspace.InitBranchAsync("main");

        Assert.Contains("Program.cs", await fileWorkspace.ListAsync("main"));
    }

    [Fact]
    public async Task Main_already_populated_is_not_overwritten_by_a_later_seed_path_change()
    {
        var (fileWorkspace, options) = Build(_seedRepo);
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.WriteAsync("main", "agent-written.txt", "real work happened here");

        var otherSeed = Path.Combine(Path.GetTempPath(), $"studio-seedrepo-other-{Guid.NewGuid():N}");
        Directory.CreateDirectory(otherSeed);
        File.WriteAllText(Path.Combine(otherSeed, "should-not-appear.txt"), "x");
        try
        {
            options.SeedRepositoryPath = otherSeed;
            await fileWorkspace.InitBranchAsync("main");

            var files = await fileWorkspace.ListAsync("main");
            Assert.Contains("agent-written.txt", files);
            Assert.DoesNotContain("should-not-appear.txt", files);
        }
        finally
        {
            Directory.Delete(otherSeed, recursive: true);
        }
    }

    [Fact]
    public async Task Child_branch_left_empty_by_an_empty_parent_can_still_be_backfilled_once_the_parent_is_seeded()
    {
        // InitBranchAsync only ever runs once per branch id in production (at CreateBranchAsync
        // time), so this re-invocation is a stress case rather than a real call pattern — but the
        // safe behavior either way is: an empty directory has nothing to lose, so it stays
        // eligible for seeding; a directory that already has real content (the next test) must
        // never be touched again.
        var (fileWorkspace, options) = Build();

        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.InitBranchAsync("work-early", "main");
        Assert.Empty(await fileWorkspace.ListAsync("work-early"));

        options.SeedRepositoryPath = _seedRepo;
        await fileWorkspace.InitBranchAsync("main");
        Assert.NotEmpty(await fileWorkspace.ListAsync("main"));

        await fileWorkspace.InitBranchAsync("work-early", "main");
        Assert.Contains("Program.cs", await fileWorkspace.ListAsync("work-early"));
    }
}
