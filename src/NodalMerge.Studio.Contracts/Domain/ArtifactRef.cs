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

// Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — the "reach" half of the 2x2
// scope model (Reach x Application): WHO gets a replicated copy. Application — WHICH repo it applies
// to — is the separate RepositoryId axis (null = all repos). Reach selects the room; injection then
// filters by RepositoryId. Null Reach means legacy routing (pre-Phase-1): route by RepositoryId
// alone (repo room if set, else the local "studio" room), so existing artifacts are unchanged with
// zero migration — only records that opt in by setting Reach get the new behavior.
public enum ArtifactReach
{
    // The peer-private "studio" room — never replicated (Slice 7.3). A user's own knowledge, or a
    // local override of a shared policy (with RepositoryId set = override scoped to one repo).
    Private,
    // Shared across the workgroup. With RepositoryId set → the repo/{id} room (peers on that repo);
    // with RepositoryId null → the workgroup room (every member, all repos — the old "global").
    Workgroup,
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
    IReadOnlyList<string>? Supersedes = null,
    // Slice 6.3a (plans/cas-distribution-and-storage.md Phase 6, D1) — denormalized at RecordAsync
    // time from OwnedByWorkUnitId's own RepositoryId. Null for global artifacts (OwnedByWorkUnitId
    // itself null — e.g. GetGlobalConstraintsAsync's constraint set), a work unit with no
    // resolvable RepositoryId, or an artifact that predates 6.3a. Phase 1: doubles as the
    // *application* filter (null = applies to all repos); combined with Reach it forms the 2x2 scope.
    string? RepositoryId = null,
    // Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — the reach half of the scope
    // model (see ArtifactReach). Null = legacy routing (route by RepositoryId alone); non-null opts
    // into (Reach x RepositoryId) routing. See ArtifactLineageService.RecordAsync.
    ArtifactReach? Reach = null,
    // Phase 1 follow-up (organizational-knowledge-and-workgroup-scope.md §8) — provenance for a global
    // constraint promoted from a work-unit-owned (lineage) one: the source artifact's id. Set only on
    // the promoted global copy; null everywhere else (manual adds, promoted findings, the lineage
    // source itself). Distinct from ParentArtifactId (which stays null on globals so no invalidation
    // cascade couples the promotion to the source): this is a pure back-reference used to (a) show
    // where a policy came from and (b) dedupe — a source is "already promoted" iff some global's
    // PromotedFromArtifactId equals it. Additive/nullable = zero migration, same as Reach above.
    string? PromotedFromArtifactId = null,
    // Item 3 (plans/vision-punchlist-remediation.md) — monotonic counter bumped on every scope change
    // (SetScopeAsync / ElevateToWorkgroupAsync). Re-scoping re-routes the artifact into a different
    // room but cannot delete the copy left in the old one (EngineRoomMap has no per-entry eviction),
    // so the same ArtifactId genuinely exists in two rooms with divergent payloads. The read fan-out
    // resolves that collision by highest ScopeVersion instead of room precedence — without it, every
    // *narrowing* re-scope (workgroup→repo, workgroup/repo→private) resolves to the stale broader
    // copy and silently reverts on other peers and after any rehydrate. Additive with a 0 default =
    // zero migration, same as Reach/RepositoryId/PromotedFromArtifactId above; artifacts that were
    // never re-scoped all sit at 0 and fall through to the pre-existing precedence order.
    int ScopeVersion = 0)
{
    public IReadOnlyList<string> Supersedes { get; init; } = Supersedes ?? [];
}
