using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

internal sealed class RuntimeGraphPromoter(
    IRuntimeCommandBridge bridge,
    RuntimeRoomBroker roomBroker,
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

    private static readonly string RequestPackCommand = JsonSerializer.Serialize(new
    {
        room_id = StudioRoomId,
        command = new
        {
            RequestServerPack = new
            {
                known_ids = Array.Empty<string>()
            }
        }
    });

    public async Task TryPromoteStudioCheckpointAsync()
    {
        try
        {
            bridge.ProcessJsonCommand(PromoteCommand);
            await BroadcastStudioPackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "studio graph promotion failed; checkpoint not materialized this cycle");
        }
    }

    internal async Task BroadcastStudioPackAsync(CancellationToken cancellationToken)
    {
        try
        {
            var response = bridge.ProcessJsonCommand(RequestPackCommand);
            if (response.Status != AsStatus.Ok)
                return;

            using var eventsDoc = JsonDocument.Parse(response.EventsJson);
            if (eventsDoc.RootElement.ValueKind != JsonValueKind.Array)
                return;

            foreach (var evt in eventsDoc.RootElement.EnumerateArray())
            {
                if (!evt.TryGetProperty("ServerPackPrepared", out var serverPack))
                    continue;
                if (!serverPack.TryGetProperty("nodes_b64", out var nodesB64Node))
                    continue;
                var nodesB64 = nodesB64Node.GetString();
                if (string.IsNullOrWhiteSpace(nodesB64))
                    continue;

                var packJson = JsonSerializer.Serialize(new
                {
                    type = "pack",
                    room = StudioRoomId,
                    from = "server",
                    nodes = nodesB64
                });

                await roomBroker.BroadcastAsync(StudioRoomId, packJson, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "studio pack broadcast failed");
        }
    }
}
