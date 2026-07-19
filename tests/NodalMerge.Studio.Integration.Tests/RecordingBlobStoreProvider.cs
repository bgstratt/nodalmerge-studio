using NodalMerge.Host.Abstractions.Providers;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 1.2 (plans/cas-distribution-and-storage.md Phase 1) — a thin <see cref="IBlobStoreProvider"/>
/// wrapper that records every hash passed to <c>PutBlobAsync</c>, in call order, without changing the
/// wrapped store's behavior. Used by the directory-tree structural-sharing acceptance tests to count
/// how many *new* tree blobs a write introduces: a hash the wrapped store already had before the call
/// is still recorded (PutBlobAsync is called unconditionally by the writer — an unchanged subtree
/// still gets a no-op Put), so "new" is determined by the caller comparing <see cref="PutHashes"/>
/// against a snapshot of hashes known to exist before the write, not by this wrapper itself.
///
/// Slice 2.4 (Phase 2) — extended to also record every hash passed to <c>TryGetBlobAsync</c>, in call
/// order including duplicates, via <see cref="GetHashes"/>. Used by the scoped-fetch acceptance tests
/// to assert an upper bound on how many blobs (tree or file) a scoped resolve/materialize/prefetch
/// actually reads from the store, without changing behavior for the many existing Put-only call sites.
/// </summary>
public sealed class RecordingBlobStoreProvider(IBlobStoreProvider inner) : IBlobStoreProvider
{
    public List<string> PutHashes { get; } = [];
    public List<string> GetHashes { get; } = [];

    public ValueTask<BlobReadResult> TryGetBlobAsync(string hashHex, CancellationToken cancellationToken = default)
    {
        GetHashes.Add(hashHex);
        return inner.TryGetBlobAsync(hashHex, cancellationToken);
    }

    public ValueTask PutBlobAsync(string hashHex, byte[] bytes, string? contentType, CancellationToken cancellationToken = default)
    {
        PutHashes.Add(hashHex);
        return inner.PutBlobAsync(hashHex, bytes, contentType, cancellationToken);
    }
}
