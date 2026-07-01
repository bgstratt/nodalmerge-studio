namespace NodalMerge.Studio.Contracts.Mcp.Workspace;

public sealed record WorkspaceSummaryRequest(string? BranchId = null);

public sealed record WorkspaceSummaryResponse(
    IReadOnlyList<string> ActiveWorkUnits,
    IReadOnlyList<string> ActiveAgents,
    IReadOnlyList<string> PendingMerges,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> KnownGoodStates);

public sealed record WorkspaceStatusRequest(
    string? BranchId = null,
    string? WorkUnitId = null,
    int Limit = 50,
    int Offset = 0);

public sealed record WorkspaceStatusResponse(
    string? BranchId,
    string? WorkUnitId,
    string? CurrentWorkUnitStatus,
    IReadOnlyList<object> ChangedFiles,
    IReadOnlyList<object> ProposalSummaries,
    object? DiffStats,
    bool Truncated,
    int Limit,
    int Offset,
    int NextOffset,
    DateTimeOffset GeneratedAt);
