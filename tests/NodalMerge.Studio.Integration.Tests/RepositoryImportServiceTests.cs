using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 2 item 2 follow-up — direct unit coverage of IRepositoryImportService, previously
/// untested in isolation anywhere in this repo (only ever exercised indirectly through higher-
/// level integration tests). Pins down the exact contract that PostMergeResyncIntegrationTests
/// and WorkspaceCacheManagerMultiRepoTests depend on: EnsureBootstrappedAsync bootstraps once and
/// then no-ops forever (right for GoalCreation/StartupRecovery — a one-time seed is all those
/// need), while ForceSyncAsync always re-runs the Case 1/Case 2 diff+snapshot logic regardless of
/// whether the repository was already bootstrapped (right for PostMergeWriteBack/ManualRefresh —
/// "check again right now"). Before this split, EVERY resync request silently did nothing once a
/// repository had been bootstrapped once in the process — this is the regression this file guards.
///
/// Slice 1.1 (plans/cas-distribution-and-storage.md Phase 1) — with a real (storing) blob store
/// wired in, CreateAsync now writes cas-tree snapshots (TreeEntries: null, TreeFormat: "cas-tree",
/// tree map in the CAS); tests below assert on the resolved tree via ISnapshotTreeResolver rather
/// than the raw (now-null) TreeEntries field.
/// </summary>
[Trait("Category", "Integration")]
public class RepositoryImportServiceTests : IAsyncLifetime
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-import-{Guid.NewGuid():N}");

    public RepositoryImportServiceTests()
    {
        Directory.CreateDirectory(_repoPath);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_repoPath);

    // IBlobStoreProvider has no in-memory registration of its own anywhere in this codebase
    // (production wiring only ever supplies it via the Rust host FFI bridge) — InMemoryBlobStoreProvider
    // (shared across this project) is a real, storing test double so slice 1.1's cas-tree write/resolve
    // round trip is actually exercised, not just made reachable.
    private (IRepositoryImportService Import, IRepositorySnapshotService Snapshots, ISnapshotTreeResolver TreeResolver) Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton<IBlobStoreProvider>(new InMemoryBlobStoreProvider());
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IRepositoryImportService>(),
                provider.GetRequiredService<IRepositorySnapshotService>(),
                provider.GetRequiredService<ISnapshotTreeResolver>());
    }

    private string RepositoryId => Path.GetFullPath(_repoPath);

    [Fact]
    public async Task EnsureBootstrappedAsync_creates_a_Generation_zero_snapshot_for_a_fresh_repository()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        var (import, snapshots, treeResolver) = Build();

        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);

        var snapshot = await snapshots.GetLatestAsync(RepositoryId);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.Generation);
        Assert.Equal("Bootstrap", snapshot.Source);

        // Slice 1.1 — with a blob store configured, the node itself carries no inline map.
        Assert.Null(snapshot.TreeEntries);
        Assert.Equal("cas-tree", snapshot.TreeFormat);

        var tree = await treeResolver.ResolveTreeAsync(snapshot);
        Assert.NotNull(tree);
        Assert.Contains("a.txt", tree!.Keys);
    }

    // plans/harness-hosting-architecture.md Phase A.5 — .workspace is the harness contract
    // directory, not source content. RepositoryImportService's CAS walk deliberately does not
    // treat dotfiles as hidden (so .env seeds correctly) — WorkspacePathFilter.IgnoredDirNames must
    // name .workspace explicitly, or it leaks into RepositorySnapshot and future branch seeds.
    [Fact]
    public async Task EnsureBootstrappedAsync_excludes_the_workspace_contract_directory_from_the_snapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        Directory.CreateDirectory(Path.Combine(_repoPath, ".workspace"));
        await File.WriteAllTextAsync(Path.Combine(_repoPath, ".workspace", "manifest.json"), "{}");
        var (import, snapshots, treeResolver) = Build();

        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);

        var snapshot = await snapshots.GetLatestAsync(RepositoryId);
        var tree = await treeResolver.ResolveTreeAsync(snapshot!);
        Assert.NotNull(tree);
        Assert.Contains("a.txt", tree!.Keys);
        Assert.DoesNotContain(tree.Keys, k => k.Contains(".workspace"));
    }

    [Fact]
    public async Task EnsureBootstrappedAsync_called_again_after_a_disk_change_does_not_advance_the_snapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        var (import, snapshots, _) = Build();
        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);
        var first = await snapshots.GetLatestAsync(RepositoryId);

        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v2 — changed on disk");
        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);

        var second = await snapshots.GetLatestAsync(RepositoryId);
        Assert.Equal(first!.SnapshotId, second!.SnapshotId); // one-time bootstrap gate — no re-diff
    }

    [Fact]
    public async Task ForceSyncAsync_advances_the_snapshot_after_a_disk_change_that_EnsureBootstrappedAsync_would_have_missed()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        var (import, snapshots, _) = Build();
        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);
        var first = await snapshots.GetLatestAsync(RepositoryId);

        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v2 — changed on disk");
        await import.ForceSyncAsync(RepositoryId, _repoPath);

        var second = await snapshots.GetLatestAsync(RepositoryId);
        Assert.NotEqual(first!.SnapshotId, second!.SnapshotId);
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.Equal(first.SnapshotId, second.BaseSnapshotId);
        Assert.Equal("ImportedFromFilesystem", second.Source);
    }

    [Fact]
    public async Task ForceSyncAsync_does_not_create_a_redundant_snapshot_when_nothing_changed_on_disk()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        var (import, snapshots, _) = Build();
        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);
        var first = await snapshots.GetLatestAsync(RepositoryId);

        await import.ForceSyncAsync(RepositoryId, _repoPath); // no disk change since bootstrap

        var second = await snapshots.GetLatestAsync(RepositoryId);
        Assert.Equal(first!.SnapshotId, second!.SnapshotId); // hasChanges guard — no spurious successor
    }

    [Fact]
    public async Task ForceSyncAsync_detects_a_deleted_file_and_removes_it_from_the_new_snapshots_tree()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "b.txt"), "v1");
        var (import, snapshots, treeResolver) = Build();
        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);

        File.Delete(Path.Combine(_repoPath, "b.txt"));
        await import.ForceSyncAsync(RepositoryId, _repoPath);

        var snapshot = await snapshots.GetLatestAsync(RepositoryId);
        var tree = await treeResolver.ResolveTreeAsync(snapshot!);
        Assert.NotNull(tree);
        Assert.Contains("a.txt", tree!.Keys);
        Assert.DoesNotContain("b.txt", tree.Keys);
    }

    [Fact]
    public async Task ForceSyncAsync_marks_the_repository_bootstrapped_so_a_later_EnsureBootstrappedAsync_call_no_ops()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        var (import, snapshots, _) = Build();

        // ForceSyncAsync as the FIRST call ever for this repository (no prior EnsureBootstrappedAsync) —
        // proves it performs its own Case 1 bootstrap, not just Case 2 resync.
        await import.ForceSyncAsync(RepositoryId, _repoPath);
        var afterForceSync = await snapshots.GetLatestAsync(RepositoryId);
        Assert.NotNull(afterForceSync);
        Assert.Equal(0, afterForceSync!.Generation);

        // A subsequent disk change followed by EnsureBootstrappedAsync (e.g. a later goal created
        // against the same repo) must NOT re-run Case 1/2 — ForceSyncAsync already marked this
        // repositoryId as bootstrapped, so GoalCreation's one-time-seed semantics still hold.
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v2 — changed on disk");
        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);

        var afterEnsure = await snapshots.GetLatestAsync(RepositoryId);
        Assert.Equal(afterForceSync.SnapshotId, afterEnsure!.SnapshotId);
    }

    // Slice 1.1 — a hand-written legacy node (inline TreeEntries, TreeFormat null, as if written
    // before this slice existed) must still drive Case 2 sync and materialize, proving cas-tree and
    // legacy snapshots are interchangeable to every reader without ever rewriting the old node.
    [Fact]
    public async Task Case2_sync_still_works_against_a_hand_written_legacy_inline_snapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");

        var legacy = new NodalMerge.Studio.Contracts.Domain.RepositorySnapshot(
            SnapshotId: "legacy-0",
            RepositoryId: RepositoryId,
            TreeHash: "legacy-hash",
            Generation: 0,
            CreatedAt: DateTimeOffset.UtcNow.AddMinutes(-5),
            Source: "Bootstrap",
            TreeEntries: new Dictionary<string, string> { ["a.txt"] = "deadbeef" });
        // Force this exact instance into the service by writing the node directly, then rehydrating
        // a fresh service instance from it (bypassing CreateAsync, which would produce a cas-tree
        // snapshot given the blob store wired in below — the whole point here is a pre-slice-1.1
        // node that was never touched).
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton<IBlobStoreProvider>(new InMemoryBlobStoreProvider());
        var provider = services.BuildServiceProvider();
        var nodeStore = provider.GetRequiredService<IStudioNodeStore>();
        await nodeStore.WriteNodeAsync(
            StudioNodeKind.RepositorySnapshotV1, legacy.SnapshotId,
            System.Text.Json.JsonSerializer.Serialize(legacy));

        var localImport = provider.GetRequiredService<IRepositoryImportService>();
        var localSnapshots = provider.GetRequiredService<IRepositorySnapshotService>();
        var localTreeResolver = provider.GetRequiredService<ISnapshotTreeResolver>();

        // Only rehydrate the one service under test (not every IRehydratable in the container —
        // that would pull in unrelated services like InMemoryDeadLetterService, which needs
        // IWorkUnitService and isn't registered by AddInMemoryStorage() alone).
        Assert.IsAssignableFrom<IRehydratable>(localSnapshots);
        await ((IRehydratable)localSnapshots).RehydrateAsync();

        var beforeSync = await localSnapshots.GetLatestAsync(RepositoryId);
        Assert.NotNull(beforeSync);
        Assert.Equal("legacy-0", beforeSync!.SnapshotId);

        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v2 — changed on disk");
        await localImport.ForceSyncAsync(RepositoryId, _repoPath);

        var afterSync = await localSnapshots.GetLatestAsync(RepositoryId);
        Assert.NotEqual("legacy-0", afterSync!.SnapshotId);
        Assert.Equal("legacy-0", afterSync.BaseSnapshotId);

        var tree = await localTreeResolver.ResolveTreeAsync(afterSync);
        Assert.NotNull(tree);
        Assert.Contains("a.txt", tree!.Keys);
    }
}
