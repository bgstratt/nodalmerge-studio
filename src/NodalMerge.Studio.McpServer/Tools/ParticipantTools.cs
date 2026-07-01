using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class ParticipantTools(IStudioParticipantService participants, IParticipantEventBus eventBus)
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

    [McpServerTool(Name = McpToolNames.ParticipantEvents),
     Description("Get recent domain events scoped to a specific participant's work unit. " +
                 "Returns the latest events (default 50) filtered to the participant's workUnitId. " +
                 "Useful for an agent to inspect what has happened in its own activity stream.")]
    public async Task<string> GetEventsAsync(
        [Description("ID of the participant whose events to retrieve")] string participantId,
        [Description("Maximum number of events to return (default 50)")] int limit = 50,
        CancellationToken cancellationToken = default)
    {
        var all = (await participants.ListAsync(cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(p => p.Id == participantId);
        if (all is null)
            return McpJson.Error(McpToolNames.ParticipantEvents, $"Participant '{participantId}' not found.");

        var events = eventBus.GetRecentEvents(200)
            .Where(e => all.WorkUnitId is not null && e.WorkUnitId == all.WorkUnitId)
            .TakeLast(limit)
            .ToList();

        return McpJson.Ok(new { participantId, events, count = events.Count });
    }

    [McpServerTool(Name = McpToolNames.EventTypes),
     Description("List all known domain event types emitted by the runtime event bus. " +
                 "Use this to understand what event types you can subscribe to or observe.")]
    public Task<string> GetEventTypesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(McpJson.Ok(new { eventTypes = eventBus.GetRegisteredEventTypes() }));
}
