using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

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
//
// Slice 6.3 — per the slice's own "what NOT to do" boundary, this class is NOT redesigned: it
// still does exactly one thing (promote a room's latest checkpoint + notify outbound). What
// changed is WHICH room: repositoryId resolves via the same BoundRepoRooms helper
// NodalMergeStudioNodeStore's write path uses, so a repo-scoped WorkUnit's completion safety net
// promotes that repo's own room instead of unconditionally promoting "studio" — otherwise the
// safety net would promote the wrong room's checkpoint (or a room the write never touched at all)
// once WorkUnitV1 writes started routing to repo/{repoId} rooms.
internal sealed class RuntimeGraphPromoter(
    IRuntimeCommandBridge bridge,
    IStudioReplicationOutbound replicationOutbound,
    ILogger<RuntimeGraphPromoter> logger,
    IRepositoryRegistryService? repositoryRegistry = null
) : IStudioGraphPromoter
{
    private const string StudioRoomId = "studio";

    public async Task TryPromoteStudioCheckpointAsync(string? repositoryId = null)
    {
        var roomId = await BoundRepoRooms.TryResolveRepoRoomIdAsync(repositoryRegistry, repositoryId, CancellationToken.None)
            .ConfigureAwait(false) ?? StudioRoomId;

        try
        {
            var promoteCommand = JsonSerializer.Serialize(new
            {
                room_id = roomId,
                command = new
                {
                    PromoteCheckpointToGraph = new
                    {
                        selector = new { selector = "latest" }
                    }
                }
            });

            var response = bridge.ProcessJsonCommand(promoteCommand);
            if (response.Status != AsStatus.Ok)
                return;

            var nodeIdHex = TryExtractPromotedNodeIdHex(response.EventsJson);
            if (string.IsNullOrWhiteSpace(nodeIdHex))
                return;

            await replicationOutbound.NotifyLocalWriteAsync(roomId, nodeIdHex, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "studio graph promotion failed room={Room}; checkpoint not materialized this cycle", roomId);
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
