using System.Text.Json;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class NodalMergeBranchService(IStudioNodeStore nodeStore, IFileWorkspaceService fileWorkspace) : IBranchService
{
    public async Task<string> CreateBranchAsync(string name, string? fromBranchId = null,
        IReadOnlyList<string>? fileScope = null, string? repositoryId = null, string? seedSnapshotId = null,
        CancellationToken cancellationToken = default)
    {
        // Slice 6.3a — RepositoryId added to what was previously a bare {id, parentId, createdAt}.
        // The JSON property name ("RepositoryId", PascalCase) matches every other routed record so
        // NodalMergeStudioNodeStore's generic migration-time payload parsing (TryExtractStringProperty
        // looking for that exact literal) works uniformly across kinds.
        // Slice 6.5 Part 2 — seedSnapshotId, the branch-record half of WorkUnit.SeedSnapshotId (see
        // that field's own doc comment); null for pre-slice/ad hoc branches, same convention.
        var json = JsonSerializer.Serialize(new
        {
            id = name,
            parentId = fromBranchId,
            createdAt = DateTimeOffset.UtcNow,
            RepositoryId = repositoryId,
            SeedSnapshotId = seedSnapshotId,
        });
        await nodeStore.WriteNodeAsync(StudioNodeKind.BranchV1, name, json, repositoryId, cancellationToken).ConfigureAwait(false);
        await fileWorkspace.InitBranchAsync(name, fromBranchId, fileScope, cancellationToken).ConfigureAwait(false);
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
