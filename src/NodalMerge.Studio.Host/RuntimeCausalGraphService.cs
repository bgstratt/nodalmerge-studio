using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.Studio.Core.Services;
using System.Text.Json;

namespace NodalMerge.Studio.Host;

internal sealed class RuntimeCausalGraphService(
    IRuntimeCommandBridge bridge,
    ILogger<RuntimeCausalGraphService> logger
) : IStudioCausalGraphService
{
    private const string StudioRoomId = "studio";

    private static string MakeEnvelope(object command) =>
        JsonSerializer.Serialize(new { room_id = StudioRoomId, command });

    public Task<string[]> GetFrontierAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = MakeEnvelope(new { GetFrontier = new { } });
            var response = bridge.ProcessJsonCommand(json);
            if (response.Status != AsStatus.Ok) return Task.FromResult(Array.Empty<string>());

            using var doc = JsonDocument.Parse(response.EventsJson);
            foreach (var ev in doc.RootElement.EnumerateArray())
            {
                if (ev.TryGetProperty("FrontierQueried", out var fq)
                    && fq.TryGetProperty("frontier_heads_hex", out var heads)
                    && heads.ValueKind == JsonValueKind.Array)
                {
                    return Task.FromResult(heads.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()!)
                        .ToArray());
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetFrontier failed");
        }
        return Task.FromResult(Array.Empty<string>());
    }

    public Task<CausalParentsResult> GetCausalParentsAsync(string nodeIdHex, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = MakeEnvelope(new { GetCausalParents = new { node_id_hex = nodeIdHex } });
            var response = bridge.ProcessJsonCommand(json);
            if (response.Status != AsStatus.Ok) return Task.FromResult(new CausalParentsResult([], false));

            using var doc = JsonDocument.Parse(response.EventsJson);
            foreach (var ev in doc.RootElement.EnumerateArray())
            {
                if (ev.TryGetProperty("CausalParentsQueried", out var cp))
                {
                    var nodeFound = cp.TryGetProperty("node_found", out var nf) && nf.GetBoolean();
                    var parents = cp.TryGetProperty("parent_ids_hex", out var pids) && pids.ValueKind == JsonValueKind.Array
                        ? pids.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!)
                            .ToArray()
                        : [];
                    return Task.FromResult(new CausalParentsResult(parents, nodeFound));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetCausalParents failed for node {NodeId}", nodeIdHex);
        }
        return Task.FromResult(new CausalParentsResult([], false));
    }

    public Task<CanonicalResolutionResult> GetCanonicalResolutionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var json = MakeEnvelope(new { GetCanonicalResolution = new { } });
            var response = bridge.ProcessJsonCommand(json);
            if (response.Status != AsStatus.Ok) return Task.FromResult(new CanonicalResolutionResult([]));

            using var doc = JsonDocument.Parse(response.EventsJson);
            foreach (var ev in doc.RootElement.EnumerateArray())
            {
                if (ev.TryGetProperty("CanonicalResolutionQueried", out var cr)
                    && cr.TryGetProperty("entries", out var entries)
                    && entries.ValueKind == JsonValueKind.Array)
                {
                    var list = entries.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.Object
                            && e.TryGetProperty("key", out _)
                            && e.TryGetProperty("value_bytes_b64", out _))
                        .Select(e => new CanonicalResolutionEntry(
                            e.GetProperty("key").GetString()!,
                            e.GetProperty("value_bytes_b64").GetString()!))
                        .ToList();
                    return Task.FromResult(new CanonicalResolutionResult(list));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "GetCanonicalResolution failed");
        }
        return Task.FromResult(new CanonicalResolutionResult([]));
    }

    public Task<SyncDiffResult> ComputeSyncDiffAsync(string[] peerNodeIdsHex, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = MakeEnvelope(new { ComputeSyncDiff = new { peer_node_ids_hex = peerNodeIdsHex } });
            var response = bridge.ProcessJsonCommand(json);
            if (response.Status != AsStatus.Ok) return Task.FromResult(new SyncDiffResult([], []));

            using var doc = JsonDocument.Parse(response.EventsJson);
            foreach (var ev in doc.RootElement.EnumerateArray())
            {
                if (ev.TryGetProperty("SyncDiffComputed", out var sd))
                {
                    var onlyServer = sd.TryGetProperty("only_in_server", out var os) && os.ValueKind == JsonValueKind.Array
                        ? os.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                        : [];
                    var onlyPeer = sd.TryGetProperty("only_in_peer", out var op) && op.ValueKind == JsonValueKind.Array
                        ? op.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToArray()
                        : [];
                    return Task.FromResult(new SyncDiffResult(onlyServer, onlyPeer));
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ComputeSyncDiff failed");
        }
        return Task.FromResult(new SyncDiffResult([], []));
    }
}
