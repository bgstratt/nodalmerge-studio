using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class CausalGraphTools(IStudioCausalGraphService causal)
{
    [McpServerTool(Name = McpToolNames.CausalGetFrontier)]
    [Description("Get the current CRDT frontier heads for the studio room. Returns the set of tip node IDs that represent the leading edge of the causal graph after checkpoint promotion.")]
    public async Task<string> GetFrontierAsync(CancellationToken cancellationToken = default)
    {
        var heads = await causal.GetFrontierAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            frontierHeads = heads,
            headCount = heads.Length
        });
    }

    [McpServerTool(Name = McpToolNames.CausalGetParents)]
    [Description("Get the causal parents of a specific node in the studio CRDT graph. Returns the parent node IDs and whether the node was found.")]
    public async Task<string> GetCausalParentsAsync(
        [Description("64-character lowercase hex node ID to look up.")] string nodeIdHex,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(nodeIdHex) || nodeIdHex.Length != 64)
        {
            return McpJson.Error(McpToolNames.CausalGetParents, "nodeIdHex must be a 64-character lowercase hex string.");
        }

        var result = await causal.GetCausalParentsAsync(nodeIdHex, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            nodeIdHex,
            parentIds = result.ParentIdsHex,
            nodeFound = result.NodeFound
        });
    }

    [McpServerTool(Name = McpToolNames.CausalGetResolution)]
    [Description("Get the canonical resolution of the studio CRDT graph — the merged key/value state after resolving all causal conflicts. Values are base64-encoded bytes.")]
    public async Task<string> GetCanonicalResolutionAsync(CancellationToken cancellationToken = default)
    {
        var result = await causal.GetCanonicalResolutionAsync(cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            entries = result.Entries.Select(e => new { key = e.Key, valueBytesB64 = e.ValueBytesB64 }),
            entryCount = result.Entries.Count
        });
    }

    [McpServerTool(Name = McpToolNames.CausalComputeSyncDiff)]
    [Description("Compute the sync diff between the studio CRDT graph and a peer's known node set. Returns nodes only in the server and nodes only in the peer.")]
    public async Task<string> ComputeSyncDiffAsync(
        [Description("Array of node ID hex strings the peer already has.")] string[] peerNodeIdsHex,
        CancellationToken cancellationToken = default)
    {
        var result = await causal.ComputeSyncDiffAsync(peerNodeIdsHex, cancellationToken).ConfigureAwait(false);
        return JsonSerializer.Serialize(new
        {
            onlyInServer = result.OnlyInServer,
            onlyInPeer = result.OnlyInPeer,
            serverOnlyCount = result.OnlyInServer.Length,
            peerOnlyCount = result.OnlyInPeer.Length
        });
    }
}
