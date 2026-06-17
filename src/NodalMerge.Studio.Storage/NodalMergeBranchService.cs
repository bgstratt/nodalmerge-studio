using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class NodalMergeBranchService(IStudioNodeStore nodeStore, IFileWorkspaceService fileWorkspace) : IBranchService
{
    public async Task<string> CreateBranchAsync(string name, string? fromBranchId = null, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(new { id = name, parentId = fromBranchId, createdAt = DateTimeOffset.UtcNow });
        await nodeStore.WriteNodeAsync(StudioNodeKind.BranchV1, name, json, cancellationToken).ConfigureAwait(false);
        await fileWorkspace.InitBranchAsync(name, fromBranchId, cancellationToken).ConfigureAwait(false);
        return name;
    }

    public Task CheckoutBranchAsync(string branchId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public async Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken = default)
    {
        var nodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.BranchV1, cancellationToken).ConfigureAwait(false);
        return nodes.Select(n => n.EntityId).OrderBy(x => x, StringComparer.Ordinal).ToList();
    }

    public async Task<BranchStatus> GetStatusAsync(string branchId, CancellationToken cancellationToken = default)
    {
        var payload = await nodeStore.ReadNodeAsync(StudioNodeKind.BranchV1, branchId, cancellationToken).ConfigureAwait(false);
        return new BranchStatus(branchId, payload is null ? "unknown" : "active", 0);
    }
}
