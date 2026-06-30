using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 5 — one-time CAS bootstrap for the seed repository. Runs at goal start (via
// RepositorySyncService) before any agent is assigned a branch.
// Phase 2 — adds between-run sync (Case 2): when a snapshot with TreeEntries already exists,
// diffs the filesystem against the stored map and emits Add/Replace/Delete ops for changes.
internal sealed class RepositoryImportService(
    IRepositorySnapshotService? snapshotService = null,
    IBlobStoreProvider? blobStore = null,
    IRepositoryOpService? repoOpService = null,
    WorkspaceOptions? workspaceOptions = null)
    : IRepositoryImportService, IRehydratable
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);
    private readonly HashSet<string> _bootstrapped = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        if (snapshotService is null) return;
        // Bootstrapped repositories already have a Generation-0 snapshot. We detect this lazily
        // via GetLatestAsync on first call rather than scanning all snapshot nodes ourselves —
        // InMemoryRepositorySnapshotService already does that scan in its own RehydrateAsync.
        // Nothing to pre-populate here; EnsureBootstrappedAsync will check in-memory state.
    }

    public async Task EnsureBootstrappedAsync(
        string repositoryId, string repositoryPath, CancellationToken ct = default)
    {
        if (blobStore is null || repoOpService is null || snapshotService is null) return;
        if (!Directory.Exists(repositoryPath)) return;

        lock (_lock)
        {
            if (_bootstrapped.Contains(repositoryId)) return;
        }

        var gate = _gates.GetOrAdd(repositoryId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_bootstrapped.Contains(repositoryId)) return;
            }

            // Phase 6 — compact mid-goal ops before sync so Case 2 diffs against a current
            // snapshot, not one that predates a large agent work unit.
            if (workspaceOptions?.Snapshots.OpsPerSnapshot is int threshold)
                await snapshotService.ConsiderCompactionAsync(repositoryId, threshold, ct).ConfigureAwait(false);

            var latest = await snapshotService.GetLatestAsync(repositoryId, ct).ConfigureAwait(false);

            if (latest is null)
            {
                // Case 1 — no snapshot at all: walk every file, put to CAS, emit Import ops.
                await BootstrapAsync(repositoryId, repositoryPath, ct).ConfigureAwait(false);
            }
            else if (latest.TreeEntries is not null)
            {
                // Case 2 — snapshot exists with stored tree: diff filesystem vs stored map,
                // emit Add/Replace/Delete for changed files, create a successor snapshot.
                await SyncFromFilesystemAsync(repositoryId, repositoryPath, latest, ct).ConfigureAwait(false);
            }
            // else: pre-Phase-2 bootstrap snapshot (no TreeEntries) — skip sync for now;
            // the next completed work unit will produce a proper successor via Phase 7.

            lock (_lock) { _bootstrapped.Add(repositoryId); }
        }
        finally
        {
            gate.Release();
        }
    }

    // ── Case 1 — Bootstrap ────────────────────────────────────────────────────

    private async Task BootstrapAsync(string repositoryId, string repositoryPath, CancellationToken ct)
    {
        var treeEntries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var fullPath in EnumerateImportableFiles(repositoryPath))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(repositoryPath, fullPath).Replace('\\', '/');
            if (IsPlanArtifact(relativePath)) continue;

            var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            var blobId = BlobId(bytes);

            await blobStore!.PutBlobAsync(blobId, bytes, "text/plain", ct).ConfigureAwait(false);
            await repoOpService!.EmitAsync(new RepositoryOperation(
                OperationId: Guid.NewGuid().ToString("N"),
                RepositoryId: repositoryId,
                ParentSnapshotId: null,
                Kind: OperationType.Import,
                Path: relativePath,
                Timestamp: DateTimeOffset.UtcNow,
                OldBlobId: null,
                NewBlobId: blobId), ct).ConfigureAwait(false);

            treeEntries[relativePath] = blobId;
        }

        var gitCommit = await TryGetGitHeadAsync(repositoryPath).ConfigureAwait(false);

        await snapshotService!.CreateAsync(
            repositoryId, treeEntries,
            baseSnapshotId: null,
            gitCommit: gitCommit,
            source: "Bootstrap",
            ct: ct).ConfigureAwait(false);
    }

    // ── Case 2 — Between-run sync ─────────────────────────────────────────────

    private async Task SyncFromFilesystemAsync(
        string repositoryId, string repositoryPath,
        RepositorySnapshot latest, CancellationToken ct)
    {
        var snapshotTree = new Dictionary<string, string>(latest.TreeEntries!, StringComparer.Ordinal);
        var diskTree = new Dictionary<string, string>(StringComparer.Ordinal);

        // Walk disk — emit Add/Replace for new or changed files.
        foreach (var fullPath in EnumerateImportableFiles(repositoryPath))
        {
            ct.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(repositoryPath, fullPath).Replace('\\', '/');
            if (IsPlanArtifact(relativePath)) continue;

            var bytes = await File.ReadAllBytesAsync(fullPath, ct).ConfigureAwait(false);
            var blobId = BlobId(bytes);
            diskTree[relativePath] = blobId;

            if (snapshotTree.TryGetValue(relativePath, out var knownBlobId) && knownBlobId == blobId)
                continue; // unchanged

            await blobStore!.PutBlobAsync(blobId, bytes, "text/plain", ct).ConfigureAwait(false);

            var kind = snapshotTree.ContainsKey(relativePath) ? OperationType.Replace : OperationType.Add;
            await repoOpService!.EmitAsync(new RepositoryOperation(
                OperationId: Guid.NewGuid().ToString("N"),
                RepositoryId: repositoryId,
                ParentSnapshotId: latest.SnapshotId,
                Kind: kind,
                Path: relativePath,
                Timestamp: DateTimeOffset.UtcNow,
                OldBlobId: kind == OperationType.Replace ? knownBlobId : null,
                NewBlobId: blobId), ct).ConfigureAwait(false);
        }

        // Emit Delete for files in the snapshot that no longer exist on disk.
        foreach (var (path, oldBlobId) in snapshotTree)
        {
            if (diskTree.ContainsKey(path)) continue;

            await repoOpService!.EmitAsync(new RepositoryOperation(
                OperationId: Guid.NewGuid().ToString("N"),
                RepositoryId: repositoryId,
                ParentSnapshotId: latest.SnapshotId,
                Kind: OperationType.Delete,
                Path: path,
                Timestamp: DateTimeOffset.UtcNow,
                OldBlobId: oldBlobId,
                NewBlobId: null), ct).ConfigureAwait(false);

            diskTree.Remove(path);
        }

        // Only create a successor snapshot if anything actually changed.
        var hasChanges = diskTree.Count != snapshotTree.Count
            || diskTree.Any(kv => !snapshotTree.TryGetValue(kv.Key, out var v) || v != kv.Value);

        if (hasChanges)
        {
            await snapshotService!.CreateAsync(
                repositoryId, diskTree,
                baseSnapshotId: latest.SnapshotId,
                source: "ImportedFromFilesystem",
                ct: ct).ConfigureAwait(false);
        }
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    private static IEnumerable<string> EnumerateImportableFiles(string repositoryPath) =>
        Directory.EnumerateFiles(repositoryPath, "*", SearchOption.AllDirectories)
            .Where(f => !WorkspacePathFilter.IsIgnoredDirSegment(
                Path.GetRelativePath(repositoryPath, f)));

    private static async Task<string?> TryGetGitHeadAsync(string repositoryPath)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", "rev-parse HEAD")
                {
                    WorkingDirectory = repositoryPath,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            var head = output.Trim();
            return head.Length == 40 ? head : null;
        }
        catch { return null; }
    }

    private static string BlobId(byte[] bytes) => BlobHasher.ComputeHash(bytes);

    private static bool IsPlanArtifact(string relativePath) =>
        relativePath.Replace('\\', '/') == PlanDocumentPaths.FileName;
}
