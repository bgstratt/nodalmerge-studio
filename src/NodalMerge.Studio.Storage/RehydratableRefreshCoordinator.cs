using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.5 Part 1 (plans/cas-distribution-and-storage.md Phase 6) — see
// IStudioCacheRefreshCoordinator's own doc comment (NodalMerge.Studio.Core.Services) for why this
// exists.
//
// Partial partitioning by room, using the frozen StudioNodeKind.RepoScopedKinds set as the only
// signal (no bespoke per-room-type registry needed): a rehydratable that DECLARES its owned kind(s)
// via IRehydratable.RehydratedKinds is skipped when the room the pack just arrived on could not
// possibly hold that kind — a repo-scoped-kind owner skips "studio"/"workgroup" packs, and a
// declared non-repo-scoped-kind owner skips repo-room packs. A rehydratable that leaves
// RehydratedKinds at its default (empty — "undeclared") is always refreshed regardless of room,
// preserving the original fully-conservative behavior for every service that hasn't opted in.
//
// This existed originally as "refresh literally everything on every pack" (no partitioning at all);
// that version reliably (not merely occasionally) broke an existing, previously-passing test
// (RoomPerRepoTests' two-repo-rooms room-isolation scenario) once refreshed live. The ACTUAL root
// cause, found by bisecting down to a single deterministic repro and instrumenting
// GetBoundRepoRoomIdsAsync directly: RepositoryRegistryService.RehydrateAsync — an ordinary,
// undeclared (conservative-refresh) IRehydratable — re-reads StudioNodeKind.RepositoryV1 on every
// call. RepositoryV1 rows are meant to be strictly peer-local ("stays local by necessity" — see
// StudioNodeKind.RepoScopedKinds' own comment) — but a connected peer's own "studio" room is not
// actually private: RoomPeerClient joins room `options.RoomId` (hardcoded "studio") by opening a
// WebSocket directly to the remote host's OWN "studio" room, so once replication catches up, this
// peer's local engine view of "studio" also contains every OTHER peer's own RepositoryV1
// registrations. Re-running RehydrateAsync live (this slice's whole point) silently absorbed those
// other peers' rows into this peer's OWN registry cache — which BoundRepoRooms.GetBoundRepoRoomIdsAsync
// (consumed by RoomPeerClient's own membership loop) then read as "repos I'm bound to," making a peer
// bound only to R1 start actually JOINING and receiving R2's/R3's real room content too. Fixed at the
// source: RepositoryRegistryService.RefreshAsync is overridden to a no-op (see that class's own
// comment) — its cache is only ever safe to (re)populate once, at startup, before any room
// connection exists. This partitioning below is kept as an independently-worthwhile reduction of
// unnecessary cross-room work, not as a fix for that bug (it wasn't one — RepositoryV1 isn't
// repo-scoped, so this partitioning never touched RepositoryRegistryService's refresh either way).
//
// One failing service never blocks the rest: each RefreshAsync call is individually try/caught and
// logged, so a bug in one service's refresh path degrades only that service's freshness (it stays
// on its last-known state until the next successful refresh) instead of leaving every other
// service's cache stale too.
internal sealed class RehydratableRefreshCoordinator(
    IEnumerable<IRehydratable> rehydratables,
    RoomOptions roomOptions,
    ILogger<RehydratableRefreshCoordinator> logger) : IStudioCacheRefreshCoordinator
{
    // Belt-and-suspenders: RoomPeerClient's own _inboundApplyGate already serializes every inbound-
    // pack application (and therefore every call into this coordinator) across all of a peer's
    // joined rooms, so in practice this gate is never contended. Kept because this coordinator's
    // own IStudioCacheRefreshCoordinator contract doesn't (and shouldn't) assume every future caller
    // provides that same external serialization.
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task RefreshAfterInboundPackAsync(string roomId, CancellationToken cancellationToken = default)
    {
        // The workgroup room never carries any StudioNodeKind data at all — it's owned entirely by
        // IWorkgroupRepositoryDirectory/IWorkgroupGoalDirectory, which read/write it directly and
        // never go through IStudioNodeStore.ReadAllNodesAsync (what every IRehydratable's own
        // Refresh/RehydrateAsync calls). So no registered IRehydratable could ever find anything new
        // here — skip the entire sweep outright rather than pointlessly touching every one of them.
        if (string.Equals(roomId, roomOptions.EffectiveWorkgroupRoomId, StringComparison.Ordinal))
            return;

        var isRepoRoom = roomId.StartsWith("repo/", StringComparison.Ordinal);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await RefreshCoreAsync(roomId, isRepoRoom, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RefreshCoreAsync(string roomId, bool isRepoRoom, CancellationToken cancellationToken)
    {
        foreach (var rehydratable in rehydratables)
        {
            var kinds = rehydratable.RehydratedKinds;
            if (kinds.Count > 0)
            {
                var ownsRepoScopedKind = kinds.Any(StudioNodeKind.RepoScopedKinds.Contains);
                if (ownsRepoScopedKind != isRepoRoom)
                    continue; // declared kind(s) can't live in a room of this type — skip
            }

            try
            {
                await rehydratable.RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(
                    ex,
                    "[cache-refresh] {Service} failed to refresh after inbound pack room={Room} — its cache stays at its last-known state until the next successful refresh",
                    rehydratable.GetType().Name, roomId);
            }
        }
    }
}
