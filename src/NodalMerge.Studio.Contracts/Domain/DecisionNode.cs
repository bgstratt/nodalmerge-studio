namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Decision record stored under studio/decision/v1 — captures the outcome of a convergence review
/// with model identity and confidence metadata.
/// </summary>
public sealed record DecisionNode(
    string DecisionId,
    string WorkUnitId,
    string? ProposalId,
    DecisionOutcome Outcome,
    string? ReviewerAgentId,
    string? ReviewerModel,
    string? ReviewerProvider,
    double? Confidence,
    string? Rationale,
    DateTimeOffset DecidedAt,
    string? SessionId = null,
    // Slice 6.3a — denormalized at RecordAsync time from WorkUnitId's own RepositoryId. Null when
    // the work unit itself has no resolvable RepositoryId, or the decision predates 6.3a.
    string? RepositoryId = null);

public enum DecisionOutcome
{
    Accepted,
    Rejected,
    Deferred,
    Superseded
}