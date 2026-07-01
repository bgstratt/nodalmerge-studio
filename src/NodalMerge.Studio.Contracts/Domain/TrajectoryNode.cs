namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Trajectory record stored under studio/trajectory/v1 — captures a decision's evolution
/// through the goal → decomposition → execution → convergence lifecycle as a replayable timeline.
/// </summary>
public sealed record TrajectoryNode(
    string TrajectoryId,
    string WorkUnitId,
    string BranchId,
    string Goal,
    TrajectoryPhase Phase,
    string? AgentId,
    string? Model,
    string? Provider,
    string? ParentTrajectoryId,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? SessionId = null);

public enum TrajectoryPhase
{
    GoalDefined,
    Decomposed,
    Executing,
    Proposed,
    Converging,
    Converged,
    Forked,
    Abandoned
}