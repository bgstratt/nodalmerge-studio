namespace NodalMerge.Studio.Contracts.Domain;

public sealed record BlobIndexEntry(
    string BlobHash,
    long SizeBytes,
    int ReferenceCount,
    IReadOnlyList<string> RepositoryIds,
    IReadOnlyList<string> SnapshotIds,
    IReadOnlyList<string> WorkUnitIds,
    DateTimeOffset LastAccessedAt,
    bool Pinned);

public sealed record BlobIndex(
    IReadOnlyList<BlobIndexEntry> Entries);
