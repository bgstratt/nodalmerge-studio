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

    public DecisionNodeService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<DecisionNode> RecordAsync(DecisionNode decision, CancellationToken ct = default)
    {
        _decisions[decision.DecisionId] = decision;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.DecisionV1, decision.DecisionId, JsonSerializer.Serialize(decision), ct).ConfigureAwait(false);
        return decision;
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