namespace NodalMerge.Studio.Contracts.Domain;

public sealed record RepositorySnapshot(
    string SnapshotId,
    string RepositoryId,
    string TreeHash,
    long Generation,
    DateTimeOffset CreatedAt,
    string? BaseSnapshotId = null,
    string? GitCommit = null,
    string? WorkUnitId = null,
    // "Bootstrap" | "WorkUnitCompletion" | "ImportedFromFilesystem"
    string? Source = null,
    // path → blobId map; null for pre-Phase-2 bootstrap snapshots written without this field.
    // Materializer and between-run sync fall back to op replay when null.
    IReadOnlyDictionary<string, string>? TreeEntries = null);
