using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class ConversationLogService : IConversationLogService, IRehydratable
{
    // A single oversized tool result (e.g. a large file read) shouldn't bloat a node-store
    // payload — cap the string and flag it, rather than capping cycle count (AP-5: append-only,
    // no dropped history).
    private const int MaxToolResultLength = 20_000;

    private readonly ConcurrentDictionary<string, ConversationLogEntry> _entriesById = new();
    // workUnitId -> ordered list of logIds
    private readonly ConcurrentDictionary<string, List<string>> _byWorkUnit = new();
    private readonly Lock _indexLock = new();
    private readonly IStudioNodeStore _nodeStore;

    public ConversationLogService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<ConversationLogEntry> RecordAsync(ConversationLogEntry entry, CancellationToken ct = default)
    {
        var truncatedResults = entry.ToolResults.Select(Truncate).ToList();
        var stored = entry with { ToolResults = truncatedResults };

        _entriesById[stored.LogId] = stored;
        lock (_indexLock)
        {
            if (!_byWorkUnit.TryGetValue(stored.WorkUnitId, out var list))
                _byWorkUnit[stored.WorkUnitId] = list = [];
            list.Add(stored.LogId);
        }

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ConversationLogV1, stored.LogId, JsonSerializer.Serialize(stored), ct).ConfigureAwait(false);

        return stored;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.ConversationLogV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var entry = JsonSerializer.Deserialize<ConversationLogEntry>(payloadJson);
            if (entry is null || !_entriesById.TryAdd(entry.LogId, entry))
                continue;

            lock (_indexLock)
            {
                if (!_byWorkUnit.TryGetValue(entry.WorkUnitId, out var list))
                    _byWorkUnit[entry.WorkUnitId] = list = [];
                list.Add(entry.LogId);
            }
        }
    }

    public Task<IReadOnlyList<ConversationLogEntry>> GetEntriesAsync(string workUnitId, CancellationToken ct = default)
    {
        List<string> ids;
        lock (_indexLock)
            ids = _byWorkUnit.TryGetValue(workUnitId, out var list) ? [.. list] : [];

        var entries = ids
            .Select(id => _entriesById.TryGetValue(id, out var entry) ? entry : null)
            .Where(entry => entry is not null)
            .Cast<ConversationLogEntry>()
            .OrderBy(entry => entry.CycleNumber)
            .ThenBy(entry => entry.OccurredAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<ConversationLogEntry>>(entries);
    }

    private static ConversationToolResult Truncate(ConversationToolResult result)
    {
        if (result.Result.Length <= MaxToolResultLength)
            return result;

        return result with { Result = result.Result[..MaxToolResultLength] + "...truncated", Truncated = true };
    }
}
