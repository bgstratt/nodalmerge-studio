namespace NodalMerge.Studio.Contracts.Mcp.Snapshot;

public sealed record SnapshotGetRequest(
    string AgentId,
    string? WorkUnitId = null,
    string? BranchId = null);

public sealed record SnapshotGetResponse(
    string AgentId,
    string? CurrentGoal,
    string? CurrentTask,
    int FailureCount,
    int RollbackCount,
    string? NextSuggestedAction);

public sealed record SnapshotCompareRequest(
    string AgentId,
    string OtherAgentId,
    string? WorkUnitId = null);

public sealed record SnapshotCompareResponse(
    IReadOnlyList<string> Differences);
