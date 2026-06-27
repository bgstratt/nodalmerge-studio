using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Studio.Core.Services;
using System.Text.Json;

namespace NodalMerge.Studio.Host;

internal sealed class RuntimeGraphPromoter(
    IRuntimeCommandBridge bridge,
    ILogger<RuntimeGraphPromoter> logger
) : IStudioGraphPromoter
{
    private static readonly string PromoteCommand = JsonSerializer.Serialize(new
    {
        room_id = "studio",
        command = new
        {
            PromoteCheckpointToGraph = new
            {
                selector = new { selector = "latest" }
            }
        }
    });

    public void TryPromoteStudioCheckpoint()
    {
        try
        {
            bridge.ProcessJsonCommand(PromoteCommand);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "studio graph promotion failed; checkpoint not materialized this cycle");
        }
    }
}
