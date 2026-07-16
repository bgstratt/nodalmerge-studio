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

    // Slice 6.3 (plans/cas-distribution-and-storage.md Phase 6, D1) — the kinds whose payload
    // record carries a direct, non-derived `RepositoryId` field, verified by inspection of every
    // StudioNodeKind's backing C# record (see the slice's own research pass, summarized here so the
    // finding survives as more than a commit message): WorkUnit.RepositoryId (nullable),
    // RepositorySnapshot.RepositoryId, RepositoryOperation.RepositoryId, RepositoryConflict.RepositoryId,
    // CoModificationPattern.RepositoryId. These are the kinds NodalMergeStudioNodeStore actually
    // routes to a per-repo engine room (repo/{repositoryId}) via the WriteNodeAsync(..., repositoryId,
    // ...) overload below; every other kind stays in the workspace-local "studio" room.
    //
    // Two families were deliberately NOT included despite being conceptually repo-scoped, and are
    // flagged here rather than silently guessed at (decide from the data, per the slice brief):
    //   1. REPO-SCOPED-INDIRECT (~25 of the remaining kinds: TaskV1, MergeProposalV1, BranchV1,
    //      KnownGoodStateV1, GoalV1, DecisionV1, ArtifactRefV1, ... ) carry no RepositoryId field at
    //      all — only a WorkUnitId/BranchId/ParentWorkUnitId foreign key, and WorkUnit.RepositoryId
    //      itself is nullable (null for forked/child work units, which inherit repo identity via
    //      their parent chain instead). Resolving these correctly means either a live WorkUnit
    //      lookup at every one of ~40 write call sites (risking the exact IStudioNodeStore ->
    //      IWorkUnitService -> IStudioNodeStore DI cycle NodalMergeStudioNodeStore already has to
    //      dodge for IRepositoryRegistryService) or a parent-chain walk to find the nearest
    //      non-null RepositoryId. Both are real design decisions, not a mechanical wire-through —
    //      left for a follow-up slice (6.4/6.5) rather than rushed here. These kinds remain in
    //      "studio" for 6.3; BranchV1 in particular has NO stored field of any kind linking it to a
    //      repo or work unit (it's a bare {id, parentId, createdAt} — see NodalMergeBranchService).
    //   2. Genuinely GLOBAL/cross-repo kinds: BlobIndexEntryV1 and WorkspaceV1 carry `RepositoryIds`
    //      (plural) by design — a blob can be referenced by multiple repos (global CAS dedup, D5),
    //      and the workspace aggregate owns every repo. RepositorySyncStateV1 is keyed by
    //      `RepositoryPath` (a legacy pre-6.2 identity concept), not `RepositoryId` — flagged as the
    //      odd one out, not silently reinterpreted. RepositoryV1 itself (the local-candidate
    //      registry entry) stays local by necessity: it's the record that MAPS a local candidate to
    //      a repo room, so it can't live inside the room it names.
    public static readonly IReadOnlyCollection<string> RepoScopedKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        WorkUnitV1,
        RepositorySnapshotV1,
        RepositoryOpV1,
        RepositoryConflictV1,
        CoModPatternV1,
    };
}

public interface IStudioNodeStore
{
    Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default);

    // Slice 6.3 — explicit repo-room routing overload (design option (a): explicit over ambient).
    // Repo-scoped callers (StudioNodeKind.RepoScopedKinds) pass the entity's own RepositoryId
    // (typically already in scope as a field on the object being serialized — e.g. `updated.RepositoryId`)
    // so the store can write into repo/{repositoryId}'s bound engine room instead of the
    // workspace-local "studio" room. `repositoryId` is the *local-candidate* RepositoryId (the id
    // carried on WorkUnit/RepositorySnapshot/etc. payloads today) — NodalMergeStudioNodeStore
    // resolves it to the workgroup-bound room via the registry's WorkgroupRepoId (6.2) internally;
    // callers never need to know the distinction.
    //
    // Default implementation ignores repositoryId and falls back to the 3-arg overload — this keeps
    // InMemoryStudioNodeStore (and any other pre-6.3 IStudioNodeStore implementation/test double)
    // compiling and behaviorally unchanged with zero edits; only NodalMergeStudioNodeStore overrides
    // it with real per-repo-room writes.
    Task WriteNodeAsync(string kind, string entityId, string payloadJson, string? repositoryId, CancellationToken cancellationToken = default) =>
        WriteNodeAsync(kind, entityId, payloadJson, cancellationToken);

    Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default);

    // Slice 0a — rehydration. Returns the latest payload per entityId for every node of this
    // kind, so a service can rebuild its in-memory dictionary on startup from what was already
    // durably written via WriteNodeAsync.
    //
    // Slice 6.3 — for a kind in StudioNodeKind.RepoScopedKinds, an IStudioNodeStore implementation
    // MUST aggregate across every repo room this peer has bound (see BoundRepoRooms), not just the
    // local "studio" room — callers of this method (rehydration dictionaries, migration, etc.) never
    // need to know which room a given entity actually lives in.
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
