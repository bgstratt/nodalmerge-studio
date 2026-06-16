namespace NodalMerge.Studio.Contracts.Domain;

// OrchestrationAction is defined in ExecutionEventPayloads.cs (10b.2) alongside
// OrchestrationDecisionPayload — both this dedicated log and the unified event stream
// share the same action vocabulary; see the as-built note in plans/phase-3-foundations.md.
public sealed record OrchestrationEvent(
    string EventId,
    string WorkUnitId,
    string OrchestratorAgentId,
    PipelineStage InputStage,
    string InputProjectionSnapshot,
    OrchestrationAction Action,
    IReadOnlyList<string> SpawnedIds,
    string? Reason,
    DateTimeOffset OccurredAt);
