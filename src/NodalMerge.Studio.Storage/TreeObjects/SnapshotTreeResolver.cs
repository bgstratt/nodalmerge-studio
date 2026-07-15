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

    public async Task<IReadOnlyDictionary<string, string>?> ResolveTreeAsync(
        RepositorySnapshot snapshot, CancellationToken ct = default)
    {
        // Legacy inline map always wins, no CAS fetch — this is the common case for every snapshot
        // written before this slice, and must never be redirected through the blob store.
        if (snapshot.TreeEntries is not null)
            return snapshot.TreeEntries;

        if (!string.Equals(snapshot.TreeFormat, CasTreeFormat, StringComparison.Ordinal))
            return null; // pre-Phase-2 legacy: neither inline entries nor a cas-tree marker.

        if (blobStore is null)
        {
            logger.LogWarning(
                "Cannot resolve cas-tree snapshot {SnapshotId} (TreeHash={TreeHash}) — no blob store is configured.",
                snapshot.SnapshotId, snapshot.TreeHash);
            return null;
        }

        if (_memo.TryGetValue(snapshot.TreeHash, out var cached))
            return cached;

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var treeHashes = new List<string>();
        var ok = await WalkTreeAsync(snapshot.TreeHash, "", entries, treeHashes, snapshot.SnapshotId, ct)
            .ConfigureAwait(false);
        if (!ok) return null;

        Memoize(snapshot.TreeHash, entries);
        return entries;
    }

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
    // path by the directory path walked so far. Returns false on the first CAS miss or corrupt/
    // unparseable blob encountered anywhere under hash.
    private async Task<bool> WalkTreeAsync(
        string hash,
        string prefix,
        Dictionary<string, string> entriesOut,
        List<string> treeHashesOut,
        string snapshotId,
        CancellationToken ct)
    {
        var (ok, fragment) = await ResolveFragmentAsync(hash, snapshotId, ct).ConfigureAwait(false);
        if (!ok) return false;

        treeHashesOut.AddRange(fragment!.DescendantTreeHashes);
        foreach (var (relPath, fileHash) in fragment.RelativeEntries)
            entriesOut[prefix.Length == 0 ? relPath : $"{prefix}/{relPath}"] = fileHash;
        return true;
    }

    // Resolves a single tree blob's fragment, recursing into child directories (each recursion
    // itself cache-assisted). Miss and corrupt are logged with distinct messages so operators can
    // tell "the blob store lost data" from "something wrote a malformed tree object" apart.
    private async Task<(bool Ok, TreeFragment? Fragment)> ResolveFragmentAsync(
        string hash, string snapshotId, CancellationToken ct)
    {
        if (_fragmentCache.TryGetValue(hash, out var cached))
            return (true, cached);

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

        switch (doc.Version)
        {
            case 1 when doc.FlatEntries is not null:
            {
                var fragment = new TreeFragment(doc.FlatEntries, new[] { hash });
                CacheFragment(hash, fragment);
                return (true, fragment);
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

                    var (childOk, childFragment) =
                        await ResolveFragmentAsync(entry.Hash, snapshotId, ct).ConfigureAwait(false);
                    if (!childOk) return (false, null);

                    foreach (var (relPath, fileHash) in childFragment!.RelativeEntries)
                        relEntries[$"{entry.Name}/{relPath}"] = fileHash;
                    descendants.AddRange(childFragment.DescendantTreeHashes);
                }

                var fragment = new TreeFragment(relEntries, descendants);
                CacheFragment(hash, fragment);
                return (true, fragment);
            }

            default:
                logger.LogWarning(
                    "Tree blob {TreeHash} referenced by snapshot {SnapshotId} has an unrecognized shape (version {Version}).",
                    hash, snapshotId, doc.Version);
                return (false, null);
        }
    }
}
