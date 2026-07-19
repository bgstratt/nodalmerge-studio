using System.Text.Json;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// L2.3 (plans/room-persistence-bloat.md) — publishes a bounded, peer-visible reasoning transcript
// for a work unit to the CAS content plane and writes a repo-scoped ConversationRef pointing at it.
// This is the tier-2 "share the reasoning" mechanism: the transcript BYTES ride the content plane
// (pulled on demand by hash), only the small ref replicates. No synchronous LLM summarization — the
// transcript is the raw ConversationLogV1 reasoning + tool intent (tool-result bodies dropped), and
// the node label is a cheap first-line heuristic.
public interface IReasoningPublisher
{
    // Publishes the work unit's reasoning-so-far and returns the ConversationRef (RefId links back),
    // or null when there is no reasoning to publish. `repositoryId` (already resolved by the caller)
    // routes the ref to the repo room so peers on that repo see it.
    Task<ConversationRef?> PublishAsync(
        string workUnitId, string? sessionId, string? repositoryId,
        string? decisionId = null, string? proposalId = null,
        CancellationToken cancellationToken = default);
}

public sealed class ReasoningPublisherService : IReasoningPublisher
{
    // Tool inputs can be large (a Write's file content); a peer only needs the intent, so preview.
    private const int MaxToolInputPreview = 512;
    private const int MaxLabelLength = 120;

    private readonly IConversationLogService _conversationLog;
    private readonly IBlobStoreProvider _blobStore;
    private readonly IStudioNodeStore _nodeStore;

    public ReasoningPublisherService(
        IConversationLogService conversationLog, IBlobStoreProvider blobStore, IStudioNodeStore nodeStore)
    {
        _conversationLog = conversationLog;
        _blobStore = blobStore;
        _nodeStore = nodeStore;
    }

    public async Task<ConversationRef?> PublishAsync(
        string workUnitId, string? sessionId, string? repositoryId,
        string? decisionId = null, string? proposalId = null,
        CancellationToken cancellationToken = default)
    {
        var entries = await _conversationLog.GetEntriesAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        if (entries.Count == 0)
            return null;

        // Reasoning + tool intent per cycle; tool-result bodies are dropped (derivable / re-runnable
        // and the high-volume part of the firehose).
        var cycles = entries
            .Select(e => new PublishedReasoningCycle(
                e.CycleNumber,
                e.AgentRole,
                e.AssistantText,
                e.ToolCalls.Select(c => new PublishedToolCall(c.Name, Preview(c.InputJson))).ToList(),
                e.OccurredAt))
            .ToList();

        var transcript = new PublishedReasoningTranscript(workUnitId, sessionId, cycles);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(transcript);
        var hash = BlobHasher.ComputeHash(bytes);
        await _blobStore.PutBlobAsync(hash, bytes, "application/json", cancellationToken).ConfigureAwait(false);

        var cref = new ConversationRef(
            RefId: $"cref-{Guid.NewGuid():N}",
            WorkUnitId: workUnitId,
            TranscriptBlobHash: hash,
            CycleCount: cycles.Count,
            Label: DeriveLabel(entries),
            PublishedAt: DateTimeOffset.UtcNow,
            SessionId: sessionId,
            DecisionId: decisionId,
            ProposalId: proposalId,
            RepositoryId: repositoryId);

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ConversationRefV1, cref.RefId, JsonSerializer.Serialize(cref), repositoryId, cancellationToken)
            .ConfigureAwait(false);

        return cref;
    }

    private static string Preview(string inputJson) =>
        inputJson.Length <= MaxToolInputPreview ? inputJson : inputJson[..MaxToolInputPreview] + "…";

    // First non-empty line of the most recent cycle's assistant text — a label without an LLM turn.
    private static string? DeriveLabel(IReadOnlyList<ConversationLogEntry> entries)
    {
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var text = entries[i].AssistantText;
            if (string.IsNullOrWhiteSpace(text))
                continue;

            var firstLine = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrEmpty(firstLine))
                continue;

            return firstLine.Length <= MaxLabelLength ? firstLine : firstLine[..MaxLabelLength] + "…";
        }

        return null;
    }
}
