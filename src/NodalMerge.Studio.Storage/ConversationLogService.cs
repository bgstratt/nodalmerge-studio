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
    private readonly IStudioLocalLogStore _localLog;

    // L2.1 — persists to the studio-owned local append log, not the CRDT room; this is peer-local
    // reasoning history, off the sync graph. L2.3 publishes a bounded, shareable slice of it to the
    // CAS content plane referenced by a repo-scoped ConversationRef.
    public ConversationLogService(IStudioLocalLogStore localLog)
    {
        _localLog = localLog;
    }

    public async Task<ConversationLogEntry> RecordAsync(ConversationLogEntry entry, CancellationToken ct = default)
    {
        // L2.2 — cap the uncapped reasoning/tool-input fields (tool *results* were already capped
        // below via Truncate). Bounds the per-cycle node payload; the marker keeps truncation visible.
        var truncatedResults = entry.ToolResults.Select(Truncate).ToList();
        var truncatedCalls = entry.ToolCalls
            .Select(c => c with { InputJson = NodePayloadLimits.Cap(c.InputJson) ?? c.InputJson })
            .ToList();
        var stored = entry with
        {
            AssistantText = NodePayloadLimits.Cap(entry.AssistantText),
            ToolCalls = truncatedCalls,
            ToolResults = truncatedResults,
        };

        _entriesById[stored.LogId] = stored;
        lock (_indexLock)
        {
            if (!_byWorkUnit.TryGetValue(stored.WorkUnitId, out var list))
                _byWorkUnit[stored.WorkUnitId] = list = [];
            list.Add(stored.LogId);
        }

        await _localLog.AppendAsync(
            StudioNodeKind.ConversationLogV1, stored.LogId, JsonSerializer.Serialize(stored), stored.OccurredAt, ct)
            .ConfigureAwait(false);

        return stored;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _localLog.ReadAllAsync(StudioNodeKind.ConversationLogV1, ct).ConfigureAwait(false);
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
            // Order by wall-clock, not CycleNumber: cycle numbers reset per agent/process
            // (orchestrator, planner, re-plan, retry each start at 0), so a CycleNumber-first
            // sort groups every "cycle 0" together across agents instead of flowing the
            // conversation in the real order things happened. OccurredAt is the true timeline;
            // CycleNumber only tie-breaks entries stamped at the same instant.
            .OrderBy(entry => entry.OccurredAt)
            .ThenBy(entry => entry.CycleNumber)
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
