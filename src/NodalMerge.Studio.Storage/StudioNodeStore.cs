using System.Collections.Concurrent;

namespace NodalMerge.Studio.Storage;

public static class StudioNodeKind
{
    public const string WorkUnitV1 = "studio/work-unit/v1";
    public const string TaskV1 = "studio/task/v1";
    public const string MergeProposalV1 = "studio/merge-proposal/v1";
    public const string KnownGoodStateV1 = "studio/known-good-state/v1";
    public const string BranchV1 = "studio/branch/v1";
    public const string AgentProfileV1 = "studio/agent-profile/v1";
    public const string SchedulerV1 = "studio/scheduler/v1";
    public const string ExecutionSessionV1 = "studio/execution-session/v1";
    public const string ExecutionEventV1    = "studio/execution-event/v1";
    public const string CommandResultV1     = "studio/command-result/v1";
    public const string AgentWorkspaceV1    = "studio/agent-workspace/v1";
    public const string ArtifactRefV1       = "studio/artifact-ref/v1";
    public const string OrchestrationEventV1 = "studio/orchestration-event/v1";
    public const string ChangeIntentV1       = "studio/change-intent/v1";
    public const string DeadLetterV1         = "studio/dead-letter/v1";
    // Safe-to-persist subset of a goal's Default-profile credential registration (no ApiKey — see
    // InMemoryAgentRuntimeService's GoalRoutingConfig) so AutoReviewProfileId/
    // EnabledDomainAgents/stage routing survive a Host restart without ever writing a secret.
    public const string GoalRoutingV1 = "studio/goal-routing/v1";
    // Legacy alias for GoalRoutingV1 from when this record was named "orchestrator routing"
    // (plans/orchestrator-pure-service.md M1). Never written anymore; rehydration still reads it
    // so goals in flight across the upgrade keep their routing. Do not use for new writes.
    public const string OrchestratorRoutingV1 = "studio/orchestrator-routing/v1";
    public const string RuntimeSettingsV1    = "studio/runtime-settings/v1";
    public const string ExecutionResultV1    = "studio/execution-result/v1";
    public const string GoalV1               = "studio/goal/v1";
    public const string DecisionV1           = "studio/decision/v1";
    public const string EvidenceV1           = "studio/evidence/v1";
    public const string TrajectoryV1         = "studio/trajectory/v1";
    public const string HypothesisV1         = "studio/hypothesis/v1";
    public const string ReasoningCommitV1    = "studio/reasoning-commit/v1";
    public const string ReviewTimerV1        = "studio/review-timer/v1";
    public const string ExperimentV1         = "studio/experiment/v1";
    public const string SteeringDecisionV1   = "studio/steering-decision/v1";
    public const string FindingV1            = "studio/finding/v1";
    public const string ConversationLogV1    = "studio/conversation-log/v1";
    public const string FileLeaseV1          = "studio/file-lease/v1";
    public const string RepositorySyncStateV1 = "studio/repository-sync-state/v1";
    public const string RepositoryV1          = "studio/repository/v1";
    public const string ProjectionSnapshotV1  = "studio/projection-snapshot/v1";
    public const string WorkspaceV1            = "studio/workspace/v1";
    public const string RepositoryOpV1         = "studio/repository-op/v1";
    public const string RepositorySnapshotV1   = "studio/repository-snapshot/v1";
    public const string RepositoryConflictV1   = "studio/repository-conflict/v1";
    public const string CoModPatternV1         = "studio/comod-pattern/v1";
    public const string BlobIndexEntryV1       = "studio/blob-index-entry/v1";
    public const string CandidateConflictV1    = "studio/candidate-conflict/v1";
    public const string TaskConflictV1         = "studio/task-conflict/v1";
    // Phase 5 slice 5.2 — local blob-GC run ledger (BlobGcRunRecord), one row per run, keyed by a
    // generated RunId (never reused, so unlike most kinds there is no "latest per entityId"
    // collapse to worry about). Safe under the STUDIO_ROOM_SCHEMA.md (a) longest-match parsing
    // rule: "studio/gc-run/v1/" is not a prefix of any other kind above once suffixed with "/",
    // nor is any other kind a prefix of it (verified by inspection of the full list above).
    public const string GcRunV1                = "studio/gc-run/v1";
}

public interface IStudioNodeStore
{
    Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default);

    Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default);

    // Slice 0a — rehydration. Returns the latest payload per entityId for every node of this
    // kind, so a service can rebuild its in-memory dictionary on startup from what was already
    // durably written via WriteNodeAsync.
    Task<IReadOnlyList<(string EntityId, string PayloadJson)>> ReadAllNodesAsync(
        string kind, CancellationToken cancellationToken = default);
}

public sealed class InMemoryStudioNodeStore : IStudioNodeStore
{
    private readonly ConcurrentDictionary<(string Kind, string EntityId), string> _nodes = new();

    public Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default)
    {
        _nodes[(kind, entityId)] = payloadJson;
        return Task.CompletedTask;
    }

    public Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default)
    {
        _nodes.TryGetValue((kind, entityId), out var payload);
        return Task.FromResult(payload);
    }

    public Task<IReadOnlyList<(string EntityId, string PayloadJson)>> ReadAllNodesAsync(
        string kind, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(string EntityId, string PayloadJson)> results = _nodes
            .Where(n => n.Key.Kind == kind)
            .Select(n => (n.Key.EntityId, n.Value))
            .ToList();
        return Task.FromResult(results);
    }
}
