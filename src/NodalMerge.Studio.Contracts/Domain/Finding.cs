namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Finding record stored under studio/finding/v1 — a detected pattern (from a deterministic scan
/// or an LLM scan) awaiting human Promote/Dismiss/Investigate review. Modeled on MergeProposal's
/// review lifecycle, but simpler: there's no build/apply step, just a durable effect on promotion.
/// </summary>
public enum FindingStatus
{
    Open,
    Promoted,
    Dismissed,
    Investigating,
}

// PromptImprovement is reserved for Phase 4 — kept here now so the pipeline doesn't need to
// reshape when that promotion action (editing an AgentProfile's SystemPrompt) is added.
public enum FindingKind
{
    KnowledgeGuideline,
    PromptImprovement,
}

public enum FindingSource
{
    Deterministic,
    LlmScan,
}

public sealed record Finding(
    string FindingId,
    FindingKind Kind,
    FindingSource Source,
    string Title,
    string Summary,
    string? SupportingDataJson,
    FindingStatus Status,
    DateTimeOffset CreatedAt,
    string? ReviewNotes = null,
    DateTimeOffset? ReviewedAt = null,
    // Set when Promoted — the resulting global Constraint ArtifactRef's id, so the review UI can
    // link straight to the durable effect this Finding produced.
    string? PromotedArtifactId = null);
