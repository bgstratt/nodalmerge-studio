namespace NodalMerge.Studio.Contracts.Mcp.Workspace;

public sealed record WorkspaceSummaryRequest(string? BranchId = null);

public sealed record WorkspaceSummaryResponse(
    IReadOnlyList<string> ActiveWorkUnits,
    IReadOnlyList<string> ActiveAgents,
    IReadOnlyList<string> PendingMerges,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> KnownGoodStates);
