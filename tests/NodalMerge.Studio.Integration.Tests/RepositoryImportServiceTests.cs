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
/// </summary>
[Trait("Category", "Integration")]
public class RepositoryImportServiceTests : IDisposable
{
    private readonly string _repoPath = Path.Combine(Path.GetTempPath(), $"studio-import-{Guid.NewGuid():N}");

    public RepositoryImportServiceTests()
    {
        Directory.CreateDirectory(_repoPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_repoPath)) Directory.Delete(_repoPath, recursive: true);
    }

    // IBlobStoreProvider has no in-memory registration of its own anywhere in this codebase
    // (production wiring only ever supplies it via the Rust host FFI bridge) — a minimal fake is
    // enough here since these tests only care about snapshot/op-log bookkeeping, not blob content
    // round-tripping.
    private sealed class FakeBlobStoreProvider : IBlobStoreProvider
    {
        public ValueTask<BlobReadResult> TryGetBlobAsync(string hashHex, CancellationToken ct = default) =>
            ValueTask.FromResult(BlobReadResult.Missing);
        public ValueTask PutBlobAsync(string hashHex, byte[] bytes, string? contentType, CancellationToken ct = default) =>
            ValueTask.CompletedTask;
    }

    private (IRepositoryImportService Import, IRepositorySnapshotService Snapshots) Build()
    {
        var services = new ServiceCollection();
        services.AddInMemoryStorage();
        services.AddSingleton<IBlobStoreProvider>(new FakeBlobStoreProvider());
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IRepositoryImportService>(), provider.GetRequiredService<IRepositorySnapshotService>());
    }

    private string RepositoryId => Path.GetFullPath(_repoPath);

    [Fact]
    public async Task EnsureBootstrappedAsync_creates_a_Generation_zero_snapshot_for_a_fresh_repository()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        var (import, snapshots) = Build();

        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);

        var snapshot = await snapshots.GetLatestAsync(RepositoryId);
        Assert.NotNull(snapshot);
        Assert.Equal(0, snapshot!.Generation);
        Assert.Equal("Bootstrap", snapshot.Source);
        Assert.NotNull(snapshot.TreeEntries);
        Assert.Contains("a.txt", snapshot.TreeEntries!.Keys);
    }

    [Fact]
    public async Task EnsureBootstrappedAsync_called_again_after_a_disk_change_does_not_advance_the_snapshot()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        var (import, snapshots) = Build();
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
        var (import, snapshots) = Build();
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
        var (import, snapshots) = Build();
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
        var (import, snapshots) = Build();
        await import.EnsureBootstrappedAsync(RepositoryId, _repoPath);

        File.Delete(Path.Combine(_repoPath, "b.txt"));
        await import.ForceSyncAsync(RepositoryId, _repoPath);

        var snapshot = await snapshots.GetLatestAsync(RepositoryId);
        Assert.Contains("a.txt", snapshot!.TreeEntries!.Keys);
        Assert.DoesNotContain("b.txt", snapshot.TreeEntries!.Keys);
    }

    [Fact]
    public async Task ForceSyncAsync_marks_the_repository_bootstrapped_so_a_later_EnsureBootstrappedAsync_call_no_ops()
    {
        await File.WriteAllTextAsync(Path.Combine(_repoPath, "a.txt"), "v1");
        var (import, snapshots) = Build();

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
}
