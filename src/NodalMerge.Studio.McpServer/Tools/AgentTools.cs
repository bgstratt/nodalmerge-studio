using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class AgentTools(IAgentControlService agents, IWorkUnitService workUnits)
{
    [McpServerTool(Name = McpToolNames.AgentSpawn), Description("Spawn an agent for a work unit.")]
    public async Task<string> SpawnAsync(
        string agentType,
        string workUnitId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? profileId = null,
        string? autoReviewProfileId = null,
        string[]? enabledDomainAgents = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var wu = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            if (wu is null)
                return McpJson.Error(McpToolNames.AgentSpawn, $"Work unit '{workUnitId}' not found.");

            var agentId = await agents.SpawnAsync(
                agentType, workUnitId, taskId, model, baseUrl, apiKey, provider, profileId,
                autoReviewProfileId, enabledDomainAgents: enabledDomainAgents,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { agentId, agentType, workUnitId, branchId = wu.BranchId });
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpToolNames.AgentSpawn, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.AgentPause), Description("Pause an agent.")]
    public async Task<string> PauseAsync(string agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            await agents.PauseAsync(agentId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { agentId, status = "paused" });
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpToolNames.AgentPause, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.AgentResume), Description("Resume a paused agent.")]
    public async Task<string> ResumeAsync(string agentId, CancellationToken cancellationToken = default)
    {
        try
        {
            await agents.ResumeAsync(agentId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { agentId, status = "active" });
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpToolNames.AgentResume, ex.Message);
        }
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
        try
        {
            await agents.StopAsync(agentId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { agentId, status = "stopped" });
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpToolNames.AgentStop, ex.Message);
        }
    }
}
