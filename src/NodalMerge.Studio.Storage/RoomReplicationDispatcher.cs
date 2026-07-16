using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.3 (plans/cas-distribution-and-storage.md Phase 6) — the single registered
// IStudioNodeStoreReplicationSink. Pre-6.3 there was only ever one room ("studio") to replay after
// an inbound pack, so NodalMergeStudioNodeStore was registered directly as the sink. Now RoomPeerClient
// applies inbound packs for "studio", "workgroup", and every bound repo room, and each of those
// three room families is owned by a different collaborator (NodalMergeStudioNodeStore for "studio" +
// repo rooms per its own _repoRoomMaps cache; WorkgroupRepositoryDirectory for "workgroup") — this
// class is the one seam RoomPeerClient resolves, routing by roomId to whichever collaborator
// actually owns that room's EngineRoomMap.
internal sealed class RoomReplicationDispatcher(
    NodalMergeStudioNodeStore studioStore,
    IWorkgroupRepositoryDirectory workgroupDirectory) : IStudioNodeStoreReplicationSink
{
    public Task RehydrateLiveMapFromCanonicalResolutionAsync(string roomId, CancellationToken cancellationToken = default) =>
        string.Equals(roomId, BoundRepoRooms.WorkgroupRoomId, StringComparison.Ordinal)
            ? workgroupDirectory.ReplayCanonicalResolutionIntoLiveMapAsync(cancellationToken)
            : studioStore.RehydrateLiveMapFromCanonicalResolutionAsync(roomId, cancellationToken);
}
