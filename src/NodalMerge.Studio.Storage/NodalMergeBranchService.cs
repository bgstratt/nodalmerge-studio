using System.Text;
using System.Text.Json;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class NodalMergeBranchService : IBranchService
{
    private readonly INodeStoreProvider _nodeStore;

    public NodalMergeBranchService(INodeStoreProvider nodeStore) => _nodeStore = nodeStore;

    public async Task<string> CreateBranchAsync(string name, string? fromBranchId = null, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new { id = name, parentId = fromBranchId, createdAt = DateTimeOffset.UtcNow });
        var nodeId = BuildNodeId(name);
        var record = new AcceptedNodeRecord(
            NodeIdHex: nodeId,
            Payload: Encoding.UTF8.GetBytes(json),
            PayloadKind: "studio-branch",
            AcceptedAtUtc: DateTimeOffset.UtcNow
        );
        await _nodeStore.PersistAcceptedNodesAsync(NodalMergeStudioNodeStore.StudioRoomId, [record], cancellationToken).ConfigureAwait(false);
        return name;
    }

    public Task CheckoutBranchAsync(string branchId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = await _nodeStore.LoadRoomSnapshotAsync(NodalMergeStudioNodeStore.StudioRoomId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Nodes.Count == 0)
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        const string prefix = "studio-branch:";
        foreach (var node in snapshot.Nodes)
        {
            if (!node.NodeIdHex.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = node.NodeIdHex[prefix.Length..];
            var colonIdx = rest.LastIndexOf(':');
            if (colonIdx < 0) continue;
            seen.Add(rest[..colonIdx]);
        }
        return [.. seen.OrderBy(x => x, StringComparer.Ordinal)];
    }

    public async Task<BranchStatus> GetStatusAsync(string branchId, CancellationToken cancellationToken = default)
    {
        var snapshot = await _nodeStore.LoadRoomSnapshotAsync(NodalMergeStudioNodeStore.StudioRoomId, cancellationToken).ConfigureAwait(false);
        var exists = snapshot?.Nodes.Any(n =>
            n.NodeIdHex.StartsWith($"studio-branch:{branchId}:", StringComparison.Ordinal)) ?? false;
        return new BranchStatus(branchId, exists ? "active" : "unknown", 0);
    }

    private static string BuildNodeId(string branchId) =>
        $"studio-branch:{branchId}:{DateTimeOffset.UtcNow.Ticks:D20}";
}
