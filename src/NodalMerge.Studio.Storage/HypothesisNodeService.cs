using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IHypothesisNodeService
{
    Task<HypothesisNode> RecordAsync(HypothesisNode node, CancellationToken ct = default);
    Task<IReadOnlyList<HypothesisNode>> ListByParentWorkUnitIdAsync(string parentWorkUnitId, CancellationToken ct = default);
    Task<HypothesisNode> UpdateStatusAsync(string hypothesisId, HypothesisStatus status, CancellationToken ct = default);
}

public sealed class HypothesisNodeService : IHypothesisNodeService, IRehydratable
{
    private readonly ConcurrentDictionary<string, HypothesisNode> _hypotheses = new();
    private readonly IStudioNodeStore _nodeStore;

    public HypothesisNodeService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<HypothesisNode> RecordAsync(HypothesisNode node, CancellationToken ct = default)
    {
        _hypotheses[node.HypothesisId] = node;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.HypothesisV1, node.HypothesisId, JsonSerializer.Serialize(node), ct).ConfigureAwait(false);
        return node;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.HypothesisV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var node = JsonSerializer.Deserialize<HypothesisNode>(payloadJson);
            if (node is not null) _hypotheses[node.HypothesisId] = node;
        }
    }

    public Task<IReadOnlyList<HypothesisNode>> ListByParentWorkUnitIdAsync(string parentWorkUnitId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HypothesisNode>>(
            _hypotheses.Values.Where(h => h.ParentWorkUnitId == parentWorkUnitId).OrderBy(h => h.CreatedAt).ToList());

    public async Task<HypothesisNode> UpdateStatusAsync(string hypothesisId, HypothesisStatus status, CancellationToken ct = default)
    {
        if (!_hypotheses.TryGetValue(hypothesisId, out var existing))
            throw new InvalidOperationException($"Hypothesis '{hypothesisId}' not found.");

        var updated = existing with { Status = status };
        return await RecordAsync(updated, ct).ConfigureAwait(false);
    }
}
