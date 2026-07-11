namespace NodalMerge.Studio.Contracts.Mcp.Replay;

public sealed record ReplayRollbackRequest(
    string BranchId,
    string KnownGoodStateId);

public sealed record ReplayInspectRequest(
    string BranchId,
    string? NodeId = null);

public sealed record ReplayInspectResponse(
    string BranchId,
    string Summary,
    IReadOnlyList<string> Highlights);
