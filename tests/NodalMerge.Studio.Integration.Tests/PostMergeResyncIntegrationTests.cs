using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 2 item 2 follow-up — proves the post-merge auto-resync (InMemoryMergeService.ApplyAsync
/// calling IRepositorySyncService.SyncBranchFromRepositoryAsync with SyncTrigger.PostMergeWriteBack)
/// actually advances the CAS RepositorySnapshot end-to-end, against the real RepositoryImportService
/// — not just that the call happens (InMemoryMergeServiceTests already proves that with a fake).
/// This is the regression test for the deeper bug found while building this: EnsureBootstrappedAsync's
/// one-time-per-process "_bootstrapped" gate meant a repository's snapshot never advanced again after
/// its first goal-creation bootstrap, no matter how many merges landed — RepositoryImportService now
/// exposes ForceSyncAsync specifically to bypass that gate for triggers that need "check again now".
/// </summary>
[Trait("Category", "Integration")]
public class PostMergeResyncIntegrationTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-postmerge-resync-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repoPath)) Directory.Delete(_repoPath, recursive: true);
    }

    [Fact]
    public async Task ApplyAsync_auto_resync_advances_the_real_RepositorySnapshot_after_write_back()
    {
        Directory.CreateDirectory(_repoPath);
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "Program.cs"), "// v1");

        await using var app = StudioWebApplication.Build([], configureServices: services =>
        {
            services.AddInMemoryStorage();
            services.AddSingleton(new WorkspaceOptions { SeedRepositoryPath = _repoPath });
        });

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var snapshotService = app.Services.GetRequiredService<IRepositorySnapshotService>();
        var repositories = app.Services.GetRequiredService<IRepositoryRegistryService>();

        var workUnit = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Update the repo", "test", RepositoryPath: _repoPath));

        // Slice 7.2 — the repository's actual CAS/snapshot key is now the workgroup-portable
        // WorkgroupRepoId (resolved via IRepositoryRegistryService.ResolveCasIdentityAsync at
        // bootstrap time), not Path.GetFullPath(_repoPath) — see plans/cas-distribution-and-
        // storage.md Phase 7, 7.2.
        var repository = await repositories.GetAsync(workUnit.RepositoryId!);
        var repositoryId = await repositories.ResolveCasIdentityAsync(workUnit.RepositoryId, _repoPath);
        Assert.NotNull(repository);
        Assert.NotNull(repositoryId);

        var snapshotBeforeMerge = await snapshotService.GetLatestAsync(repositoryId!);
        Assert.NotNull(snapshotBeforeMerge); // Case 1 bootstrap ran at goal creation

        await fileWorkspace.WriteAsync(workUnit.BranchId, "Program.cs", "// v2 — from agent");

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: workUnit.BranchId, targetBranch: "main", summary: "Update", workUnitId: workUnit.WorkUnitId);
        await mergeCommands.ValidateAsync(proposal.ProposalId);
        await mergeCommands.ReviewAsync(proposal.ProposalId, "Approved");
        await mergeCommands.ApplyAsync(proposal.ProposalId);

        // Confirm the write-back itself landed (pre-existing behavior, not what's under test here).
        Assert.Equal("// v2 — from agent", await File.ReadAllTextAsync(Path.Combine(_repoPath, "Program.cs")));

        var snapshotAfterMerge = await snapshotService.GetLatestAsync(repositoryId!);
        Assert.NotNull(snapshotAfterMerge);
        Assert.NotEqual(snapshotBeforeMerge!.SnapshotId, snapshotAfterMerge!.SnapshotId);
        Assert.True(snapshotAfterMerge.Generation > snapshotBeforeMerge.Generation);
        Assert.True(snapshotAfterMerge.CreatedAt > snapshotBeforeMerge.CreatedAt);
        Assert.Equal(snapshotBeforeMerge.SnapshotId, snapshotAfterMerge.BaseSnapshotId);
    }
}
