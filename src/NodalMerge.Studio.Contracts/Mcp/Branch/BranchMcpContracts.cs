namespace NodalMerge.Studio.Contracts.Mcp.Branch;

public sealed record BranchCreateRequest(
    string Name,
    string? FromBranch = null);

public sealed record BranchCheckoutRequest(string BranchId);

public sealed record BranchListRequest(string? ParentBranch = null);

public sealed record BranchStatusRequest(string BranchId);

public sealed record BranchCreateResponse(string BranchId);

public sealed record BranchStatusResponse(
    string BranchId,
    string Status,
    int PendingChangeCount,
    string? HeadCheckpoint = null);
