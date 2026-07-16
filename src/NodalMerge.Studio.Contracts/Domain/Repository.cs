namespace NodalMerge.Studio.Contracts.Domain;

// Slice 6.2 (plans/cas-distribution-and-storage.md Phase 6, D1/D2; docs/STUDIO_ROOM_SCHEMA.md (b))
// — matching hints for the workgroup repositories map, never identity (D2's core rule). A pure data
// shape (no process invocation here — that's IRepositoryIdentityHintsService in
// NodalMerge.Studio.Storage) so it can be embedded both in the workgroup map's own entries and in
// RepositoryV1.PendingDisambiguation's candidate list below, which lives in this Contracts project.
// RootShas/Remotes are sets (order-insensitive, no duplicates expected) — callers that need
// deterministic output order should sort explicitly; this record doesn't normalize order itself.
public sealed record RepositoryIdentityHints(
    IReadOnlyList<string> RootShas,
    IReadOnlyList<string> Remotes)
{
    public static readonly RepositoryIdentityHints Empty = new([], []);

    // Both hint components empty — no signal at all (shallow/no-remote/empty-repo clones per the
    // frozen schema doc's degraded-case list). Matching treats this as "no algorithm can know",
    // never a silent mint — see RepositoryIdentityMatcher.
    public bool IsDegraded => RootShas.Count == 0 && Remotes.Count == 0;
}

// One candidate offered to a caller resolving a NeedsDisambiguation match result (see
// NodalMerge.Studio.Storage.RepositoryMatchResult) — mirrors a workgroup repositories map entry
// without depending on Storage's WorkgroupRepositoryEntry type (Contracts sits below Storage).
public sealed record RepositoryDisambiguationCandidateV1(
    string RepoId,
    string? Label,
    RepositoryIdentityHints Hints);

// Persisted on a RepositoryV1 whose most recent workgroup-binding attempt came back ambiguous
// (docs/STUDIO_ROOM_SCHEMA.md (b) matching flow, step 4/7) — surfaced by the
// GET /studio/repositories/{id}/identity REST endpoint until resolved via
// POST .../identity/resolve (RepositoryRegistryService.ResolveDisambiguationAsync).
public sealed record RepositoryDisambiguationPendingV1(
    IReadOnlyList<RepositoryDisambiguationCandidateV1> Candidates);

// Capability-gap fix — informational registry of repository paths agents have referenced, so a
// "create a repository elsewhere" request can be discovered/registered instead of guessed at. Does
// not change which repository is actually seeded (WorkspaceOptions.SeedRepositoryPath) — Studio
// still manages exactly one active repository per instance until real multi-repository support
// exists.
//
// Slice 6.2: RepositoryId above remains this repo's permanent *local candidate* identity forever —
// per plans/cas-distribution-and-storage.md D2/6.2 ("today's RepositoryRegistryService minting
// repo-{guid} keyed by local path becomes 'local candidate pending workgroup registration', not the
// end of the story") existing rows are never rewritten. WorkgroupRepoId records the separate
// workgroup-authoritative repoId this local candidate has been bound to (docs/STUDIO_ROOM_SCHEMA.md
// (b)) — null until the first successful bind. In the common single-user/standalone case
// (RegisterAsync's preferred-id continuity rule — see RepositoryRegistryService) WorkgroupRepoId
// ends up equal to RepositoryId, so nothing observable changes for existing single-peer workspaces.
// PendingDisambiguation is set instead of WorkgroupRepoId when the most recent bind attempt
// couldn't resolve automatically (fork ambiguity with no remote tiebreak, or genuinely degraded
// hints against more than zero registered candidates) — see RepositoryMatchResult.NeedsDisambiguation.
public sealed record RepositoryV1(
    string RepositoryId,
    string Path,
    string? Label,
    DateTimeOffset RegisteredAt,
    string? WorkgroupRepoId = null,
    RepositoryDisambiguationPendingV1? PendingDisambiguation = null);

// Cross-repo file reference — a pointer to a file in a *different* registered repository than the
// one a work unit is actually seeded from, used for read-only context (style/examples) during a
// run. Distinct from WorkUnit.FileScope, which gates which files an agent may write to.
public sealed record FileReferenceV1(string RepositoryId, string Path, string? Note = null);

// Slice 7.2 (plans/cas-distribution-and-storage.md Phase 7) — thrown when a repository's
// local-candidate RepositoryId (as carried by WorkUnit.RepositoryId, MergeProposal.RepositoryId,
// etc. — per 6.3a's denormalization, possibly FOREIGN: replicated from a different peer that
// minted it) cannot be bound to any CAS/snapshot identity on THIS peer: neither an already-existing
// snapshot chain under a known local disk path, nor a workgroup binding (this peer's own registry
// entry, or a foreign RepositoryV1 row replicated via the shared "studio" room) resolves it.
// Distinct from an ordinary "path not found in the latest snapshot" 404 — that means the repository
// resolved fine and the specific file genuinely isn't in it; this means the repository itself
// couldn't be identified at all, which needs registration/disambiguation, not a retry. Replaces the
// pre-7.2 failure mode where an unresolvable identity silently fell back to a physically-unrelated
// default repository (or a bare `false`) with no indication why.
public sealed class RepositoryIdentityUnresolvedException(string repositoryId)
    : Exception(
        $"Repository '{repositoryId}' could not be bound to a local repository or workgroup " +
        "binding on this peer. Register the repository locally (POST /studio/repositories) and " +
        "resolve its workgroup identity (POST /studio/repositories/{repositoryId}/identity/resolve) " +
        "before retrying.")
{
    public string RepositoryId { get; } = repositoryId;
}
