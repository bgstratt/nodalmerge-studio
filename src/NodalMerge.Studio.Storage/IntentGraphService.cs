using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class IntentGraphService : IIntentGraphService
{
    private readonly ConcurrentDictionary<string, ChangeIntent> _byId = new();
    private readonly ConcurrentDictionary<string, List<string>> _byWorkUnit = new();
    // normalized TargetPath → intentIds, for cross-work-unit overlap lookup
    private readonly ConcurrentDictionary<string, List<string>> _byTargetPath = new();
    private readonly Lock _indexLock = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IArtifactLineageService _artifacts;

    public IntentGraphService(IStudioNodeStore nodeStore, IArtifactLineageService artifacts)
    {
        _nodeStore = nodeStore;
        _artifacts = artifacts;
    }

    public async Task RecordIntentAsync(ChangeIntent intent, CancellationToken ct = default)
    {
        // Idempotent: a second record with the same IntentId is a no-op.
        if (_byId.ContainsKey(intent.IntentId))
            return;

        _byId[intent.IntentId] = intent;
        lock (_indexLock)
        {
            Add(_byWorkUnit, intent.WorkUnitId, intent.IntentId);
            Add(_byTargetPath, Normalize(intent.TargetPath), intent.IntentId);
        }

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ChangeIntentV1,
            intent.IntentId,
            JsonSerializer.Serialize(intent),
            ct).ConfigureAwait(false);

        // Mirror into the lineage store (10d) so intent history is queryable/replayable
        // alongside the rest of a work unit's artifact chain.
        await _artifacts.RecordAsync(new ArtifactRef(
            intent.IntentId,
            ArtifactType.ChangeIntent,
            intent.WorkUnitId,
            ArtifactStatus.Active,
            intent.CreatedAt,
            intent.WorkUnitId,
            null), ct).ConfigureAwait(false);
    }

    public Task<IReadOnlyList<ChangeIntent>> QueryIntentsAsync(string workUnitId, CancellationToken ct = default)
    {
        List<string> ids;
        lock (_indexLock)
            ids = _byWorkUnit.TryGetValue(workUnitId, out var list) ? [.. list] : [];

        var intents = ids
            .Select(id => _byId.TryGetValue(id, out var i) ? i : null)
            .Where(i => i is not null)
            .Cast<ChangeIntent>()
            .OrderBy(i => i.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ChangeIntent>>(intents);
    }

    public Task<IReadOnlyList<ChangeIntent>> QueryOverlappingAsync(ChangeIntent intent, CancellationToken ct = default)
    {
        List<string> ids;
        lock (_indexLock)
            ids = _byTargetPath.TryGetValue(Normalize(intent.TargetPath), out var list) ? [.. list] : [];

        var overlapping = ids
            .Select(id => _byId.TryGetValue(id, out var i) ? i : null)
            .Where(i => i is not null)
            .Cast<ChangeIntent>()
            // Same work unit re-declaring or refining its own intent is not a conflict.
            .Where(i => i.IntentId != intent.IntentId && i.WorkUnitId != intent.WorkUnitId)
            .Where(i => RegionsOverlap(i.RegionDescriptor, intent.RegionDescriptor))
            .OrderBy(i => i.CreatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ChangeIntent>>(overlapping);
    }

    public async Task RemoveIntentAsync(string intentId, CancellationToken ct = default)
    {
        if (!_byId.TryRemove(intentId, out var intent))
            return;

        lock (_indexLock)
        {
            _byWorkUnit.GetValueOrDefault(intent.WorkUnitId)?.Remove(intentId);
            _byTargetPath.GetValueOrDefault(Normalize(intent.TargetPath))?.Remove(intentId);
        }

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ChangeIntentV1, intentId, "{\"removed\":true}", ct).ConfigureAwait(false);
    }

    private static void Add(ConcurrentDictionary<string, List<string>> index, string key, string value)
    {
        if (!index.TryGetValue(key, out var list))
            index[key] = list = [];
        list.Add(value);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').ToLowerInvariant();

    // A whole-file region ("" or "*") overlaps with any region on the same path; otherwise
    // two intents conflict only if they name the exact same region.
    private static bool RegionsOverlap(string a, string b) =>
        IsWholeFile(a) || IsWholeFile(b) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static bool IsWholeFile(string region) =>
        string.IsNullOrEmpty(region) || region is "*" or "whole-file";
}
