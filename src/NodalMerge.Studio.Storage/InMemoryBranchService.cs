using System.Collections.Concurrent;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class InMemoryBranchService(IFileWorkspaceService fileWorkspace) : IBranchService
{
    private sealed record BranchEntry(string BranchId, string? ParentBranchId, string WorkingDirectory);

    private readonly ConcurrentDictionary<string, BranchEntry> _branches = new();

    public async Task<string> CreateBranchAsync(string name, string? fromBranchId = null, CancellationToken cancellationToken = default)
    {
        await fileWorkspace.InitBranchAsync(name, fromBranchId, cancellationToken).ConfigureAwait(false);
        var workDir = await fileWorkspace.GetWorkingDirectoryAsync(name, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        _branches.TryAdd(name, new BranchEntry(name, fromBranchId, workDir));
        return name;
    }

    // No-op: agents track their active branch through WorkUnit.BranchId, not through a host session switch.
    public Task CheckoutBranchAsync(string branchId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_branches.Keys.OrderBy(k => k).ToList());

    public async Task<BranchStatus> GetStatusAsync(string branchId, CancellationToken cancellationToken = default)
    {
        var status = _branches.ContainsKey(branchId) ? "active" : "unknown";
        var files  = await fileWorkspace.ListAsync(branchId, ct: cancellationToken).ConfigureAwait(false);
        return new BranchStatus(branchId, status, files.Count);
    }
}
