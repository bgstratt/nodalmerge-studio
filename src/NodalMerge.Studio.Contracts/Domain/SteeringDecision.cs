namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Steering decision recorded under studio/steering-decision/v1 — captures a human-injected
/// constraint/redirect mid-execution, forking the work unit at its current decision node.
/// </summary>
public sealed record SteeringDecision(
    string SteeringDecisionId,
    string WorkUnitId,
    string? AgentId,
    string InjectedConstraint,
    string? NewChildWorkUnitId,
    DateTimeOffset SteeredAt,
    string? SessionId = null);