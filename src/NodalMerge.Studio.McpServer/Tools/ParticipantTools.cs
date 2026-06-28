using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ParticipantTools(IStudioParticipantService participants)
{
    [McpServerTool(Name = McpToolNames.ParticipantList),
     Description("List all current runtime participants — in-process agent loops and connected WebSocket peers. " +
                 "Use this to see who is active, what work unit each agent is processing, and their current activity.")]
    public async Task<string> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = await participants.ListAsync(cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { participants = list, count = list.Count });
    }

    [McpServerTool(Name = McpToolNames.ParticipantStop),
     Description("Stop a specific runtime participant by ID. " +
                 "For in-process agent loops this cancels the running task; " +
                 "for connected peers this sends a stop signal over the room channel.")]
    public async Task<string> StopAsync(
        [Description("ID of the participant to stop (agentId for agents, peerId for room peers)")]
        string participantId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await participants.StopAsync(participantId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { participantId, stopped = true });
        }
        catch (KeyNotFoundException)
        {
            return McpJson.Error(McpToolNames.ParticipantStop, $"Participant '{participantId}' not found.");
        }
    }
}
