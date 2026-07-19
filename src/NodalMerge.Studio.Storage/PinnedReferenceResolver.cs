using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.3 (plans/cas-distribution-and-storage.md Phase 6, D3; docs/STUDIO_ROOM_SCHEMA.md (c)) —
// the pinned cross-repo reference triple (repoId, generationId, path) and its frozen resolution
// chain:
//
//   1. repo room  — repoId -> repoRoomId via the workgroup repositories map ((b); falls back to
//      the frozen "repo/{repoId}" naming when the directory has no entry, which is the same string
//      by the frozen contract — the map consult is kept anyway so a future repoRoomId scheme
//      change only touches the map, not this resolver).
//   2. generation node — generationId looked up in THAT room's snapshot/generation DAG (the same
//      identity space RepositorySnapshotId occupies; kind studio/repository-snapshot/v1) -> its
//      TreeHash. Deliberately reads the room directly (NodalMergeStudioNodeStore.
//      TryReadNodeFromRoomAsync), NOT via IRepositorySnapshotService — that service's in-memory
//      cache only covers repos this peer has locally synced, and the whole point of a pinned
//      reference (D3) is resolving on a peer that never cloned/materialized the referenced repo.
//   3. CAS walk — TreeHash -> tree objects -> walk along path -> file blob hash
//      (ISnapshotTreeResolver's scope-pruned resolve, so only the directories on path's spine are
//      fetched) -> IBlobStoreProvider.TryGetBlobAsync (the chained provider when configured, so
//      the bytes can come from the remote origin with no local clone).
//
// Returns null (never throws) for every "can't resolve" case — unknown generation, path not in
// the tree, CAS miss — logged distinctly so operators can tell which link broke.
//
// NOTE (explicitly out of scope, per the slice brief): IRepositoryRegistryService.ReadFileAsync's
// live-disk read is NOT replaced here — its callers (WorkUnit.ReferenceFiles context assembly)
// keep working off local disk. Migrating them onto pinned references is future work (a 6.5-family
// change: those call sites would need a generationId to pin against, which today's
// FileReferenceV1 doesn't carry).
public sealed record PinnedReference(string RepoId, string GenerationId, string Path);

public interface IPinnedReferenceResolver
{
    Task<byte[]?> ResolveAsync(PinnedReference reference, CancellationToken ct = default);
}

public sealed class PinnedReferenceResolver(
    IStudioNodeStore nodeStore,
    IWorkgroupRepositoryDirectory workgroupDirectory,
    ISnapshotTreeResolver treeResolver,
    IBlobStoreProvider? blobStore,
    ILogger<PinnedReferenceResolver> logger) : IPinnedReferenceResolver
{
    public async Task<byte[]?> ResolveAsync(PinnedReference reference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reference.RepoId)
            || string.IsNullOrWhiteSpace(reference.GenerationId)
            || string.IsNullOrWhiteSpace(reference.Path))
            return null;

        if (blobStore is null)
        {
            logger.LogWarning("pinned reference resolution requires a blob store — none configured");
            return null;
        }

        // Step 2 needs the room-targeted read only the engine-backed store has; the in-memory
        // test-double store has no rooms at all, so ordinary ReadNodeAsync is the honest equivalent
        // there (its dictionary IS the "room").
        var payloadJson = nodeStore is NodalMergeStudioNodeStore engineStore
            ? await engineStore.TryReadNodeFromRoomAsync(
                await ResolveRepoRoomIdAsync(reference.RepoId, ct).ConfigureAwait(false),
                StudioNodeKind.RepositorySnapshotV1, reference.GenerationId, ct).ConfigureAwait(false)
            : await nodeStore.ReadNodeAsync(StudioNodeKind.RepositorySnapshotV1, reference.GenerationId, ct).ConfigureAwait(false);

        if (payloadJson is null)
        {
            logger.LogInformation(
                "pinned reference generation node not found repoId={RepoId} generationId={GenerationId}",
                reference.RepoId, reference.GenerationId);
            return null;
        }

        RepositorySnapshot? snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<RepositorySnapshot>(payloadJson);
        }
        catch (JsonException ex)
        {
            logger.LogWarning(ex,
                "pinned reference generation node unparseable repoId={RepoId} generationId={GenerationId}",
                reference.RepoId, reference.GenerationId);
            return null;
        }

        if (snapshot is null)
            return null;

        // Step 3 — scope-pruned tree walk along the path's spine only.
        var entries = await treeResolver.ResolveTreeAsync(snapshot, [reference.Path], ct).ConfigureAwait(false);
        if (entries is null || !entries.TryGetValue(reference.Path, out var blobHash))
        {
            logger.LogInformation(
                "pinned reference path not present in generation tree repoId={RepoId} generationId={GenerationId} path={Path}",
                reference.RepoId, reference.GenerationId, reference.Path);
            return null;
        }

        var result = await blobStore.TryGetBlobAsync(blobHash, ct).ConfigureAwait(false);
        if (!result.Found || result.Bytes is null)
        {
            logger.LogWarning(
                "pinned reference blob missing from every configured store hash={Hash} repoId={RepoId} path={Path}",
                blobHash, reference.RepoId, reference.Path);
            return null;
        }

        return result.Bytes;
    }

    private async Task<string> ResolveRepoRoomIdAsync(string repoId, CancellationToken ct)
    {
        try
        {
            var entries = await workgroupDirectory.ListAsync(ct).ConfigureAwait(false);
            var entry = entries.FirstOrDefault(e => string.Equals(e.RepoId, repoId, StringComparison.Ordinal));
            if (entry is not null)
                return entry.RepoRoomId;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "workgroup repositories map lookup failed for repoId={RepoId} — using frozen naming", repoId);
        }

        return BoundRepoRooms.RoomIdFor(repoId);
    }
}
