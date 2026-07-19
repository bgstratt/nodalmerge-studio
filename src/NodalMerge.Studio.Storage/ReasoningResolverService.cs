using System.Text;
using System.Text.Json;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// L2.3 (plans/room-persistence-bloat.md) — resolves a work unit's reasoning for display, unified as
// ConversationLogEntry[] so the existing conversation-log drawer needs no client change. Prefers the
// local full-fidelity ConversationLogV1 (the owning peer's own work); when that's empty — i.e. the
// work unit ran on ANOTHER peer — it falls back to the peer-published transcript by finding the
// repo-scoped ConversationRef (which replicated) and pulling its CAS blob on demand. This is the read
// half of tier-2 reasoning sharing: the ref arrives via replication, the bytes arrive via this pull.
public interface IReasoningResolver
{
    Task<IReadOnlyList<ConversationLogEntry>> GetReasoningAsync(
        string workUnitId, CancellationToken cancellationToken = default);
}

public sealed class ReasoningResolverService : IReasoningResolver
{
    private readonly IConversationLogService _conversationLog;
    private readonly IStudioNodeStore _nodeStore;
    private readonly IBlobStoreProvider? _blobStore;

    public ReasoningResolverService(
        IConversationLogService conversationLog, IStudioNodeStore nodeStore, IBlobStoreProvider? blobStore = null)
    {
        _conversationLog = conversationLog;
        _nodeStore = nodeStore;
        _blobStore = blobStore;
    }

    public async Task<IReadOnlyList<ConversationLogEntry>> GetReasoningAsync(
        string workUnitId, CancellationToken cancellationToken = default)
    {
        // Local full-fidelity reasoning wins when present (this peer ran the work unit).
        var local = await _conversationLog.GetEntriesAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        if (local.Count > 0)
            return local;

        if (_blobStore is null)
            return local;

        var cref = await FindLatestRefAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        if (cref is null)
            return local;

        var blob = await _blobStore.TryGetBlobAsync(cref.TranscriptBlobHash, cancellationToken).ConfigureAwait(false);
        if (!blob.Found || blob.Bytes is null)
            return local;

        PublishedReasoningTranscript? transcript;
        try
        {
            transcript = JsonSerializer.Deserialize<PublishedReasoningTranscript>(Encoding.UTF8.GetString(blob.Bytes));
        }
        catch (JsonException)
        {
            return local;
        }
        if (transcript is null)
            return local;

        return transcript.Cycles
            .Select(c => new ConversationLogEntry(
                LogId: $"published-{transcript.WorkUnitId}-{c.CycleNumber}",
                WorkUnitId: transcript.WorkUnitId,
                AgentId: "(remote)",
                AgentRole: c.AgentRole,
                TaskId: null,
                CycleNumber: c.CycleNumber,
                AssistantText: c.AssistantText,
                ToolCalls: c.ToolCalls.Select(tc => new ConversationToolCall(string.Empty, tc.Name, tc.InputPreview)).ToList(),
                ToolResults: [],
                StopReason: string.Empty,
                OccurredAt: c.OccurredAt,
                SessionId: transcript.SessionId))
            // Match the local path's timeline ordering (see ConversationLogService.GetEntriesAsync):
            // wall-clock, not CycleNumber, so a remote peer's transcript flows in the same order.
            .OrderBy(e => e.OccurredAt)
            .ThenBy(e => e.CycleNumber)
            .ToList();
    }

    private async Task<ConversationRef?> FindLatestRefAsync(string workUnitId, CancellationToken ct)
    {
        var refNodes = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.ConversationRefV1, ct).ConfigureAwait(false);
        ConversationRef? latest = null;
        foreach (var (_, json) in refNodes)
        {
            ConversationRef? cref;
            try { cref = JsonSerializer.Deserialize<ConversationRef>(json); }
            catch (JsonException) { continue; }
            if (cref is null || cref.WorkUnitId != workUnitId)
                continue;
            if (latest is null || cref.PublishedAt > latest.PublishedAt)
                latest = cref;
        }
        return latest;
    }
}
