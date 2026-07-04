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
    bool MaxAttemptsReached = false,
    // Captured from whatever credentials the failed run actually used, so retry doesn't have to
    // re-derive them from the in-memory orchestrator registry — that registry is ephemeral (lost
    // on Host restart, or once the orchestrator's own loop has completed) and may simply be gone
    // by the time a human gets around to retrying a dead-lettered item.
    string? Model = null,
    string? BaseUrl = null,
    string? ApiKey = null,
    string? Provider = null,
    // Structured failure classification — see FailureKind for the two-track recovery model this
    // enables. Defaults to Exception for any recording path not yet updated to pass a real value.
    FailureKind Kind = FailureKind.Exception);
