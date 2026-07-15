using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

// Slice 6.1b: this class used to own its own RequestServerPack + RuntimeRoomBroker.BroadcastAsync
// call (the 30 s StudioCrdtSyncBackgroundService promoter's broadcast half, now retired). It now
// routes through the same IStudioReplicationOutbound seam NodalMergeStudioNodeStore's per-write
// path uses, so there is exactly one place that knows how to turn a promoted node id into an
// outbound pack (RoomPeerClient) instead of two. What remains here is purely the promote call
// itself — still needed for InMemoryWorkUnitService's WorkUnit-completion-boundary safety net
// (see IStudioGraphPromoter's own doc comment); it is frequently a no-op race against
// NodalMergeStudioNodeStore's own per-write promote (PromoteCheckpointToGraph is idempotent per
// checkpoint identity), and when it isn't, the resulting node still needs to reach replication.
internal sealed class RuntimeGraphPromoter(
    IRuntimeCommandBridge bridge,
    IStudioReplicationOutbound replicationOutbound,
    ILogger<RuntimeGraphPromoter> logger
) : IStudioGraphPromoter
{
    private const string StudioRoomId = "studio";

    private static readonly string PromoteCommand = JsonSerializer.Serialize(new
    {
        room_id = StudioRoomId,
        command = new
        {
            PromoteCheckpointToGraph = new
            {
                selector = new { selector = "latest" }
            }
        }
    });

    public async Task TryPromoteStudioCheckpointAsync()
    {
        try
        {
            var response = bridge.ProcessJsonCommand(PromoteCommand);
            if (response.Status != AsStatus.Ok)
                return;

            var nodeIdHex = TryExtractPromotedNodeIdHex(response.EventsJson);
            if (string.IsNullOrWhiteSpace(nodeIdHex))
                return;

            await replicationOutbound.NotifyLocalWriteAsync(StudioRoomId, nodeIdHex, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "studio graph promotion failed; checkpoint not materialized this cycle");
        }
    }

    private static string? TryExtractPromotedNodeIdHex(string eventsJson)
    {
        using var eventsDoc = JsonDocument.Parse(eventsJson);
        if (eventsDoc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var evt in eventsDoc.RootElement.EnumerateArray())
        {
            if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("CheckpointPromoted", out var promoted))
                continue;
            if (promoted.TryGetProperty("node_id_hex", out var nodeIdEl) && nodeIdEl.ValueKind == JsonValueKind.String)
                return nodeIdEl.GetString();
        }

        return null;
    }
}
