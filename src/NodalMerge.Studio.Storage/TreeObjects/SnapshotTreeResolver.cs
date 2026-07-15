using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage.TreeObjects;

// plans/cas-distribution-and-storage.md Phase 1 slice 1.1 — the one place that knows how to turn a
// RepositorySnapshot into a path→blobId map, whichever of the three shapes it's in: legacy inline
// TreeEntries, cas-tree (TreeHash -> v1 flat blob), or pre-Phase-2 (neither — returns null, same as
// every caller already handled). Registered as a singleton — trees are immutable, so the resolved-
// root memo below is safe to share process-wide.
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

    public async Task<string> WriteTreeAsync(
        IReadOnlyDictionary<string, string> entries, CancellationToken ct = default)
    {
        if (blobStore is null)
            throw new InvalidOperationException(
                "Cannot write a tree object — no blob store is configured. Callers should check CanWrite first.");

        var bytes = CanonicalTreeSerializer.SerializeFlat(entries);
        var hash = BlobHasher.ComputeHash(bytes);
        await blobStore.PutBlobAsync(hash, bytes, "application/vnd.nodalmerge.tree+json", ct).ConfigureAwait(false);
        return hash;
    }

    public async Task<IReadOnlyCollection<string>> GetTreeBlobHashesAsync(
        RepositorySnapshot snapshot, CancellationToken ct = default)
    {
        if (snapshot.TreeEntries is not null) return Array.Empty<string>();
        if (!string.Equals(snapshot.TreeFormat, CasTreeFormat, StringComparison.Ordinal)) return Array.Empty<string>();
        if (blobStore is null) return Array.Empty<string>();

        // v1 (the only format WriteTreeAsync emits today) is a single root blob with no subtrees;
        // WalkTreeAsync's recursion is a no-op beyond the root until something writes v2. The walk
        // is re-run rather than reusing ResolveTreeAsync's memo — this method is only on the GC
        // reachability path (Phase 1.3 prep), not the hot materialize path, so the duplicate CAS
        // read is an acceptable simplicity/perf tradeoff for this slice.
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

    // Recursive tree-blob walk shared by ResolveTreeAsync and GetTreeBlobHashesAsync. Adds every
    // visited tree-blob hash to treeHashesOut (root included) and every file path->hash pair,
    // prefixed by the directory path walked so far, to entriesOut. Returns false (logging why) on
    // the first CAS miss or corrupt/unparseable blob encountered — miss and corrupt are logged with
    // distinct messages so operators can tell "the blob store lost data" from "something wrote a
    // malformed tree object" apart.
    private async Task<bool> WalkTreeAsync(
        string hash,
        string prefix,
        Dictionary<string, string> entriesOut,
        List<string> treeHashesOut,
        string snapshotId,
        CancellationToken ct)
    {
        treeHashesOut.Add(hash);

        var result = await blobStore!.TryGetBlobAsync(hash, ct).ConfigureAwait(false);
        if (!result.Found || result.Bytes is null)
        {
            logger.LogWarning(
                "Tree blob {TreeHash} referenced by snapshot {SnapshotId} is missing from the blob store (CAS miss).",
                hash, snapshotId);
            return false;
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
            return false;
        }

        switch (doc.Version)
        {
            case 1 when doc.FlatEntries is not null:
                foreach (var (path, fileHash) in doc.FlatEntries)
                    entriesOut[prefix.Length == 0 ? path : $"{prefix}/{path}"] = fileHash;
                return true;

            case 2 when doc.Entries is not null:
                foreach (var entry in doc.Entries)
                {
                    var childPath = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
                    if (entry.Kind == TreeEntryKind.File)
                    {
                        entriesOut[childPath] = entry.Hash;
                    }
                    else
                    {
                        var ok = await WalkTreeAsync(entry.Hash, childPath, entriesOut, treeHashesOut, snapshotId, ct)
                            .ConfigureAwait(false);
                        if (!ok) return false;
                    }
                }
                return true;

            default:
                logger.LogWarning(
                    "Tree blob {TreeHash} referenced by snapshot {SnapshotId} has an unrecognized shape (version {Version}).",
                    hash, snapshotId, doc.Version);
                return false;
        }
    }
}
