namespace NodalMerge.Studio.Contracts.Domain;

public enum ArtifactType
{
    Goal,
    Plan,
    Task,
    Research,
    Decision,
    Constraint,
    BranchChangeset,
    MergeProposal,
    MergeResult,
    ChangeIntent,
    ExternalChangeset,
    // A human reviewer's "Revise" restart — a compacted summary (goal, files touched, truncated
    // diff) of the almost-correct attempt being revised. Distinct from Constraint: this is
    // per-attempt context to build on, not an inherited rule the agent must obey.
    RevisionContext,
    // IReconciliationAgentService's audit trail — records which source proposals a reconciliation
    // work unit was created to fold together. Distinct from RevisionContext: that's one agent's own
    // almost-correct attempt being revised; this spans two-or-more independent goals' proposals.
    ReconciliationContext,
    // Phase A (harness-hosting-architecture.md) — a standalone retirement marker for
    // "supersedes X, no replacement content." Carries only Supersedes (+ Title/Body as the human
    // reason); distinct from a Decision/Constraint that supersedes an ancestor as a side effect of
    // recording its own new content. See EngineeringStateFact.SupersededBy — always derived by the
    // ProjectionType.EngineeringState fold from these forward Supersedes links, never stored back
    // onto the superseded artifact.
    Supersession,
}

public enum ArtifactStatus
{
    Active,
    Approved,
    Rejected,
    Superseded,
    Applied,
    Invalidated,
}

public sealed record ArtifactRef(
    string ArtifactId,
    ArtifactType Type,
    string? ParentArtifactId,
    ArtifactStatus Status,
    DateTimeOffset CreatedAt,
    string? OwnedByWorkUnitId,
    string? OwnedByAgentId,
    string? Title = null,
    string? Body = null,
    // Capability-gap fix — set on a descendant (in the ParentArtifactId chain) when an ancestor is
    // invalidated. Distinct from Status: the descendant's own status (e.g. a MergeProposal's
    // Applied) is left untouched, this only flags that something it was built on is now stale.
    string? InvalidatedByArtifactId = null,
    // Phase A — forward link: artifact IDs this one supersedes, set at creation time. The reverse
    // relation (SupersededBy) is never stored here — it's branch-relative (two branches can each
    // promote a different successor to the same artifact) and so can only be derived by walking a
    // chosen history, which is exactly what the EngineeringState projection fold does.
    IReadOnlyList<string>? Supersedes = null)
{
    public IReadOnlyList<string> Supersedes { get; init; } = Supersedes ?? [];
}
