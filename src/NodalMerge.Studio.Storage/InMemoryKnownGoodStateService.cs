using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class InMemoryKnownGoodStateService : IKnownGoodStateService
{
    private readonly ConcurrentDictionary<string, KnownGoodState> _states = new();
    private readonly IStudioNodeStore _nodeStore;

    public InMemoryKnownGoodStateService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<KnownGoodState> MarkKnownGoodAsync(KnownGoodState state, CancellationToken cancellationToken = default)
    {
        _states[state.StateId] = state;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.KnownGoodStateV1,
            state.StateId,
            JsonSerializer.Serialize(state),
            cancellationToken).ConfigureAwait(false);
        return state;
    }

    public Task<IReadOnlyList<KnownGoodState>> FindKnownGoodAsync(string branchId, CancellationToken cancellationToken = default)
    {
        var results = _states.Values
            .Where(s => s.BranchId == branchId)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<KnownGoodState>>(results);
    }

    public Task<KnownGoodState?> CheckoutKnownGoodAsync(string stateId, CancellationToken cancellationToken = default)
    {
        _states.TryGetValue(stateId, out var state);
        return Task.FromResult(state);
    }
}
