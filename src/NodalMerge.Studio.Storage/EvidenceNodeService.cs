using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IEvidenceNodeService
{
    Task<EvidenceNode> RecordAsync(EvidenceNode evidence, CancellationToken ct = default);
    Task<IReadOnlyList<EvidenceNode>> ListByWorkUnitAsync(string workUnitId, CancellationToken ct = default);
}

public sealed class EvidenceNodeService : IEvidenceNodeService, IRehydratable
{
    private readonly ConcurrentDictionary<string, EvidenceNode> _evidence = new();
    private readonly IStudioNodeStore _nodeStore;

    public EvidenceNodeService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<EvidenceNode> RecordAsync(EvidenceNode evidence, CancellationToken ct = default)
    {
        _evidence[evidence.EvidenceId] = evidence;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.EvidenceV1, evidence.EvidenceId, JsonSerializer.Serialize(evidence), ct).ConfigureAwait(false);
        return evidence;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.EvidenceV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var ev = JsonSerializer.Deserialize<EvidenceNode>(payloadJson);
            if (ev is not null) _evidence[ev.EvidenceId] = ev;
        }
    }

    public Task<IReadOnlyList<EvidenceNode>> ListByWorkUnitAsync(string workUnitId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<EvidenceNode>>(
            _evidence.Values.Where(e => e.WorkUnitId == workUnitId).OrderByDescending(e => e.AttachedAt).ToList());
}