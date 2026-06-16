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

public static class ProjectionCatalog
{
    public static IReadOnlyList<string> Types { get; } =
        Enum.GetNames<ProjectionType>();

    public static IReadOnlyList<string> Levels { get; } =
        Enum.GetNames<ProjectionLevel>();
}
