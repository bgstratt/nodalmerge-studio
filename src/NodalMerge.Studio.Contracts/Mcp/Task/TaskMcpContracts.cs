namespace NodalMerge.Studio.Contracts.Mcp.Task;

public sealed record TaskCreateRequest(
    string WorkUnitId,
    string Title,
    string Description,
    string? BranchId = null,
    int Priority = 0);

public sealed record TaskUpdateRequest(
    string TaskId,
    string? Status = null,
    string? Title = null,
    string? Description = null,
    int? Priority = null);

public sealed record TaskListRequest(
    string? WorkUnitId = null,
    string? BranchId = null);

public sealed record TaskAssignRequest(
    string TaskId,
    string AgentId,
    string? BranchId = null);

public sealed record TaskResponse(string TaskId);
