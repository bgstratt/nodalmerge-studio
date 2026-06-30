namespace NodalMerge.Studio.Contracts.Domain;

// LLM credentials for use by LlmAssistedMergeStrategy. Passed through MergeContext so
// the strategy chain doesn't need a separate credentials-injection mechanism.
public sealed record LlmMergeCredentials(string Provider, string Model, string BaseUrl, string ApiKey);

// Phase 10 — inputs to a merge strategy. All content fields are null when the file was
// deleted by that side (Delete op) or didn't exist before (null OldBlobId).
public sealed record MergeContext(
    string ConflictId,
    string RepositoryId,
    string Path,
    string? BaseContent,
    string? ContentA,
    string? ContentB,
    LlmMergeCredentials? LlmCredentials = null);

// Result returned by a single IMergeStrategy attempt.
public sealed record MergeStrategyResult(
    bool Success,
    string? MergedContent,
    string StrategyName,
    string? FailureReason = null);

// Result of IConflictResolutionService.ResolveAsync — wraps the updated conflict record and
// the RepositoryOperation ID that represents the merged content in the op log.
// When all strategies fail, RequeueLosingWorkUnit = true signals the caller (REST endpoint or
// orchestrator) to re-trigger the losing work unit as a new work unit with parentWorkUnitId set.
public sealed record ConflictResolutionResult(
    bool Success,
    string ConflictId,
    string StrategyUsed,
    string? ResolutionOpId = null,
    string? FailureReason = null,
    bool RequeueLosingWorkUnit = false,
    string? RequeuedWorkUnitId = null);
