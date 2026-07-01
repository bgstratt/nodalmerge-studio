namespace NodalMerge.Studio.Contracts.Domain;

public enum ConflictStatus
{
    Open,
    Resolved,   // Phase 10 — a ConflictResolutionOp was recorded
    Dismissed,  // human chose to ignore
}

// Phase 9 — represents a structural DAG fork: two RepositoryOps that share the same OldBlobId
// (i.e., started from the same parent blob) but produced different NewBlobIds. This is a
// content-level conflict detectable at op-emit time, not at merge time.
//
// OldBlobId = null is valid: two concurrent Add ops on the same path (both "creating from nothing")
// are a conflict even though there is no parent blob.
public sealed record RepositoryConflict(
    string ConflictId,
    string RepositoryId,
    string Path,
    string OpIdA,               // op that landed first
    string OpIdB,               // op that triggered conflict detection
    string? OldBlobId,          // shared ancestor blob (null for concurrent Adds)
    string? BlobIdA,            // what OpA produced (null for Delete)
    string? BlobIdB,            // what OpB produced (null for Delete)
    DateTimeOffset DetectedAt,
    ConflictStatus Status = ConflictStatus.Open,
    string? WorkUnitIdA = null,
    string? WorkUnitIdB = null,
    string? ResolvedByConflictResolutionOpId = null);
