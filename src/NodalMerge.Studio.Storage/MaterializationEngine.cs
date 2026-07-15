using Microsoft.Extensions.Logging;
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

// Phase 7 — reconstructs workspace directories from a snapshot's tree map + CAS.
// The skip-already-matching optimization avoids re-fetching blobs for files already on disk
// with the correct content — critical for partial-eviction recovery.
// Slice 1.1 — the tree map itself is resolved via ISnapshotTreeResolver (inline legacy TreeEntries
// or a CAS tree blob keyed by TreeHash), never dereferenced from RepositorySnapshot directly.
internal sealed class MaterializationEngine(
    IBlobStoreProvider blobStore,
    WorkspaceOptions options,
    ISnapshotTreeResolver treeResolver,
    ILogger<MaterializationEngine>? logger = null) : IMaterializationEngine
{
    public async Task<int> MaterializeAsync(
        RepositorySnapshot snapshot,
        string targetPath,
        IReadOnlyList<string>? fileScope = null,
        CancellationToken ct = default)
    {
        // Phase 2 slice 2.4 — passes fileScope into the resolver so a v2 tree walk can prune
        // subtrees the scope can't reach (bounding CAS reads, not just the local file-write loop
        // below). FilterByScope is still applied afterward: it's a no-op against an
        // already-scoped map (same IsInScope predicate) but keeps behavior identical for legacy
        // snapshots (whose resolver-side filtering is itself just FilterByScope's twin) and for a
        // null scope (a no-op filter either way).
        var treeEntries = await treeResolver.ResolveTreeAsync(snapshot, fileScope, ct).ConfigureAwait(false);
        if (treeEntries is null) return 0;

        var entries = FilterByScope(treeEntries, fileScope);
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
        var currentEntries = await treeResolver.ResolveTreeAsync(snapshot, ct).ConfigureAwait(false);
        if (currentEntries is null) return 0;

        var current = FilterByScope(currentEntries, fileScope);
        var previousEntries = await treeResolver.ResolveTreeAsync(previousSnapshot, ct).ConfigureAwait(false);
        var previous = previousEntries is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : FilterByScope(previousEntries, fileScope);

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
                    if (!result.Found || result.Bytes is null)
                    {
                        // Silently leaving this file unwritten would make the workspace look like the
                        // path was deleted to any downstream diff (e.g. merge proposal generation) —
                        // log loudly so a CAS/origin gap is distinguishable from a real deletion.
                        logger?.LogWarning(
                            "File blob {BlobId} for {RelativePath} is missing from the blob store " +
                            "(CAS miss) — leaving the path unwritten; workspace at {TargetPath} will be " +
                            "incomplete.", blobId, relativePath, targetPath);
                        return;
                    }

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
