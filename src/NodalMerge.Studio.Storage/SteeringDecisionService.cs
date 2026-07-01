using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface ISteeringDecisionService
{
    Task<SteeringDecision> RecordAsync(SteeringDecision decision, CancellationToken ct = default);
    Task<IReadOnlyList<SteeringDecision>> ListByWorkUnitAsync(string workUnitId, CancellationToken ct = default);
}

public sealed class SteeringDecisionService : ISteeringDecisionService, IRehydratable
{
    private readonly ConcurrentDictionary<string, SteeringDecision> _decisions = new();
    private readonly IStudioNodeStore _nodeStore;

    public SteeringDecisionService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<SteeringDecision> RecordAsync(SteeringDecision decision, CancellationToken ct = default)
    {
        _decisions[decision.SteeringDecisionId] = decision;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.SteeringDecisionV1, decision.SteeringDecisionId,
            JsonSerializer.Serialize(decision), ct).ConfigureAwait(false);
        return decision;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.SteeringDecisionV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var decision = JsonSerializer.Deserialize<SteeringDecision>(payloadJson);
            if (decision is not null) _decisions[decision.SteeringDecisionId] = decision;
        }
    }

    public Task<IReadOnlyList<SteeringDecision>> ListByWorkUnitAsync(string workUnitId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SteeringDecision>>(
            _decisions.Values.Where(d => d.WorkUnitId == workUnitId)
                .OrderByDescending(d => d.SteeredAt).ToList());
}