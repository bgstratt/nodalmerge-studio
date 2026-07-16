using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IDecisionNodeService
{
    Task<DecisionNode> RecordAsync(DecisionNode decision, CancellationToken ct = default);
    Task<IReadOnlyList<DecisionNode>> ListByWorkUnitAsync(string workUnitId, CancellationToken ct = default);
}

public sealed class DecisionNodeService : IDecisionNodeService, IRehydratable
{
    private readonly ConcurrentDictionary<string, DecisionNode> _decisions = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IWorkUnitService? _workUnits;

    // workUnits is optional/constructor-injected directly (not via lazy IServiceProvider) — safe
    // because, unlike IArtifactLineageService/IMergeService/IKnownGoodStateService,
    // InMemoryWorkUnitService does not itself depend on IDecisionNodeService, so no DI cycle exists.
    public DecisionNodeService(IStudioNodeStore nodeStore, IWorkUnitService? workUnits = null)
    {
        _nodeStore = nodeStore;
        _workUnits = workUnits;
    }

    public async Task<DecisionNode> RecordAsync(DecisionNode decision, CancellationToken ct = default)
    {
        // Slice 6.3a — denormalize from WorkUnitId's own RepositoryId when not already supplied.
        var stored = decision;
        if (stored.RepositoryId is null)
        {
            var repositoryId = await RepositoryIdResolution
                .ResolveFromWorkUnitAsync(_workUnits, stored.WorkUnitId, ct).ConfigureAwait(false);
            if (repositoryId is not null)
                stored = stored with { RepositoryId = repositoryId };
        }

        _decisions[stored.DecisionId] = stored;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.DecisionV1, stored.DecisionId, JsonSerializer.Serialize(stored), stored.RepositoryId, ct)
            .ConfigureAwait(false);
        return stored;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.DecisionV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var decision = JsonSerializer.Deserialize<DecisionNode>(payloadJson);
            if (decision is not null) _decisions[decision.DecisionId] = decision;
        }
    }

    public Task<IReadOnlyList<DecisionNode>> ListByWorkUnitAsync(string workUnitId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DecisionNode>>(
            _decisions.Values.Where(d => d.WorkUnitId == workUnitId).OrderByDescending(d => d.DecidedAt).ToList());
}