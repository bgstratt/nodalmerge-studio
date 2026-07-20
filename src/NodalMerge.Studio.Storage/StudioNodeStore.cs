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
    // Phase 3 (plans/organizational-knowledge-and-workgroup-scope.md) — a peer-LOCAL suppression of a
    // constraint by ArtifactId. Deliberately NOT in RepoScopedKinds/WorkgroupScopedKinds: it lives in
    // the peer-private "studio" room so turning a shared constraint off affects only this peer.
    public const string ConstraintToggleV1   = "studio/constraint-toggle/v1";
    public const string ConversationLogV1    = "studio/conversation-log/v1";
    // L2.3 (plans/room-persistence-bloat.md) — repo-scoped reference to a peer-visible reasoning
    // transcript. The bounded transcript body lives in the CAS (pulled on demand); this node carries
    // only ids + the blob hash + a heuristic label, so it replicates cheaply. This is what makes
    // another peer's agentic reasoning legible on the same repo without the ConversationLogV1 firehose.
    public const string ConversationRefV1    = "studio/conversation-ref/v1";
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
    // Slice 6.3a — the "repo-scoped-indirect" family 6.3 deliberately deferred (see that slice's own
    // finding, preserved below) is now ALSO in this set: MergeProposalV1, TaskV1, BranchV1,
    // KnownGoodStateV1, DecisionV1, ArtifactRefV1, CandidateConflictV1, TaskConflictV1. Each of
    // these records now carries its OWN `RepositoryId` field too (added by 6.3a, denormalized at
    // creation time from the owning WorkUnit's already-resolved RepositoryId — see
    // RepositoryIdResolution and each record's own field comment), so — per 6.3's own recommendation
    // — no live WorkUnit lookup or parent-chain walk is needed at read/route time; every kind in
    // this set now satisfies the exact same "direct field" contract uniformly, and
    // NodalMergeStudioNodeStore's routing/aggregation/migration code (all written generically
    // against this set + a "RepositoryId" JSON property name) needed ZERO changes to cover them.
    // BranchV1 in particular had NO stored field of any kind before 6.3a (a bare
    // {id, parentId, createdAt} — see NodalMergeBranchService) and gained one for this slice.
    //
    // Deliberately left OUT of this set (still peer-local, "studio" room), with reasons:
    //   - GoalV1, goal routing (GoalRoutingV1/OrchestratorRoutingV1), RuntimeSettingsV1,
    //     WorkspaceV1, SchedulerV1, AgentWorkspaceV1: workgroup/global by design (D1) — a goal can
    //     span multiple repos (D3), so it is not single-repo-scoped at all.
    //   - ExecutionSessionV1, ExecutionEventV1, ExecutionResultV1, CommandResultV1: hang off a
    //     work unit/session but are by far the highest-volume kinds in practice (one row per
    //     execution event — see the 2026-07-15 baseline note: "bulk of node-store growth... is
    //     tasks/messages/history", i.e. exactly these). 5.3's retention classification (Pinned/
    //     Active/Intermediate, 5.1) is computed entirely from snapshots/work-units/branches/
    //     proposals — it never needs to see these to do its job — and Phase 5's own "Out of scope"
    //     section already flags non-CAS node-store retention for this family as a *separate*,
    //     replication-plane-compaction problem, not this slice's. Routing them now would multiply
    //     per-repo-room replication traffic for no 5.3 benefit; revisit only if/when that
    //     compaction question is designed.
    //   - GcRunV1: not per-repo (today's GC run ledger is workspace-wide); out of this slice's
    //     named target set.
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
        // Slice 6.3a additions:
        MergeProposalV1,
        TaskV1,
        BranchV1,
        KnownGoodStateV1,
        DecisionV1,
        ArtifactRefV1,
        CandidateConflictV1,
        TaskConflictV1,
        // #1 goal replication (plans/repo-identity-convergence.md): a top-level goal is denormalized
        // with its work unit's RepositoryId (GoalNode.RepositoryId) and routed to that repo's room so
        // it replicates to peers on the same repo. This deliberately revises the "GoalV1 is
        // workgroup/global, not single-repo-scoped" note above: a single-repo goal is repo-scoped;
        // genuinely cross-repo goal fan-out (D3) is a later layer that can override this per-goal.
        GoalV1,
        // L2.3 (plans/room-persistence-bloat.md) — the reasoning-transcript reference replicates so a
        // same-repo peer can trace the "why" behind a decision; the transcript BYTES ride the CAS
        // content plane (pulled on demand), only this small ref is on the replication plane.
        ConversationRefV1,
        // Phase 0 (plans/organizational-knowledge-and-workgroup-scope.md) — findings are low-volume
        // human-review candidates (like DecisionV1), so the Insights Findings queue is shared among
        // peers on the same repo. Attributed to the workspace's repo at ProposeAsync; a null
        // RepositoryId (no seed repo) falls back to the local "studio" room. The manual Export/Import
        // JSON path stays as the deliberate *cross*-workgroup sharing escape hatch.
        FindingV1,
    };

    // Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — kinds that can carry
    // Workgroup reach with no RepositoryId, i.e. live in the shared "workgroup" room (every member,
    // all repos). The read fan-out consults that room for these kinds in addition to studio + repo
    // rooms. Today only ArtifactRefV1 (a Workgroup-reach global constraint); the write path routes
    // there via WriteWorkgroupNodeAsync, driven by ArtifactRef.Reach.
    public static readonly IReadOnlyCollection<string> WorkgroupScopedKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        ArtifactRefV1,
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

    // Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — write a node into the shared
    // "workgroup" room (every member, all repos): the home for a Workgroup-reach artifact with no
    // RepositoryId. Rides the same EngineRoomMap bridging every other room uses, in a separate
    // namespace from the workgroup repositories/goals directories. Default falls back to the 3-arg
    // (local) overload so InMemoryStudioNodeStore and other doubles are unchanged; only
    // NodalMergeStudioNodeStore overrides it with a real workgroup-room write.
    Task WriteWorkgroupNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default) =>
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

    // Integration-checkpoint model (plans/room-snapshot-checkpoint-redesign.md): mints a single
    // full-room snapshot ("last known good") for the repo room bound to `repositoryId`, taken only
    // at integration points (merge to main / goal completion). Per-write durability now rides
    // incremental deltas, so this is the only place — besides the disconnect/shutdown flush — a
    // full-room snapshot is minted; it bounds the delta chain replayed on the next hydrate and lines
    // up with git-commit/PR boundaries. No-op if `repositoryId` doesn't resolve to a bound repo room.
    // Default no-op so InMemoryStudioNodeStore and other test doubles are unaffected — only
    // NodalMergeStudioNodeStore overrides it with a real per-repo-room snapshot.
    Task CheckpointRepositoryRoomAsync(string repositoryId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    // plans/vision-punchlist-remediation.md (Items 1+2) — per-kind node counts inside ONE repo room,
    // addressed by its workgroup repo id (i.e. the room is repo/{workgroupRepoId}). Re-linking a
    // repository re-points future reads and writes at a different room, and nothing moves the content
    // already written to the old one — it simply drops out of the read fan-out. This is what lets the
    // re-link flow tell the user what will stop appearing BEFORE they commit, instead of silently
    // orphaning it. Default returns empty so test doubles are unaffected; only
    // NodalMergeStudioNodeStore enumerates a real room.
    Task<IReadOnlyDictionary<string, int>> CountRepoRoomNodesByKindAsync(
        string workgroupRepoId, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyDictionary<string, int>>(
            new Dictionary<string, int>(StringComparer.Ordinal));
}

// Implements IStudioLocalLogStore as well as IStudioNodeStore over the same (kind, id) map so the
// many tests that construct services directly with `new InMemoryStudioNodeStore()` — or share one
// store across several services — keep compiling after the four high-volume services switched their
// dependency to IStudioLocalLogStore (L2.1). Production keeps the two stores separate
// (NodalMergeStudioNodeStore for the room, FileStudioLocalLogStore for the local log).
public sealed class InMemoryStudioNodeStore : IStudioNodeStore, IStudioLocalLogStore
{
    private readonly ConcurrentDictionary<(string Kind, string EntityId), string> _nodes = new();
    private readonly ConcurrentDictionary<(string Kind, string EntityId), DateTimeOffset> _timestamps = new();

    public Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default)
    {
        _nodes[(kind, entityId)] = payloadJson;
        return Task.CompletedTask;
    }

    // ── IStudioLocalLogStore ─────────────────────────────────────────────────
    public Task AppendAsync(
        string kind, string id, string payloadJson, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        _nodes[(kind, id)] = payloadJson;
        _timestamps[(kind, id)] = occurredAt;
        return Task.CompletedTask;
    }

    public Task<string?> GetAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        _nodes.TryGetValue((kind, id), out var payload);
        return Task.FromResult(payload);
    }

    public Task<IReadOnlyList<(string Id, string PayloadJson)>> ReadAllAsync(
        string kind, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(string Id, string PayloadJson)> results = _nodes
            .Where(n => n.Key.Kind == kind)
            .Select(n => (n.Key.EntityId, n.Value))
            .ToList();
        return Task.FromResult(results);
    }

    public Task<int> PruneOlderThanAsync(
        string kind, DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        var toRemove = _timestamps
            .Where(t => t.Key.Kind == kind && t.Value < olderThan)
            .Select(t => t.Key)
            .ToList();
        foreach (var key in toRemove)
        {
            _nodes.TryRemove(key, out _);
            _timestamps.TryRemove(key, out _);
        }
        return Task.FromResult(toRemove.Count);
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
