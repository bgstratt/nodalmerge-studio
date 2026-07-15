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
/// </summary>
public sealed class RecordingBlobStoreProvider(IBlobStoreProvider inner) : IBlobStoreProvider
{
    public List<string> PutHashes { get; } = [];

    public ValueTask<BlobReadResult> TryGetBlobAsync(string hashHex, CancellationToken cancellationToken = default) =>
        inner.TryGetBlobAsync(hashHex, cancellationToken);

    public ValueTask PutBlobAsync(string hashHex, byte[] bytes, string? contentType, CancellationToken cancellationToken = default)
    {
        PutHashes.Add(hashHex);
        return inner.PutBlobAsync(hashHex, bytes, contentType, cancellationToken);
    }
}
