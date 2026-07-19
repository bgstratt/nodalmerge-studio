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
    string? PauseReason = null,
    // Denormalized from the goal's top-level work unit at creation (plans/repo-identity-convergence.md
    // "#1 — repo-scope goals"), so GoalV1 can route to and replicate through its repo room like the
    // other repo-scoped kinds (StudioNodeStore.RepoScopedKinds) — the mechanism that makes a goal
    // visible to another peer on the same repo. Null for a goal with no resolvable repository (the
    // node then stays in the peer-local "studio" room, the pre-#1 behavior).
    string? RepositoryId = null,
    // Materialization anchors (plans/first-class-goals-and-materialization.md Phase 4). BaseSnapshotId
    // = the repo's pre-work state (the root work unit's SeedSnapshotId), denormalized at creation so a
    // peer can materialize "what this goal started from" without walking the work unit. FinalSnapshotId
    // = the integrated result, stamped when the goal's winning proposal is applied (the merge's
    // appliedSnapshotId). Both are RepositorySnapshot ids feedable straight to
    // POST /studio/repository-snapshots/{id}/materialize. Null until known.
    string? BaseSnapshotId = null,
    string? FinalSnapshotId = null)
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