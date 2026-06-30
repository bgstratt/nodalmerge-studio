using Blake3;

namespace NodalMerge.Studio.Storage;

// Phase 11.75 — canonical blob ID computation for CAS.
// The nodalmerge host engine uses Blake3 (32 bytes, 64-char lowercase hex) for all blob
// content addresses. Using a different algorithm here produces IDs that are opaque strings
// to the engine — the same content under two different hashes cannot be found by either side.
//
// All callers that produce a string to pass to IBlobStoreProvider.PutBlobAsync or that
// compare against a blob ID from a RepositoryOperation MUST use this helper.
public static class BlobHasher
{
    // Compute the Blake3 content hash of a byte array. Returns a 64-character lowercase hex
    // string that matches the Rust core's `blake3::hash(data).to_hex()` output exactly.
    public static string ComputeHash(byte[] bytes) =>
        Hasher.Hash(bytes).ToString();

    // Overload for ReadOnlySpan<byte> to avoid allocations when the caller already has a span.
    public static string ComputeHash(ReadOnlySpan<byte> bytes) =>
        Hasher.Hash(bytes).ToString();
}
