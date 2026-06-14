namespace NodalMerge.Studio.Contracts.Mcp.Projection;

public sealed record ProjectionGetRequest(
    string ProjectionType,
    string ProjectionLevel = "Normal",
    string? WorkUnitId = null,
    string? BranchId = null,
    string? AgentId = null);

public sealed record ProjectionGetResponse(
    string ProjectionType,
    string Level,
    object Data);

public sealed record ProjectionListResponse(
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Levels);
