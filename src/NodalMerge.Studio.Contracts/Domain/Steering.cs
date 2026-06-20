namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Command to pause a running agent and redirect to a sibling fork with an injected constraint.
/// </summary>
public sealed record SteeringCommand(
    string WorkUnitId,
    string AgentId,
    string InjectedConstraint,
    string? ProfileId = null,
    string? SessionId = null);

/// <summary>
/// Command to fork a new work unit from a specific trajectory/decision node.
/// </summary>
public sealed record ForkFromNodeCommand(
    string WorkUnitId,
    string? NodeId = null,
    string? ProposalId = null,
    string? NewGoal = null,
    string? ConstraintText = null,
    string? ProfileId = null,
    string? SessionId = null);

/// <summary>
/// Result from a steering or fork-from-node operation.
/// </summary>
public sealed record SteeringResult(
    string SteeringDecisionId,
    string NewWorkUnitId,
    string OriginalWorkUnitId);

/// <summary>
/// Service for mid-execution steering — pause, inject constraint, fork at decision point.
/// </summary>
public interface ISteeringService
{
    Task<SteeringResult> PauseAndRedirectAsync(SteeringCommand command, CancellationToken ct = default);
    Task<SteeringResult> ForkFromNodeAsync(ForkFromNodeCommand command, CancellationToken ct = default);
}