using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Used when IBlobStoreProvider is unavailable (e.g. in-memory test environments without CAS).
// FileSystemWorkspaceService.InitBranchAsync falls through to the directory-copy path when
// MaterializeAsync returns 0 on a snapshot with no TreeEntries — but this ensures the DI
// graph is always complete without null checks in the factory.
internal sealed class NullMaterializationEngine : IMaterializationEngine
{
    public static readonly NullMaterializationEngine Instance = new();
    public Task<int> MaterializeAsync(RepositorySnapshot snapshot, string targetPath,
        IReadOnlyList<string>? fileScope = null, CancellationToken ct = default)
        => Task.FromResult(0);
    public Task<int> RematerializeAsync(RepositorySnapshot snapshot, RepositorySnapshot previousSnapshot,
        string targetPath, IReadOnlyList<string>? fileScope = null, CancellationToken ct = default)
        => Task.FromResult(0);
}

// Phase 7 — reconstructs workspace directories from a snapshot's TreeEntries + CAS.
// The skip-already-matching optimization avoids re-fetching blobs for files already on disk
// with the correct content — critical for partial-eviction recovery.
internal sealed class MaterializationEngine(
    IBlobStoreProvider blobStore,
    WorkspaceOptions options) : IMaterializationEngine
{
    public async Task<int> MaterializeAsync(
        RepositorySnapshot snapshot,
        string targetPath,
        IReadOnlyList<string>? fileScope = null,
        CancellationToken ct = default)
    {
        if (snapshot.TreeEntries is null) return 0;

        var entries = FilterByScope(snapshot.TreeEntries, fileScope);
        var entrySet = entries.Keys.ToHashSet(StringComparer.Ordinal);

        Directory.CreateDirectory(targetPath);

        // Delete files present on disk but absent from the snapshot (within scope).
        DeleteAbsentFiles(targetPath, entrySet, fileScope);

        // Write missing or changed files in parallel.
        return await WriteEntriesAsync(entries, targetPath, ct).ConfigureAwait(false);
    }

    public async Task<int> RematerializeAsync(
        RepositorySnapshot snapshot,
        RepositorySnapshot previousSnapshot,
        string targetPath,
        IReadOnlyList<string>? fileScope = null,
        CancellationToken ct = default)
    {
        if (snapshot.TreeEntries is null) return 0;

        var current  = FilterByScope(snapshot.TreeEntries, fileScope);
        var previous = previousSnapshot.TreeEntries is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : FilterByScope(previousSnapshot.TreeEntries, fileScope);

        // Changed or added paths.
        var toWrite = current
            .Where(kv => !previous.TryGetValue(kv.Key, out var old) || old != kv.Value)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);

        // Deleted paths.
        foreach (var path in previous.Keys.Where(p => !current.ContainsKey(p)))
        {
            var fullPath = Path.Combine(targetPath, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath)) File.Delete(fullPath);
        }

        return await WriteEntriesAsync(toWrite, targetPath, ct).ConfigureAwait(false);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task<int> WriteEntriesAsync(
        Dictionary<string, string> entries, string targetPath, CancellationToken ct)
    {
        var concurrency = Math.Max(1, options.MaterializerConcurrency);
        var semaphore   = new SemaphoreSlim(concurrency, concurrency);
        var written     = 0;
        var tasks       = new List<Task>(entries.Count);

        foreach (var (relativePath, blobId) in entries)
        {
            var fullPath = Path.Combine(targetPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

            // Skip files already on disk that match the expected blob.
            if (File.Exists(fullPath) && FileMatchesBlob(fullPath, blobId))
                continue;

            await semaphore.WaitAsync(ct).ConfigureAwait(false);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    var result = await blobStore.TryGetBlobAsync(blobId, ct).ConfigureAwait(false);
                    if (!result.Found || result.Bytes is null) return;

                    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                    await File.WriteAllBytesAsync(fullPath, result.Bytes, ct).ConfigureAwait(false);
                    Interlocked.Increment(ref written);
                }
                finally
                {
                    semaphore.Release();
                }
            }, ct));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return written;
    }

    private static void DeleteAbsentFiles(
        string targetPath, HashSet<string> entrySet, IReadOnlyList<string>? fileScope)
    {
        if (!Directory.Exists(targetPath)) return;

        foreach (var fullPath in Directory.EnumerateFiles(targetPath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(targetPath, fullPath).Replace('\\', '/');
            if (fileScope is not null && !IsInScope(relative, fileScope)) continue;
            if (!entrySet.Contains(relative)) File.Delete(fullPath);
        }
    }

    private static Dictionary<string, string> FilterByScope(
        IReadOnlyDictionary<string, string> entries, IReadOnlyList<string>? fileScope)
    {
        if (fileScope is null || fileScope.Count == 0)
            return new Dictionary<string, string>(entries, StringComparer.Ordinal);

        return entries
            .Where(kv => IsInScope(kv.Key, fileScope))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    // A path is in scope if it exactly matches or starts with any scope entry (directory prefix).
    private static bool IsInScope(string relativePath, IReadOnlyList<string> fileScope) =>
        fileScope.Any(scope =>
            relativePath.Equals(scope, StringComparison.Ordinal) ||
            relativePath.StartsWith(scope.TrimEnd('/') + '/', StringComparison.Ordinal));

    private static bool FileMatchesBlob(string fullPath, string blobId)
    {
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            return BlobHasher.ComputeHash(bytes) == blobId;
        }
        catch { return false; }
    }
}
