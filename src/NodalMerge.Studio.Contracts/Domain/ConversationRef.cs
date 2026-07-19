namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// L2.3 (plans/room-persistence-bloat.md) — a repo-scoped, replicating reference to a peer-visible
/// reasoning transcript. The transcript body (a <see cref="PublishedReasoningTranscript"/>) lives in
/// the CAS content plane, pulled on demand by hash; this node carries only ids + the blob hash + a
/// cheap heuristic label, so it replicates cheaply. It is the link that lets a same-repo peer trace
/// the agentic reasoning behind a decision — the "why", not just file state.
/// </summary>
public sealed record ConversationRef(
    string RefId,
    string WorkUnitId,
    // The transcript body is a PublishedReasoningTranscript serialized to JSON and stored in the CAS
    // under this BLAKE3 hash. Peers pull it on demand when they open the owning work unit/decision.
    string TranscriptBlobHash,
    // Number of agent cycles captured in the published transcript.
    int CycleCount,
    // First non-empty line of the last cycle's assistant text, truncated — a label for the node
    // without an LLM summarization turn. Null when there was no assistant text to draw from.
    string? Label,
    DateTimeOffset PublishedAt,
    string? SessionId = null,
    // The decision/proposal this transcript was published for (the publish trigger). Lets a reader go
    // decision → reasoning; either may be set depending on the trigger.
    string? DecisionId = null,
    string? ProposalId = null,
    // Denormalized at publish time from the work unit's RepositoryId so the store routes this node to
    // the repo room (StudioNodeKind.RepoScopedKinds). Null falls back to the local "studio" room.
    string? RepositoryId = null);

/// <summary>
/// The bounded, peer-visible reasoning transcript stored as a CAS blob and referenced by a
/// <see cref="ConversationRef"/>. Carries the reasoning + tool INTENT per cycle; tool-result bodies
/// are deliberately dropped (derivable / re-runnable, and the high-volume part of the firehose).
/// </summary>
public sealed record PublishedReasoningTranscript(
    string WorkUnitId,
    string? SessionId,
    IReadOnlyList<PublishedReasoningCycle> Cycles);

public sealed record PublishedReasoningCycle(
    int CycleNumber,
    string AgentRole,
    string? AssistantText,
    IReadOnlyList<PublishedToolCall> ToolCalls,
    DateTimeOffset OccurredAt);

public sealed record PublishedToolCall(string Name, string InputPreview);
