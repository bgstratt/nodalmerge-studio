namespace NodalMerge.Studio.Contracts.Domain;

public enum OperationType
{
    Add,
    Replace,
    Delete,
    Rename,
    Move,
    Import,
}

public sealed record RepositoryOperation(
    string OperationId,
    string RepositoryId,
    string? ParentSnapshotId,
    OperationType Kind,
    string Path,
    DateTimeOffset Timestamp,
    string? WorkUnitId = null,
    string? OldBlobId = null,
    string? NewBlobId = null,
    string? AgentId = null,
    string? Reason = null);
