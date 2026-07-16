using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

internal sealed class InMemoryKnownGoodStateService : IKnownGoodStateService, IRehydratable
{
    private readonly ConcurrentDictionary<string, KnownGoodState> _states = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IBranchService _branches;
    private readonly IFileWorkspaceService _fileWorkspace;

    public InMemoryKnownGoodStateService(IStudioNodeStore nodeStore, IBranchService branches, IFileWorkspaceService fileWorkspace)
    {
        _nodeStore = nodeStore;
        _branches = branches;
        _fileWorkspace = fileWorkspace;
    }

    public async Task<KnownGoodState> MarkKnownGoodAsync(KnownGoodState state, CancellationToken cancellationToken = default)
    {
        // Slice 6.3a — resolve via state.BranchId's own stored BranchV1.RepositoryId (a known-good
        // state has no WorkUnitId to chain through). Null when BranchId itself never resolved one
        // (ad hoc/global branch, or a pre-6.3a BranchV1 row) — falls back to "studio" like every
        // other unresolvable case, never blocking the mark.
        var repositoryId = state.RepositoryId
            ?? await RepositoryIdResolution.ResolveFromBranchAsync(_nodeStore, state.BranchId, cancellationToken)
                .ConfigureAwait(false);

        // Real point-in-time copy: a snapshot branch seeded from state.BranchId's current files
        // (CreateBranchAsync calls IFileWorkspaceService.InitBranchAsync internally), so a later
        // edit to state.BranchId can't retroactively change what "known good" looked like.
        var snapshotBranchId = await _branches
            .CreateBranchAsync(
                $"knowngood/{state.StateId}", state.BranchId, repositoryId: repositoryId, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var snapshotted = state with { SnapshotBranchId = snapshotBranchId, RepositoryId = repositoryId };

        _states[snapshotted.StateId] = snapshotted;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.KnownGoodStateV1,
            snapshotted.StateId,
            JsonSerializer.Serialize(snapshotted),
            snapshotted.RepositoryId,
            cancellationToken).ConfigureAwait(false);
        return snapshotted;
    }

    public Task<IReadOnlyList<KnownGoodState>> FindKnownGoodAsync(string branchId, CancellationToken cancellationToken = default)
    {
        var results = _states.Values
            .Where(s => s.BranchId == branchId)
            .OrderByDescending(s => s.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<KnownGoodState>>(results);
    }

    public async Task<KnownGoodState?> CheckoutKnownGoodAsync(string stateId, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(stateId, out var state))
            return null;

        // Null only for states persisted before 13e — nothing to restore from in that case.
        if (state.SnapshotBranchId is not null)
            await _fileWorkspace.ApplyBranchAsync(state.SnapshotBranchId, state.BranchId, cancellationToken)
                .ConfigureAwait(false);

        return state;
    }

    public Task<KnownGoodState?> GetAsync(string stateId, CancellationToken cancellationToken = default)
    {
        _states.TryGetValue(stateId, out var state);
        return Task.FromResult(state);
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.KnownGoodStateV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var state = JsonSerializer.Deserialize<KnownGoodState>(payloadJson);
            if (state is not null)
                _states[entityId] = state;
        }
    }
}
