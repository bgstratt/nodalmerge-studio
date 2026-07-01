using System.Text.Json;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

/// <summary>
/// Aggregates in-process agent loops and connected WebSocket room peers into a single
/// participant list. Lives in Host because it needs both the Studio service layer and
/// the nodalmerge-host RuntimeRoomBroker.
/// </summary>
internal sealed class StudioParticipantService(
    IAgentControlService agentControl,
    RuntimeRoomBroker roomBroker) : IStudioParticipantService
{
    private const string StudioRoomId = "studio";

    public async Task<IReadOnlyList<ParticipantDto>> ListAsync(CancellationToken ct = default)
    {
        var result = new List<ParticipantDto>();

        var agents = await agentControl.ListAllAsync(ct).ConfigureAwait(false);
        foreach (var a in agents)
        {
            result.Add(new ParticipantDto(
                Id: a.AgentId,
                Kind: "agent",
                Status: a.Status,
                WorkUnitId: a.WorkUnitId,
                CurrentActivity: a.CurrentActivity,
                PeerType: null));
        }

        foreach (var (peerId, peerType, _) in roomBroker.GetConnectedPeers(StudioRoomId))
        {
            result.Add(new ParticipantDto(
                Id: peerId,
                Kind: "peer",
                Status: "connected",
                WorkUnitId: null,
                CurrentActivity: null,
                PeerType: peerType));
        }

        return result;
    }

    public async Task StopAsync(string id, CancellationToken ct = default)
    {
        // Try in-process agent first.
        try
        {
            await agentControl.StopAsync(id, ct).ConfigureAwait(false);
            return;
        }
        catch (KeyNotFoundException) { }

        // Try room peer — broadcast a targeted stop signal.
        var stopMsg = JsonSerializer.Serialize(new { type = "participant.stop", peer_id = id });
        await roomBroker.BroadcastAsync(
            StudioRoomId,
            stopMsg,
            targetPeerId: id,
            cancellationToken: ct).ConfigureAwait(false);
    }
}
