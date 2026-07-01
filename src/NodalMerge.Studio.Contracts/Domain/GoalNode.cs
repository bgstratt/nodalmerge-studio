namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Decision-centric goal record stored under studio/goal/v1.
/// </summary>
public sealed record GoalNode(
    string GoalId,
    string Goal,
    string WorkUnitId,
    string BranchId,
    GoalStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Owner,
    string? ParentGoalId = null,
    IReadOnlyList<string>? ChildGoalIds = null,
    string? SessionId = null,
    string? PauseReason = null)
{
    public IReadOnlyList<string> ChildGoalIds { get; init; } = ChildGoalIds ?? [];
}

public enum GoalStatus
{
    Exploring,
    Converging,
    Converged,
    Blocked,
    Paused,
    Abandoned
}