using System.Collections.Concurrent;
using System.Linq;
using NodalMerge.Host.Abstractions.Providers;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 1.1 (plans/cas-distribution-and-storage.md Phase 1) follow-up — a real, ConcurrentDictionary-
/// backed <see cref="IBlobStoreProvider"/> test double that actually round-trips Put→Get. Replaces the
/// three previously-duplicated "FakeBlobStoreProvider" no-ops (Put a no-op, Get always Missing) in
/// RepositoryImportServiceTests/WorkspaceSwitchTests/FileSystemWorkspaceServiceCasLoggingTests — those
/// were enough to make the CAS dual-write code path reachable, but slice 1.1 stores tree objects in
/// the blob store and reads them back (ISnapshotTreeResolver), so a store that never actually stores
/// anything makes every cas-tree snapshot unresolvable, which is wrong for every test in this repo
/// that isn't specifically exercising the CAS-miss/legacy path.
/// </summary>
public sealed class InMemoryBlobStoreProvider : IBlobStoreProvider
{
    private readonly ConcurrentDictionary<string, (byte[] Bytes, string? ContentType)> _blobs = new(StringComparer.Ordinal);

    public ValueTask<BlobReadResult> TryGetBlobAsync(string hashHex, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(_blobs.TryGetValue(hashHex, out var entry)
            ? BlobReadResult.Hit(entry.Bytes, entry.ContentType)
            : BlobReadResult.Missing);

    public ValueTask PutBlobAsync(string hashHex, byte[] bytes, string? contentType, CancellationToken cancellationToken = default)
    {
        _blobs[hashHex] = (bytes, contentType);
        return ValueTask.CompletedTask;
    }

    /// <summary>Test hook — removes a blob to simulate a CAS miss (e.g. eviction, corruption) for a
    /// hash that was previously stored, without needing a whole second store implementation.</summary>
    public bool Remove(string hashHex) => _blobs.TryRemove(hashHex, out _);

    /// <summary>Total distinct blobs currently stored — slice 1.2 vector/acceptance tests use this to
    /// assert the exact number of tree blobs a write produced.</summary>
    public int Count => _blobs.Count;

    /// <summary>All hashes currently stored, e.g. to snapshot "what existed before generation N" for
    /// a structural-sharing acceptance test.</summary>
    public IReadOnlyCollection<string> Hashes => _blobs.Keys.ToList();
}
