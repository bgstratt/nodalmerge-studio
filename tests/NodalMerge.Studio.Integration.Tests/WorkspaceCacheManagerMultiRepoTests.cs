using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 2 item 2 follow-up — WorkspaceCacheManager.GetRepositoryId() used to always resolve the
/// global WorkspaceOptions.SeedRepositoryPath regardless of which repository a work unit actually
/// belonged to, so eviction-safety and materialization checked the wrong repository's snapshot
/// entirely for a multi-repo goal. GetRepositoryIdAsync now prefers the work unit's own registered
/// RepositoryId, mirroring InMemoryMergeService's write-back resolution (MultiRepoWriteBackTests).
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceCacheManagerMultiRepoTests : IDisposable
{
    private readonly string _repoAPath = Path.Combine(Path.GetTempPath(), $"studio-cache-a-{Guid.NewGuid():N}");
    private readonly string _repoBPath = Path.Combine(Path.GetTempPath(), $"studio-cache-b-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_repoAPath)) Directory.Delete(_repoAPath, recursive: true);
        if (Directory.Exists(_repoBPath)) Directory.Delete(_repoBPath, recursive: true);
    }

    [Fact]
    public async Task EvictAsync_uses_the_work_units_own_repository_snapshot_not_the_others()
    {
        Directory.CreateDirectory(_repoAPath);
        Directory.CreateDirectory(_repoBPath);
        await File.WriteAllTextAsync(Path.Combine(_repoAPath, "Program.cs"), "// repo A v1");
        await File.WriteAllTextAsync(Path.Combine(_repoBPath, "Program.cs"), "// repo B v1");

        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var repositorySync = app.Services.GetRequiredService<IRepositorySyncService>();
        var cacheManager = app.Services.GetRequiredService<IWorkspaceCacheManager>();

        var workUnitA = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Update repo A", "test", RepositoryPath: _repoAPath));
        var workUnitB = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Update repo B", "test", RepositoryPath: _repoBPath));

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

        // Only A's proposal is applied. This test originally applied B too and relied on "the
        // automatic post-merge resync only covers the global default repo" to leave repo B's
        // snapshot stale — but that resync gap was itself a bug (pathways-workspace-history.md
        // verification item 1) and BestEffortResyncAsync now force-syncs a multi-repo work unit's
        // own repository at apply time, so a merged-but-stale repo B can no longer exist through
        // the apply path. B approved-but-unapplied preserves this test's actual target: eviction
        // safety must consult each work unit's OWN repository — the old always-use-the-global-
        // default bug reports no snapshot for BOTH work units and fails the evictedA assertion.
        await mergeCommands.ApplyAsync(proposalA.ProposalId);

        await repositorySync.SyncBranchFromRepositoryAsync("main", _repoAPath, SyncTrigger.ManualRefresh);

        var evictedA = await cacheManager.EvictAsync(workUnitA.WorkUnitId);
        var evictedB = await cacheManager.EvictAsync(workUnitB.WorkUnitId);

        Assert.True(evictedA); // repo A's own snapshot postdates its merge — safe to evict
        Assert.False(evictedB); // repo B's work is still in flight (approved, unapplied) — must not evict

        var branchDirA = await fileWorkspace.GetWorkingDirectoryAsync(workUnitA.BranchId);
        var branchDirB = await fileWorkspace.GetWorkingDirectoryAsync(workUnitB.BranchId);
        Assert.False(Directory.Exists(branchDirA));
        Assert.True(Directory.Exists(branchDirB));
    }
}
