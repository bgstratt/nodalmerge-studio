namespace NodalMerge.Studio.Contracts.Domain;

public sealed record DeadLetterEntry(
    string EntryId,
    string WorkUnitId,
    string AgentId,
    PipelineStage Stage,
    string ProfileId,
    string Reason,
    string? LastProjectionSnapshot,
    int AttemptCount,
    DateTimeOffset OccurredAt,
    string? TaskId = null,
    bool MaxAttemptsReached = false);
