using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Multi-repo Phase 2, Part A — InMemoryMergeService.ApplyAsync used to write merged changes back
/// to disk using the single global WorkspaceOptions.SeedRepositoryPath, regardless of which
/// repository the merging work unit actually belonged to. With two goals against two different
/// repositories, applying either proposal would write back to whichever repo path happened to be
/// in the global singleton — not necessarily the right one. Write-back now resolves the owning
/// work unit's own RepositoryId instead.
/// </summary>
[Trait("Category", "Integration")]
public class MultiRepoWriteBackTests : IDisposable
{
    private readonly string _repoAPath = Path.Combine(Path.GetTempPath(), $"studio-writeback-a-{Guid.NewGuid():N}");
    private readonly string _repoBPath = Path.Combine(Path.GetTempPath(), $"studio-writeback-b-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repoAPath)) Directory.Delete(_repoAPath, recursive: true);
        if (Directory.Exists(_repoBPath)) Directory.Delete(_repoBPath, recursive: true);
    }

    [Fact]
    public async Task ApplyAsync_writes_back_to_the_merging_work_units_own_repository_not_the_other_ones()
    {
        Directory.CreateDirectory(_repoAPath);
        Directory.CreateDirectory(_repoBPath);
        await File.WriteAllTextAsync(Path.Combine(_repoAPath, "Program.cs"), "// repo A v1");
        await File.WriteAllTextAsync(Path.Combine(_repoBPath, "Program.cs"), "// repo B v1");

        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var workUnitA = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Update repo A", "test", RepositoryPath: _repoAPath));
        var workUnitB = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Update repo B", "test", RepositoryPath: _repoBPath));

        // Every goal with any repository association now gets a durable RepositoryId (Part A).
        Assert.NotNull(workUnitA.RepositoryId);
        Assert.NotNull(workUnitB.RepositoryId);
        Assert.NotEqual(workUnitA.RepositoryId, workUnitB.RepositoryId);

        await fileWorkspace.WriteAsync(workUnitA.BranchId, "Program.cs", "// repo A v2 — from agent");
        await fileWorkspace.WriteAsync(workUnitB.BranchId, "Program.cs", "// repo B v2 — from agent");

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: workUnitA.BranchId, targetBranch: "main", summary: "Update A", workUnitId: workUnitA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: workUnitB.BranchId, targetBranch: "main", summary: "Update B", workUnitId: workUnitB.WorkUnitId);

        await mergeCommands.ValidateAsync(proposalA.ProposalId);
        await mergeCommands.ValidateAsync(proposalB.ProposalId);
        await mergeCommands.ReviewAsync(proposalA.ProposalId, "Approved");
        await mergeCommands.ReviewAsync(proposalB.ProposalId, "Approved");

        await mergeCommands.ApplyAsync(proposalA.ProposalId);
        await mergeCommands.ApplyAsync(proposalB.ProposalId);

        Assert.Equal("// repo A v2 — from agent", await File.ReadAllTextAsync(Path.Combine(_repoAPath, "Program.cs")));
        Assert.Equal("// repo B v2 — from agent", await File.ReadAllTextAsync(Path.Combine(_repoBPath, "Program.cs")));
    }
}
