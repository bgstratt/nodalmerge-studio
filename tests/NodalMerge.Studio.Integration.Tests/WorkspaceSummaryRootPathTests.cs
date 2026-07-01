using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Capability-gap fix — lets an agent learn Studio's managed root/seed repository path via
// nm_v1_workspace_summary instead of assuming its current branch directory is the whole world
// (the failure mode that prompted this fix: an agent asked to "create a repository elsewhere" had
// no way to discover the boundary, so it guessed and mutated the current repository instead).
[Trait("Category", "Integration")]
public class WorkspaceSummaryRootPathTests
{
    [Fact]
    public async Task GetSummaryAsync_reports_configured_root_and_seed_repository_path()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                // Last AddSingleton wins (see AddInMemoryStorage's own comment) — registering after
                // it overrides the per-test-run temp RootPath it sets up by default.
                services.AddSingleton(new WorkspaceOptions
                {
                    RootPath = @"C:\studio-root",
                    SeedRepositoryPath = @"D:\Repos\Seed",
                });
            });
        var workspace = app.Services.GetRequiredService<IWorkspaceService>();

        var summary = await workspace.GetSummaryAsync();

        Assert.Equal(@"C:\studio-root", summary.RootPath);
        Assert.Equal(@"D:\Repos\Seed", summary.SeedRepositoryPath);
    }
}
