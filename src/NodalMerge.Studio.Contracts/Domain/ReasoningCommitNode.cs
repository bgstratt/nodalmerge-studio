namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Reasoning commit record stored under studio/reasoning-commit/v1 — captures the agent's
/// reasoning at each orchestration step, linked to the work unit and agent that produced it.
/// </summary>
public sealed record ReasoningCommitNode(
    string CommitId,
    string WorkUnitId,
    string AgentId,
    string? AgentModel,
    string? AgentProvider,
    string InputStage,
    string Action,
    string? Reasoning,
    string? ProjectionSnapshotJson,
    DateTimeOffset CommittedAt,
    string? SessionId = null);