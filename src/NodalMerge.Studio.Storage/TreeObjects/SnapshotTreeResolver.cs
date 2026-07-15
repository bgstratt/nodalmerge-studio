using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage.TreeObjects;

// plans/cas-distribution-and-storage.md Phase 1 slices 1.1/1.2 — the one place that knows how to
// turn a RepositorySnapshot into a path→blobId map, whichever of the three shapes it's in: legacy
// inline TreeEntries, cas-tree (TreeHash -> a v1 flat blob or, since slice 1.2, a v2 per-directory
// blob tree), or pre-Phase-2 (neither — returns null, same as every caller already handled).
// WriteTreeAsync writes v2 (git-tree style, one blob per directory); v1 blobs written by earlier
// generations remain readable forever (append-only, AP-5) via the same walk. Registered as a
// singleton — trees are immutable, so the caches below are safe to share process-wide.
internal sealed class SnapshotTreeResolver(
    IBlobStoreProvider? blobStore,
    ILogger<SnapshotTreeResolver> logger) : ISnapshotTreeResolver
{
    private const string CasTreeFormat = "cas-tree";

    // Bounded memo of resolved root maps, keyed by TreeHash. Trees are content-addressed and
    // immutable, so a cached entry never goes stale — the cap exists purely to bound memory, not
    // for correctness. Eviction is a trivial full-clear when the cap is hit: simple, and the only
    // cost of over-clearing is a re-walk of the CAS on the next resolve, never a correctness issue.
    private const int MemoCap = 32;
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> _memo =
        new(StringComparer.Ordinal);

    public bool CanWrite => blobStore is not null;

    public Task<IReadOnlyDictionary<string, string>?> ResolveTreeAsync(
        RepositorySnapshot snapshot, CancellationToken ct = default) =>
        ResolveTreeAsync(snapshot, fileScope: null, ct);

    // Phase 2 slice 2.4 — scope-aware resolution. fileScope null/empty takes the original
    // whole-map path (memo-cache-assisted); a non-empty scope prunes the v2 walk so subtrees the
    // scope can never reach are never fetched (WalkTreeScopedAsync), or filters the already-in-
    // memory/single-blob map for v1/legacy shapes where there's no per-directory structure to prune.
    public async Task<IReadOnlyDictionary<string, string>?> ResolveTreeAsync(
        RepositorySnapshot snapshot, IReadOnlyList<string>? fileScope, CancellationToken ct = default)
    {
        var hasScope = fileScope is { Count: > 0 };

        // Legacy inline map always wins, no CAS fetch — this is the common case for every snapshot
        // written before this slice, and must never be redirected through the blob store.
        if (snapshot.TreeEntries is not null)
        {
            if (!hasScope) return snapshot.TreeEntries;

            // v1/legacy: no per-directory structure to prune — resolve fully (already in memory,
            // so this is free) then filter, same predicate MaterializationEngine.IsInScope uses.
            return snapshot.TreeEntries
                .Where(kv => IsInScope(kv.Key, fileScope!))
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        }

        if (!string.Equals(snapshot.TreeFormat, CasTreeFormat, StringComparison.Ordinal))
            return null; // pre-Phase-2 legacy: neither inline entries nor a cas-tree marker.

        if (blobStore is null)
        {
            logger.LogWarning(
                "Cannot resolve cas-tree snapshot {SnapshotId} (TreeHash={TreeHash}) — no blob store is configured.",
                snapshot.SnapshotId, snapshot.TreeHash);
            return null;
        }

        if (!hasScope)
        {
            if (_memo.TryGetValue(snapshot.TreeHash, out var cached))
                return cached;

            var entries = new Dictionary<string, string>(StringComparer.Ordinal);
            var treeHashes = new List<string>();
            var (ok, _) = await WalkTreeAsync(snapshot.TreeHash, "", entries, treeHashes, snapshot.SnapshotId, ct)
                .ConfigureAwait(false);
            if (!ok) return null;

            Memoize(snapshot.TreeHash, entries);
            return entries;
        }

        // v2 (or a v1 blob, handled as a single-directory-equivalent case inside the walk):
        // scope-pruned — a subtree is only fetched if it could contain an in-scope path.
        var scopedEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        var scopedOk = await WalkTreeScopedAsync(
            snapshot.TreeHash, "", fileScope!, scopedEntries, snapshot.SnapshotId, ct).ConfigureAwait(false);
        return scopedOk ? scopedEntries : null;
    }

    // Phase 1 slice 1.3 — see the interface doc comment for the fail-closed contract. Reuses the
    // same cache-assisted walk as ResolveTreeAsync/GetTreeBlobHashesAsync (WalkTreeAsync), but
    // never swallows a failure into null: a partial reachable set is unsafe for a GC caller, so any
    // miss/corruption anywhere in the walk becomes a thrown exception naming the snapshot and hash.
    public async Task<IReadOnlySet<string>> GetReachableHashesAsync(
        RepositorySnapshot snapshot, CancellationToken ct = default)
    {
        // Legacy inline map: file hashes only — there's no tree-object concept for it to protect.
        if (snapshot.TreeEntries is not null)
            return new HashSet<string>(snapshot.TreeEntries.Values, StringComparer.Ordinal);

        if (!string.Equals(snapshot.TreeFormat, CasTreeFormat, StringComparison.Ordinal))
            return new HashSet<string>(StringComparer.Ordinal); // pre-Phase-2: nothing to protect.

        if (blobStore is null)
        {
            throw new InvalidOperationException(
                $"Cannot compute reachable hashes for snapshot '{snapshot.SnapshotId}' " +
                $"(TreeHash={snapshot.TreeHash}) — no blob store is configured. A reachable set " +
                "computed without one would be an under-approximation; refusing to return a " +
                "partial result (fail closed).");
        }

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var treeHashes = new List<string>();
        var (ok, failedHash) = await WalkTreeAsync(
            snapshot.TreeHash, "", entries, treeHashes, snapshot.SnapshotId, ct).ConfigureAwait(false);
        if (!ok)
        {
            throw new InvalidOperationException(
                $"Cannot compute reachable hashes for snapshot '{snapshot.SnapshotId}': tree object " +
                $"'{failedHash}' could not be resolved (CAS miss or corrupt blob — see prior " +
                "warnings). A partial reachable set is unsafe to hand to a GC sweep; refusing to " +
                "return one (fail closed).");
        }

        var reachable = new HashSet<string>(treeHashes, StringComparer.Ordinal);
        foreach (var fileHash in entries.Values) reachable.Add(fileHash);
        return reachable;
    }

    // A path is in scope if it exactly matches or starts with any scope entry (directory prefix) —
    // must stay in lockstep with MaterializationEngine.IsInScope (private there; duplicated here on
    // purpose rather than shared, since the two live in different assemblies and this is a
    // three-line predicate, not a seam worth a shared package for).
    private static bool IsInScope(string relativePath, IReadOnlyList<string> fileScope) =>
        fileScope.Any(scope =>
            relativePath.Equals(scope, StringComparison.Ordinal) ||
            relativePath.StartsWith(scope.TrimEnd('/') + '/', StringComparison.Ordinal));

    // Decides whether a not-yet-fetched directory could contain an in-scope file, without
    // descending into it — this is what lets the v2 walk skip subtrees the scope can never reach.
    // A scope entry can itself be a file path or a directory prefix (IsInScope doesn't distinguish
    // the two), so "could contain" is the same relationship as IsInScope, tested one level up and
    // in both directions: either (a) some scope entry reaches downward past this directory (the
    // directory is a strict ancestor of the scope entry, so descending is the only way to reach
    // it), or (b) this directory itself sits at-or-below a directory-shaped scope entry (so
    // everything under it is automatically in scope, the same "starts with scope + '/'" check
    // IsInScope does for a leaf file, applied to the directory path instead).
    private static bool CouldContainScope(string dirPath, IReadOnlyList<string> fileScope) =>
        fileScope.Any(scope =>
            scope.Equals(dirPath, StringComparison.Ordinal) ||
            scope.StartsWith(dirPath + "/", StringComparison.Ordinal) ||
            dirPath.Equals(scope.TrimEnd('/'), StringComparison.Ordinal) ||
            dirPath.StartsWith(scope.TrimEnd('/') + "/", StringComparison.Ordinal));

    // Slice 1.2 — writes a v2 directory tree, git-tree style: one blob per directory, post-order
    // (children before parents), so a parent's blob can embed its children's already-computed
    // hashes. Building the whole trie in memory first (rather than streaming) keeps the recursion
    // simple and is cheap relative to the CAS round-trips it drives — those, not the trie build,
    // dominate cost. Because PutBlobAsync is a no-op for a hash the store already has, an unchanged
    // subtree (byte-identical bytes -> byte-identical hash across generations) costs a hash +
    // existence check here, never a rewrite — this is what makes S1.2's sharing acceptance
    // criterion ("1-file change writes <= depth-of-path new tree objects") hold.
    public async Task<string> WriteTreeAsync(
        IReadOnlyDictionary<string, string> entries, CancellationToken ct = default)
    {
        if (blobStore is null)
            throw new InvalidOperationException(
                "Cannot write a tree object — no blob store is configured. Callers should check CanWrite first.");

        var root = BuildTrie(entries);
        return await WriteDirectoryAsync(root, ct).ConfigureAwait(false);
    }

    // Splits every flat "a/b/c.txt" path into path segments and threads them into an in-memory
    // directory trie. No validation of malformed input (e.g. a name used as both a file and a
    // directory) — the format doc rules that out for well-formed snapshots, and every caller here
    // already builds `entries` from real file paths.
    private static DirNode BuildTrie(IReadOnlyDictionary<string, string> entries)
    {
        var root = new DirNode();
        foreach (var (path, fileHash) in entries)
        {
            var segments = path.Split('/');
            var node = root;
            for (var i = 0; i < segments.Length - 1; i++)
            {
                var name = segments[i];
                if (!node.Dirs.TryGetValue(name, out var child))
                {
                    child = new DirNode();
                    node.Dirs[name] = child;
                }
                node = child;
            }
            node.Files[segments[^1]] = fileHash;
        }
        return root;
    }

    // Post-order: recurse into every child directory first so its blob hash exists before this
    // directory's own entries (which reference it) are serialized.
    private async Task<string> WriteDirectoryAsync(DirNode node, CancellationToken ct)
    {
        var entries = new List<TreeEntry>(node.Files.Count + node.Dirs.Count);
        foreach (var (name, fileHash) in node.Files)
            entries.Add(new TreeEntry(name, TreeEntryKind.File, fileHash));

        foreach (var (name, child) in node.Dirs)
        {
            var childHash = await WriteDirectoryAsync(child, ct).ConfigureAwait(false);
            entries.Add(new TreeEntry(name, TreeEntryKind.Directory, childHash));
        }

        var bytes = CanonicalTreeSerializer.SerializeDirectory(entries);
        var hash = BlobHasher.ComputeHash(bytes);
        await blobStore!.PutBlobAsync(hash, bytes, "application/vnd.nodalmerge.tree+json", ct).ConfigureAwait(false);
        return hash;
    }

    // In-memory build target for WriteTreeAsync — never serialized itself, just an intermediate
    // shape between the flat path->hash map and the per-directory v2 blobs.
    private sealed class DirNode
    {
        public Dictionary<string, string> Files { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DirNode> Dirs { get; } = new(StringComparer.Ordinal);
    }

    public async Task<IReadOnlyCollection<string>> GetTreeBlobHashesAsync(
        RepositorySnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.TreeEntries is not null) return Array.Empty<string>();
        if (!string.Equals(snapshot.TreeFormat, CasTreeFormat, StringComparison.Ordinal)) return Array.Empty<string>();
        if (blobStore is null) return Array.Empty<string>();

        // Re-walks (via the fragment cache below) rather than reusing ResolveTreeAsync's whole-map
        // memo — this method is only on the GC reachability path (Phase 1.3 prep), not the hot
        // materialize path, so a cache-assisted walk is an acceptable simplicity/perf tradeoff.
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var treeHashes = new List<string>();
        await WalkTreeAsync(snapshot.TreeHash, "", entries, treeHashes, snapshot.SnapshotId, ct).ConfigureAwait(false);
        return treeHashes;
    }

    // Phase 2 slice 2.4 — like WalkTreeAsync, but for a v2 directory tree it never fetches a
    // subtree the scope can't reach (CouldContainScope), and for a file entry it only records it
    // when in scope (IsInScope). A v1 flat-tree blob has no per-directory structure to prune, so it
    // resolves in one fetch and filters in memory — still only the one blob, same as a full resolve
    // would need anyway. Returns false (no partial map) on the first CAS miss/corrupt blob
    // encountered among the subtrees actually visited.
    private async Task<bool> WalkTreeScopedAsync(
        string hash,
        string prefix,
        IReadOnlyList<string> fileScope,
        Dictionary<string, string> entriesOut,
        string snapshotId,
        CancellationToken ct)
    {
        var (ok, doc) = await FetchAndParseDocAsync(hash, snapshotId, ct).ConfigureAwait(false);
        if (!ok) return false;

        if (doc!.Version == 1 && doc.FlatEntries is not null)
        {
            foreach (var (relPath, fileHash) in doc.FlatEntries)
            {
                var fullPath = prefix.Length == 0 ? relPath : $"{prefix}/{relPath}";
                if (IsInScope(fullPath, fileScope))
                    entriesOut[fullPath] = fileHash;
            }
            return true;
        }

        if (doc.Version == 2 && doc.Entries is not null)
        {
            foreach (var entry in doc.Entries)
            {
                var fullPath = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";

                if (entry.Kind == TreeEntryKind.File)
                {
                    if (IsInScope(fullPath, fileScope))
                        entriesOut[fullPath] = entry.Hash;
                    continue;
                }

                // Directory: skip entirely (no fetch) unless the scope could reach into it —
                // this is the fetch-avoidance that bounds CAS reads to the scope.
                if (!CouldContainScope(fullPath, fileScope)) continue;

                var childOk = await WalkTreeScopedAsync(
                    entry.Hash, fullPath, fileScope, entriesOut, snapshotId, ct).ConfigureAwait(false);
                if (!childOk) return false;
            }
            return true;
        }

        logger.LogWarning(
            "Tree blob {TreeHash} referenced by snapshot {SnapshotId} has an unrecognized shape (version {Version}).",
            hash, snapshotId, doc.Version);
        return false;
    }

    private void Memoize(string hash, IReadOnlyDictionary<string, string> resolved)
    {
        if (_memo.Count >= MemoCap)
            _memo.Clear();
        _memo[hash] = resolved;
    }

    // One subtree's resolved shape, relative to its own root (never prefixed) — cacheable because
    // a subtree's content-addressed hash fully determines both fields, independent of where in the
    // repo the subtree is mounted. RelativeEntries: every file path within this subtree (relative,
    // '/'-joined) -> file blob hash. DescendantTreeHashes: this subtree's own blob hash plus every
    // nested subtree's blob hash (v1 fragments are always a single-element list: just their own
    // hash, since v1 has no subtrees).
    private sealed record TreeFragment(
        IReadOnlyDictionary<string, string> RelativeEntries,
        IReadOnlyList<string> DescendantTreeHashes);

    // Bounded fragment cache, keyed by tree-blob hash. Trees are content-addressed and immutable
    // like the whole-map memo above, so a cached fragment never goes stale; this is what lets
    // repeated resolution across generations skip re-fetching and re-parsing every subtree whose
    // hash didn't change. Same simple clear-on-overflow eviction as the whole-map memo — bounding
    // memory is the only goal, a post-clear miss just re-walks that subtree once more.
    private const int FragmentCacheCap = 4096;
    private readonly ConcurrentDictionary<string, TreeFragment> _fragmentCache =
        new(StringComparer.Ordinal);

    private void CacheFragment(string hash, TreeFragment fragment)
    {
        if (_fragmentCache.Count >= FragmentCacheCap)
            _fragmentCache.Clear();
        _fragmentCache[hash] = fragment;
    }

    // Thin wrapper shared by ResolveTreeAsync and GetTreeBlobHashesAsync: resolves hash's fragment
    // (cache-assisted) and merges it into the caller's accumulators, prefixing every relative file
    // path by the directory path walked so far. Returns (false, <failing hash>) on the first CAS
    // miss or corrupt/unparseable blob encountered anywhere under hash — the failing hash feeds
    // GetReachableHashesAsync's fail-closed exception message; callers that don't need it (the
    // no-scope ResolveTreeAsync path, GetTreeBlobHashesAsync) just discard it.
    private async Task<(bool Ok, string? FailedHash)> WalkTreeAsync(
        string hash,
        string prefix,
        Dictionary<string, string> entriesOut,
        List<string> treeHashesOut,
        string snapshotId,
        CancellationToken ct)
    {
        var (ok, fragment, failedHash) = await ResolveFragmentAsync(hash, snapshotId, ct).ConfigureAwait(false);
        if (!ok) return (false, failedHash);

        treeHashesOut.AddRange(fragment!.DescendantTreeHashes);
        foreach (var (relPath, fileHash) in fragment.RelativeEntries)
            entriesOut[prefix.Length == 0 ? relPath : $"{prefix}/{relPath}"] = fileHash;
        return (true, null);
    }

    // Resolves a single tree blob's fragment, recursing into child directories (each recursion
    // itself cache-assisted). Miss and corrupt are logged with distinct messages so operators can
    // tell "the blob store lost data" from "something wrote a malformed tree object" apart.
    private async Task<(bool Ok, TreeFragment? Fragment, string? FailedHash)> ResolveFragmentAsync(
        string hash, string snapshotId, CancellationToken ct)
    {
        if (_fragmentCache.TryGetValue(hash, out var cached))
            return (true, cached, null);

        var result = await blobStore!.TryGetBlobAsync(hash, ct).ConfigureAwait(false);
        if (!result.Found || result.Bytes is null)
        {
            logger.LogWarning(
                "Tree blob {TreeHash} referenced by snapshot {SnapshotId} is missing from the blob store (CAS miss).",
                hash, snapshotId);
            return (false, null, hash);
        }

        TreeDocument doc;
        try
        {
            doc = CanonicalTreeSerializer.Parse(result.Bytes);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex,
                "Tree blob {TreeHash} referenced by snapshot {SnapshotId} is corrupt or unparseable.",
                hash, snapshotId);
            return (false, null, hash);
        }

        switch (doc.Version)
        {
            case 1 when doc.FlatEntries is not null:
            {
                var fragment = new TreeFragment(doc.FlatEntries, new[] { hash });
                CacheFragment(hash, fragment);
                return (true, fragment, null);
            }

            case 2 when doc.Entries is not null:
            {
                var relEntries = new Dictionary<string, string>(StringComparer.Ordinal);
                var descendants = new List<string> { hash };
                foreach (var entry in doc.Entries)
                {
                    if (entry.Kind == TreeEntryKind.File)
                    {
                        relEntries[entry.Name] = entry.Hash;
                        continue;
                    }

                    var (childOk, childFragment, childFailedHash) =
                        await ResolveFragmentAsync(entry.Hash, snapshotId, ct).ConfigureAwait(false);
                    if (!childOk) return (false, null, childFailedHash);

                    foreach (var (relPath, fileHash) in childFragment!.RelativeEntries)
                        relEntries[$"{entry.Name}/{relPath}"] = fileHash;
                    descendants.AddRange(childFragment.DescendantTreeHashes);
                }

                var fragment = new TreeFragment(relEntries, descendants);
                CacheFragment(hash, fragment);
                return (true, fragment, null);
            }

            default:
                logger.LogWarning(
                    "Tree blob {TreeHash} referenced by snapshot {SnapshotId} has an unrecognized shape (version {Version}).",
                    hash, snapshotId, doc.Version);
                return (false, null, hash);
        }
    }

    // Raw parsed-document cache, keyed by tree-blob hash — deliberately separate from the
    // recursive TreeFragment cache above: a TreeFragment for a directory already embeds every
    // descendant's resolved content, so caching only the fragment would force the scoped walk to
    // either fetch a subtree it doesn't need (to populate a fragment) or duplicate the recursive
    // resolution logic. This cache stores just one directory's own parsed entries (one CAS fetch's
    // worth), which is exactly the granularity WalkTreeScopedAsync prunes at.
    private const int DocCacheCap = 4096;
    private readonly ConcurrentDictionary<string, TreeDocument> _docCache =
        new(StringComparer.Ordinal);

    private void CacheDoc(string hash, TreeDocument doc)
    {
        if (_docCache.Count >= DocCacheCap)
            _docCache.Clear();
        _docCache[hash] = doc;
    }

    // Fetches and parses a single tree blob (cache-assisted, shared with the fragment cache's own
    // fetch — a hash resolved via either cache warms both, since a TreeDocument is trivially
    // derivable from having already fetched the bytes). Used by the scope-pruned walk, which needs
    // one directory at a time rather than a whole recursively-resolved subtree.
    private async Task<(bool Ok, TreeDocument? Doc)> FetchAndParseDocAsync(
        string hash, string snapshotId, CancellationToken ct)
    {
        if (_docCache.TryGetValue(hash, out var cachedDoc))
            return (true, cachedDoc);

        var result = await blobStore!.TryGetBlobAsync(hash, ct).ConfigureAwait(false);
        if (!result.Found || result.Bytes is null)
        {
            logger.LogWarning(
                "Tree blob {TreeHash} referenced by snapshot {SnapshotId} is missing from the blob store (CAS miss).",
                hash, snapshotId);
            return (false, null);
        }

        TreeDocument doc;
        try
        {
            doc = CanonicalTreeSerializer.Parse(result.Bytes);
        }
        catch (FormatException ex)
        {
            logger.LogWarning(ex,
                "Tree blob {TreeHash} referenced by snapshot {SnapshotId} is corrupt or unparseable.",
                hash, snapshotId);
            return (false, null);
        }

        CacheDoc(hash, doc);
        return (true, doc);
    }
}
