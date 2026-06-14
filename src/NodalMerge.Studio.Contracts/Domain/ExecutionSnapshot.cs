namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Derived execution view. Not authoritative storage.
/// </summary>
public sealed record ExecutionSnapshot(
    string AgentId,
    string WorkUnitId,
    string? CurrentGoal,
    string? CurrentTask,
    string? WorkingHypothesis,
    IReadOnlyList<string> RecentActions,
    IReadOnlyList<string> Constraints,
    int FailureCount,
    int RollbackCount,
    string? NextSuggestedAction);
