namespace NodalMerge.Studio.Contracts.Domain;

public enum ExecutionSessionStatus { Active, Paused, Completed, Abandoned }

public sealed record ExecutionSession(
    string SessionId,
    string RootWorkUnitId,
    ExecutionSessionStatus Status,
    string? ParentSessionId,
    string? ParentEventId,
    DateTimeOffset StartedAt,
    DateTimeOffset? PausedAt,
    DateTimeOffset? CompletedAt,
    string ModelConfigSnapshotJson,
    IReadOnlyList<string> ProfileIdSet);
