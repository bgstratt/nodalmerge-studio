using System.Text.Json;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Host;

// Slice 12c — the embedded NodalMerge runtime already exposes a working /ws/runtime room
// broadcaster (RuntimeRoomBroker, registered by HostApplication.Build); this just gives Studio
// services a way to push a message into it without depending on NodalMerge.DotNetHost directly.
// "studio-main" is the same hardcoded room id the extension's existing DAG Replay WebSocket
// client already connects to (clients/vscode-extension/src/panels/DagReplayPanel.ts) — there's
// only ever one Studio Host process per extension session, so one shared room is sufficient.
public sealed class RuntimeRoomEventBroadcaster(RuntimeRoomBroker roomBroker) : IRuntimeEventBroadcaster
{
    private const string RoomId = "studio-main";

    public Task BroadcastWorkUnitStageChangedAsync(
        string workUnitId,
        PipelineStage? stage,
        CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new
        {
            type = "work-unit-stage-changed",
            workUnitId,
            stage = stage?.ToString(),
        });
        return roomBroker.BroadcastAsync(RoomId, json, cancellationToken: cancellationToken);
    }
}
