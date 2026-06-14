using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class AgentTools(IAgentControlService agents)
{
    [McpServerTool(Name = McpToolNames.AgentSpawn), Description("Spawn an agent for a work unit.")]
    public async Task<string> SpawnAsync(
        string agentType,
        string workUnitId,
        string? branchId = null,
        CancellationToken cancellationToken = default)
    {
        var agentId = await agents.SpawnAsync(agentType, workUnitId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { agentId, agentType, workUnitId, branchId });
    }

    [McpServerTool(Name = McpToolNames.AgentPause), Description("Pause an agent.")]
    public async Task<string> PauseAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await agents.PauseAsync(agentId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { agentId, status = "paused" });
    }

    [McpServerTool(Name = McpToolNames.AgentResume), Description("Resume a paused agent.")]
    public async Task<string> ResumeAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await agents.ResumeAsync(agentId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { agentId, status = "active" });
    }

    [McpServerTool(Name = McpToolNames.AgentStatus), Description("Get agent status.")]
    public async Task<string> StatusAsync(string agentId, CancellationToken cancellationToken = default)
    {
        var status = await agents.GetStatusAsync(agentId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { agentId, status });
    }

    [McpServerTool(Name = McpToolNames.AgentStop), Description("Stop an agent.")]
    public async Task<string> StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        await agents.StopAsync(agentId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { agentId, status = "stopped" });
    }
}
