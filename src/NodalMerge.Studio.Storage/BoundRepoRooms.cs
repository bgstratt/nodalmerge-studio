using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.3 (plans/cas-distribution-and-storage.md Phase 6, D1) — single source of truth for two
// closely related questions that must agree exactly across callers, or the membership rule breaks:
//   1. "Which repo rooms has this peer bound/materialized?" (D1's membership rule: "a peer joins
//      only rooms of repos it has bound/materialized" — consumed by RoomPeerClient's multi-room
//      join set and NodalMergeStudioNodeStore's ReadAllNodesAsync aggregation).
//   2. "Given a local-candidate RepositoryId, which room (if any) does it resolve to?" (consumed by
//      NodalMergeStudioNodeStore.WriteNodeAsync's repo-room routing and RuntimeGraphPromoter's
//      WorkUnit-completion safety net).
//
// "Bound" means: a local RepositoryV1 candidate (IRepositoryRegistryService) whose WorkgroupRepoId
// is non-null (6.2's binding outcome) — a repo still PendingDisambiguation, or one this peer has
// never even locally registered, is not bound and is never joined/routed to, exactly D2's
// never-guess stance.
public static class BoundRepoRooms
{
    // Re-exported for callers (e.g. RoomReplicationDispatcher, RoomPeerClient) that need the
    // workgroup room id alongside repo room ids — WorkgroupRepositoryDirectory.WorkgroupRoomId
    // remains the one place that literal is defined.
    public const string WorkgroupRoomId = WorkgroupRepositoryDirectory.WorkgroupRoomId;

    // docs/STUDIO_ROOM_SCHEMA.md (b) "repoRoomId naming" — repo/{repoId}, where repoId is the
    // workgroup-authoritative id (WorkgroupRepositoryEntry.RepoId / RepositoryV1.WorkgroupRepoId),
    // never the local-candidate RepositoryId directly (the two happen to be equal in the common
    // single-peer case per the preferred-id-continuity rule, but are not guaranteed to be).
    public static string RoomIdFor(string workgroupRepoId) => $"repo/{workgroupRepoId}";

    // Every workgroup repoId this peer has bound a local candidate to, deduplicated. Cheap: backed
    // by RepositoryRegistryService's in-memory ConcurrentDictionary, not a room walk.
    public static async Task<IReadOnlyList<string>> GetBoundWorkgroupRepoIdsAsync(
        IRepositoryRegistryService registry, CancellationToken ct = default)
    {
        var repos = await registry.ListAsync(ct).ConfigureAwait(false);
        return repos
            .Where(r => r.WorkgroupRepoId is not null)
            .Select(r => r.WorkgroupRepoId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static async Task<IReadOnlyList<string>> GetBoundRepoRoomIdsAsync(
        IRepositoryRegistryService registry, CancellationToken ct = default) =>
        (await GetBoundWorkgroupRepoIdsAsync(registry, ct).ConfigureAwait(false))
            .Select(RoomIdFor)
            .ToList();

    // Resolves a *local-candidate* RepositoryId (the id carried on WorkUnit/RepositorySnapshot/etc.
    // payloads) to its bound repo room id. Returns null — never throws — when repositoryId is
    // null/blank, the registry is unavailable, the candidate isn't registered, or it hasn't been
    // bound to the workgroup yet (PendingDisambiguation). Every caller's contract for a null result
    // is "fall back to the workspace-local room", matching RepositoryRegistryService's own
    // never-block-on-workgroup-binding stance (BindToWorkgroupAsync's own comment).
    public static async Task<string?> TryResolveRepoRoomIdAsync(
        IRepositoryRegistryService? registry, string? repositoryId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repositoryId) || registry is null)
            return null;

        var repository = await registry.GetAsync(repositoryId, ct).ConfigureAwait(false);
        return repository?.WorkgroupRepoId is { } workgroupRepoId ? RoomIdFor(workgroupRepoId) : null;
    }
}
