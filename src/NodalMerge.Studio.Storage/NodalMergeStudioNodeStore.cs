using System.Text;
using NodalMerge.Host.Abstractions.Providers;

namespace NodalMerge.Studio.Storage;

public sealed class NodalMergeStudioNodeStore : IStudioNodeStore
{
    internal const string StudioRoomId = "studio";
    private const string StudioPayloadKind = "studio";

    private readonly INodeStoreProvider _nodeStore;

    public NodalMergeStudioNodeStore(INodeStoreProvider nodeStore) => _nodeStore = nodeStore;

    public async Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default)
    {
        var nodeId = BuildNodeId(kind, entityId);
        var record = new AcceptedNodeRecord(
            NodeIdHex: nodeId,
            Payload: Encoding.UTF8.GetBytes(payloadJson),
            PayloadKind: StudioPayloadKind,
            AcceptedAtUtc: DateTimeOffset.UtcNow
        );
        await _nodeStore.PersistAcceptedNodesAsync(StudioRoomId, [record], cancellationToken).ConfigureAwait(false);
    }

    public async Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _nodeStore.LoadRoomSnapshotAsync(StudioRoomId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Nodes.Count == 0)
            return null;

        var prefix = BuildNodeIdPrefix(kind, entityId);
        var latest = snapshot.Nodes
            .Where(n => n.NodeIdHex.StartsWith(prefix, StringComparison.Ordinal)
                     && string.Equals(n.PayloadKind, StudioPayloadKind, StringComparison.Ordinal))
            .OrderByDescending(n => n.AcceptedAtUtc ?? DateTimeOffset.MinValue)
            .FirstOrDefault();

        return latest is null ? null : Encoding.UTF8.GetString(latest.Payload);
    }

    public async Task<IReadOnlyList<(string EntityId, string PayloadJson)>> ReadAllNodesAsync(
        string kind, CancellationToken cancellationToken = default)
    {
        var snapshot = await _nodeStore.LoadRoomSnapshotAsync(StudioRoomId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Nodes.Count == 0)
            return [];

        var kindPrefix = $"studio:{kind}:";
        return snapshot.Nodes
            .Where(n => n.NodeIdHex.StartsWith(kindPrefix, StringComparison.Ordinal)
                     && string.Equals(n.PayloadKind, StudioPayloadKind, StringComparison.Ordinal))
            // Node IDs are "studio:{kind}:{entityId}:{ticksD20}" — the trailing ":" + 20 digits
            // is always exactly 21 characters, so stripping it back off the suffix recovers
            // entityId even though entityId itself may contain "/" (e.g. "base/MP-1").
            .Select(n => (EntityId: n.NodeIdHex[kindPrefix.Length..^21], Node: n))
            .GroupBy(x => x.EntityId)
            .Select(g => g.OrderByDescending(x => x.Node.AcceptedAtUtc ?? DateTimeOffset.MinValue).First())
            .Select(x => (x.EntityId, PayloadJson: Encoding.UTF8.GetString(x.Node.Payload)))
            .ToList();
    }

    internal static string BuildNodeIdPrefix(string kind, string entityId) =>
        $"studio:{kind}:{entityId}:";

    private static string BuildNodeId(string kind, string entityId) =>
        $"studio:{kind}:{entityId}:{DateTimeOffset.UtcNow.Ticks:D20}";
}
