namespace NodalMerge.Studio.Contracts.Mcp.WorkUnit;

public sealed record WorkUnitCreateRequest(
    string Goal,
    string BranchId,
    string? Owner = null,
    string? SuccessCriteria = null);

public sealed record WorkUnitGetRequest(string WorkUnitId);

public sealed record WorkUnitUpdateRequest(
    string WorkUnitId,
    string? Status = null,
    string? AssignedAgent = null,
    string? SuccessCriteria = null);

public sealed record WorkUnitListRequest(
    string? BranchId = null,
    string? Status = null);

public sealed record WorkUnitResponse(string WorkUnitId);

public sealed record WorkUnitListResponse(IReadOnlyList<string> WorkUnitIds);
