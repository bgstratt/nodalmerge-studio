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

public enum FindingKind
{
    KnowledgeGuideline,
    PromptImprovement,
}

public enum FindingSource
{
    Deterministic,
    LlmScan,
    // Hand-curated findings brought in from another repo's export file (POST
    // /studio/findings/import) — always lands as Open, never pre-promoted, so it goes through the
    // same human review as anything else.
    Imported,
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
    // Set when a KnowledgeGuideline Finding is Promoted — the resulting global Constraint
    // ArtifactRef's id, so the review UI can link straight to the durable effect this Finding
    // produced. Always null for PromptImprovement findings — their promotion creates no artifact;
    // the durable effect is just this Finding's own Status=Promoted + TargetStage (see below),
    // read directly by the matching pipeline stage's agent loop.
    string? PromotedArtifactId = null,
    // Required for PromptImprovement findings, unused for KnowledgeGuideline. Scopes which
    // pipeline stage's agent loop(s) should see this guidance once Promoted — Title/Summary double
    // as both the review-UI text and the literal text appended to that stage's outgoing prompt.
    PipelineStage? TargetStage = null,
    // Phase 0 (plans/organizational-knowledge-and-workgroup-scope.md) — the repository this finding
    // belongs to, stamped at ProposeAsync from the workspace's seed repo (same registry RepositoryId
    // that WorkUnit/Decision carry, so a finding lands in the SAME repo room as the constraints it
    // relates to). Drives FindingV1 replication routing: non-null → repo/{RepositoryId} room (shared
    // with same-repo peers); null → local "studio" room. Workspace-aggregate findings (fork win
    // rates etc.) are attributed to the workspace's repo; genuinely cross-repo attribution is a
    // later refinement (Phase 1 workgroup reach).
    string? RepositoryId = null);
