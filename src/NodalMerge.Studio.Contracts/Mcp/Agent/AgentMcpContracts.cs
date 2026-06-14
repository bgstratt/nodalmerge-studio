namespace NodalMerge.Studio.Contracts.Mcp.Agent;

public sealed record AgentSpawnRequest(
    string AgentType,
    string WorkUnitId,
    string? BranchId = null);

public sealed record AgentControlRequest(string AgentId);

public sealed record AgentSpawnResponse(string AgentId);

public sealed record AgentStatusResponse(
    string AgentId,
    string Status,
    string? WorkUnitId = null);
