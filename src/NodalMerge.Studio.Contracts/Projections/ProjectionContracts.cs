using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Contracts.Projections;

/// <summary>
/// Frozen v1 projection type identifiers (MCP and Projection Manager).
/// </summary>
public enum ProjectionType
{
    WorkUnit,
    AuthoritativeState,
    Task,
    MergeProposal,
    ExecutionSnapshot,
    AgentWorkspace,
}

public enum ProjectionLevel
{
    Full,
    Normal,
    Compact,
    Emergency
}

public sealed record ProjectionRequest(
    ProjectionType Type,
    ProjectionLevel Level,
    string? WorkUnitId = null,
    string? BranchId = null,
    string? AgentId = null);

public sealed record ProjectionResult(
    ProjectionType Type,
    ProjectionLevel Level,
    string DataJson,
    DateTimeOffset GeneratedAt);

/// <summary>
/// WorkUnit projection payload shape at Normal level and above.
/// </summary>
public sealed record WorkUnitProjectionPayload(
    string WorkUnitId,
    string Goal,
    string BranchId,
    string Status,
    IReadOnlyList<string> ActiveTasks,
    IReadOnlyList<string> Dependencies,
    string? SuccessCriteria,
    IReadOnlyList<string> AssignedAgents);

public sealed record AuthoritativeStateProjectionPayload(
    string BranchId,
    IReadOnlyDictionary<string, string> MergedState);

public sealed record TaskProjectionPayload(
    IReadOnlyList<string> OpenTasks,
    IReadOnlyList<string> BlockedTasks,
    IReadOnlyList<string> CompletedTasks,
    IReadOnlyDictionary<string, string> Assignments);

public sealed record MergeProposalProjectionPayload(
    IReadOnlyList<string> PendingProposals,
    IReadOnlyDictionary<string, string> ReviewStatus,
    IReadOnlyList<string> VerificationResults);

public sealed record ExecutionSnapshotProjectionPayload(
    string AgentId,
    string? CurrentGoal,
    IReadOnlyList<string> FailureHistory,
    IReadOnlyList<string> RecoveryHints);

public sealed record ArtifactChain(
    IReadOnlyList<ArtifactRef> Artifacts);

public sealed record AgentWorkspaceProjectionPayload(
    string? AgentId,
    string? WorkUnitId,
    ArtifactChain Artifacts,
    IReadOnlyList<ArtifactRef> InheritedConstraints);

/// <summary>
/// What changed in a work unit's artifact chain since the last cycle. Lets an orchestrator
/// reason incrementally instead of re-reading the full projection every time, and lets the loop
/// detect stalls (no change for N consecutive cycles).
/// </summary>
public sealed record ProjectionDelta(
    string WorkUnitId,
    AgentWorkspaceProjectionPayload Previous,
    AgentWorkspaceProjectionPayload Current,
    IReadOnlyList<ArtifactRef> AddedArtifacts,
    IReadOnlyList<ArtifactRef> RemovedArtifacts,
    IReadOnlyList<ArtifactRef> StatusChangedArtifacts,
    IReadOnlyList<string> CompletedTaskIds,
    bool AnyChange)
{
    /// <summary>
    /// Diffs two AgentWorkspace snapshots on ArtifactId/Status. Artifacts are never deleted from
    /// the chain (it's append/replace lineage), so "removed" means a previously-active artifact
    /// transitioned to a terminal (non-Active) status, not that it disappeared.
    /// </summary>
    public static ProjectionDelta Compute(
        string workUnitId, AgentWorkspaceProjectionPayload previous, AgentWorkspaceProjectionPayload current)
    {
        var prevById = previous.Artifacts.Artifacts.ToDictionary(a => a.ArtifactId);
        var added = current.Artifacts.Artifacts.Where(a => !prevById.ContainsKey(a.ArtifactId)).ToList();
        var statusChanged = current.Artifacts.Artifacts
            .Where(a => prevById.TryGetValue(a.ArtifactId, out var p) && p.Status != a.Status)
            .ToList();
        var removed = statusChanged.Where(a => a.Status != ArtifactStatus.Active).ToList();
        var completedTaskIds = added.Concat(statusChanged)
            .Where(a => a.Type == ArtifactType.Task && a.Status == ArtifactStatus.Applied)
            .Select(a => a.ArtifactId)
            .ToList();

        return new ProjectionDelta(
            workUnitId, previous, current, added, removed, statusChanged, completedTaskIds,
            AnyChange: added.Count > 0 || removed.Count > 0 || statusChanged.Count > 0);
    }
}

public static class ProjectionCatalog
{
    public static IReadOnlyList<string> Types { get; } =
        Enum.GetNames<ProjectionType>();

    public static IReadOnlyList<string> Levels { get; } =
        Enum.GetNames<ProjectionLevel>();
}
