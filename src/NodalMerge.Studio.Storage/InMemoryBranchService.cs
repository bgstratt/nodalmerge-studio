using System.Collections.Concurrent;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class InMemoryBranchService : IBranchService
{
    private sealed record BranchEntry(string BranchId, string? ParentBranchId);

    private readonly ConcurrentDictionary<string, BranchEntry> _branches = new();

    public Task<string> CreateBranchAsync(string name, string? fromBranchId = null, CancellationToken cancellationToken = default)
    {
        _branches.TryAdd(name, new BranchEntry(name, fromBranchId));
        return Task.FromResult(name);
    }

    // No-op: agents track their active branch through WorkUnit.BranchId, not through a host session switch.
    public Task CheckoutBranchAsync(string branchId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>(_branches.Keys.OrderBy(k => k).ToList());

    public Task<BranchStatus> GetStatusAsync(string branchId, CancellationToken cancellationToken = default)
    {
        var status = _branches.ContainsKey(branchId) ? "active" : "unknown";
        return Task.FromResult(new BranchStatus(branchId, status, 0));
    }
}
