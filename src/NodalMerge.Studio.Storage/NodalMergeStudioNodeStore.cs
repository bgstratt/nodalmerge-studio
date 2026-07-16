using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.1a (plans/cas-distribution-and-storage.md Phase 6, docs/STUDIO_ROOM_SCHEMA.md (a)) —
// Studio state rides the embedded engine's CRDT DAG instead of writing AcceptedNodeRecords to
// INodeStoreProvider directly. Every StudioNodeKind is multiplexed into one engine map namespace
// ("studio", room "studio") via MapSet/MapGet/MapAll, keyed "{kind}/{entityId}" per the frozen
// schema. Durability rides RuntimeDagPersistenceService's existing pack-persistence machinery
// (the same one RuntimeWebSocketLoopRunner uses for WS-driven writes) rather than a new format.
//
// IMPORTANT engine-behavior note (discovered empirically while building this class, not documented
// anywhere else): MapSet/MapGet/MapAll operate on the engine's live "room_maps" state, which has NO
// persistence/hydrate story of its own. RequestServerPack/ImportPack (what
// RuntimeDagPersistenceService.PersistRoomSnapshotAsync/HydrateRoomIfNeededAsync actually drive)
// instead export/import the room's separate CRDT *sync graph*, and a MapSet mutation only reaches
// that graph once a checkpoint containing it is explicitly turned into a graph node via
// `PromoteCheckpointToGraph{selector:"latest"}` — the same command RuntimeGraphPromoter already
// issues every 30 s. Two consequences this class handles itself, using only existing engine
// commands (no new persistence format):
//   1. Write path promotes-then-persists: every WriteNodeAsync (and the migration's writes) calls
//      PromoteCheckpointToGraph before PersistRoomSnapshotAsync, otherwise the persisted pack never
//      contains the mutation at all (verified: without the promote step, RequestServerPack keeps
//      returning the exact same bytes/hash regardless of intervening MapSets).
//   2. Read path replays canonical resolution into the live map after hydrate: importing a persisted
//      pack (HydrateRoomIfNeededAsync -> ImportPack) only repopulates the sync graph, not
//      "room_maps" — so immediately after hydrate, MapGet/MapAll would see nothing at all despite
//      the graph holding every promoted checkpoint. EnsureInitializedAsync bridges this by querying
//      GetCanonicalResolution (which *does* resolve correctly off the hydrated graph) once per
//      startup and replaying every non-"_"-prefixed entry back through MapSet before anything else
//      (including migration, which depends on MapGet seeing prior state) runs. See
//      ReplayCanonicalResolutionIntoLiveMap's own comment for why namespace can be inferred safely.
// 6.1b (shipped): the promote-then-persist / replay-after-hydrate pairing above is now also the
// pattern for bidirectional room replication — RoomPeerClient reuses ReplayCanonicalResolutionInto
// LiveMap (exposed via IStudioNodeStoreReplicationSink) after applying an inbound pack, and
// PromoteLatestCheckpointToGraph now returns the promoted node's own id so the outbound seam
// (IStudioReplicationOutbound, called below) can hand RoomPeerClient exactly the node to push
// upstream via HostCommand::MstDone, instead of re-deriving a delta. RuntimeGraphPromoter's 30 s
// broadcast tick (StudioCrdtSyncBackgroundService) is retired; its promote call now routes through
// the same IStudioReplicationOutbound seam this class uses, so the two no longer duplicate
// broadcast logic — see RuntimeGraphPromoter's own comment.
//
// Legacy rows (payload kind "studio", node id "studio:{kind}:{entityId}:{ticksD20}", written by the
// pre-6.1a version of this class) are migrated into the engine map exactly once per workspace (a
// migration marker lives at namespace "studio-meta" key "migrated-v1" — a distinct namespace, not a
// key inside the "studio" namespace, so it can never collide with the kind keyspace and needs no
// special-casing in the (a) key-parsing rule) and are never rewritten or deleted (AP-5): a fallback
// read path against the legacy rows covers anything not present in the engine map.
//
// Slice 6.2: the low-level room_maps/sync-graph bridging (promote-then-persist, replay-after-
// hydrate, outbound notify) is now EngineRoomMap, extracted from this class's original private
// methods so the workgroup room's repositories map (WorkgroupRepositoryDirectory) can reuse it
// instead of re-deriving the same engine quirks. This class keeps everything studio-specific: the
// "studio"/"studio-meta" two-namespace-in-one-room split (the namespace-resolver lambda passed to
// EngineRoomMap's constructor below), the legacy-row migration, and the legacy fallback read path.
//
// Slice 6.3 (room-per-repo, D1) — retires the hardcoded single "studio" room for repo-scoped state.
// StudioNodeKind.RepoScopedKinds now route to a *separate* EngineRoomMap per bound repo
// (repo/{workgroupRepoId}, one instance per repoId cached in _repoRoomMaps, lazily created and
// hydrated on first touch — see GetOrCreateRepoRoomMapAsync). Everything else keeps living in the
// original "studio" room instance (_roomMap below), which is now the peer's local/workspace room,
// not "the" room. Three mechanical notes:
//   1. Write-time routing is explicit (IStudioNodeStore.WriteNodeAsync's new repositoryId overload)
//      — repo-scoped callers already have the entity's own RepositoryId in scope at the call site
//      (it's a field on the object being serialized), so there's no ambient/inferred state here.
//      repositoryId is the *local-candidate* id; TryResolveRepoRoomIdCoreAsync resolves it to the
//      workgroup-bound room by reading this store's OWN persisted RepositoryV1 rows (see the
//      binding-resolution NOTE on the fields below for why the registry service is deliberately
//      not consulted).
//   2. Read-time routing is aggregation, not lookup: ReadNodeAsync/ReadAllNodesAsync for a
//      repo-scoped kind fan out across the local "studio" room (legacy/never-migrated rows) AND
//      every currently-bound repo room (BoundRepoRooms.GetBoundWorkgroupRepoIdsAsync), because the
//      3-arg IStudioNodeStore interface carries no repositoryId for reads. This is the aggregation
//      the slice brief calls for explicitly; the number of bound repos per peer is expected to stay
//      small, so a linear fan-out is an acceptable cost for correctness over a bespoke index.
//   3. Migration extracts repositoryId from the payload JSON (design option (b)) rather than
//      threading it through — legacy rows have no live caller to ask, so the one-shot migration
//      pass parses each row's own "RepositoryId" JSON property (verified present, with that exact
//      C# PascalCase name, on every StudioNodeKind.RepoScopedKinds record — see that constant's own
//      comment) and resolves it the same way writes do.
public sealed class NodalMergeStudioNodeStore : IStudioNodeStore, IStudioNodeStoreReplicationSink
{
    // Engine room every workspace-global/not-yet-repo-scoped StudioNodeKind is still multiplexed
    // into. Kept internal (not referenced elsewhere in this assembly today) only because pre-6.1a
    // code exposed it the same way. Slice 6.3: also the fallback room for repo-scoped kinds whose
    // repositoryId doesn't (yet) resolve to a bound workgroup room, and the legacy-row source every
    // repo-scoped-kind read still falls back to.
    internal const string StudioRoomId = "studio";

    // Engine map namespace for ordinary studio-node entries, per STUDIO_ROOM_SCHEMA.md (a).
    private const string MapNamespace = "studio";

    // Separate namespace (not a key under MapNamespace) for the one-shot migration marker — cleaner
    // than a reserved key inside the kind keyspace, and structurally impossible to collide with any
    // "{kind}/{entityId}" key since MapAll("studio") never sees it.
    private const string MetaNamespace = "studio-meta";
    private const string MigrationMarkerKey = "migrated-v1";

    // Slice 6.3 — per-repo-room migration marker keys, "repo-migrated/{roomId}" (e.g.
    // "repo-migrated/repo/repo-3fa8..."), in the same MetaNamespace. See
    // MigrateStudioEntriesIntoRepoRoomAsync.
    private const string RepoRoomMigrationMarkerKeyPrefix = "repo-migrated/";

    // Legacy AcceptedNodeRecord shape this class wrote pre-6.1a: PayloadKind "studio", node id
    // "studio:{kind}:{entityId}:{ticksD20}".
    private const string LegacyPayloadKind = "studio";
    private const string LegacyRoomPrefix = "studio:";

    // Trailing ":" + 20-digit tick suffix is always exactly 21 characters — mirrors the pre-6.1a
    // ReadAllNodesAsync's own stripping rule, which is what lets entityId itself contain "/" or ":".
    private const int LegacyTickSuffixLength = 21;

    // The closed, compile-time-known StudioNodeKind set, longest-first, per STUDIO_ROOM_SCHEMA.md
    // (a)'s parsing rule. Built via reflection (rather than a hand-maintained copy, like the vector
    // test deliberately keeps for provability) so this list can never drift from the real one.
    private static readonly IReadOnlyList<string> KnownKinds = typeof(StudioNodeKind)
        .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
        .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .OrderByDescending(k => k.Length)
        .ToArray();

    private readonly EngineRoomMap _roomMap;
    private readonly INodeStoreProvider _legacyNodeStore;
    private readonly ILogger<NodalMergeStudioNodeStore> _logger;

    // Slice 6.3 — the four collaborators every per-repo EngineRoomMap needs, stashed so
    // GetOrCreateRepoRoomMapAsync can mint one per bound repoId on demand (EngineRoomMap itself is
    // stateless-per-room-id, just constructed once and reused — see that class's own comment).
    private readonly IRuntimeCommandBridge _bridge;
    private readonly RuntimeDagPersistenceService _dagPersistence;
    private readonly IStudioReplicationOutbound _replicationOutbound;

    // repoRoomId ("repo/{workgroupRepoId}") -> hydrated EngineRoomMap. Lazily populated; the
    // Lazy<Task<>> wrapper (ExecutionAndPublication) ensures concurrent first-touches for the same
    // repo don't race to hydrate twice, mirroring RuntimeDagPersistenceService's own per-room cache.
    private readonly ConcurrentDictionary<string, Lazy<Task<EngineRoomMap>>> _repoRoomMaps = new(StringComparer.Ordinal);

    // NOTE (6.3, learned the hard way): binding resolution here deliberately does NOT go through
    // IRepositoryRegistryService. The registry's in-memory cache is only populated by RegisterAsync
    // calls or by startup rehydration (StudioStateRehydrationService -> RehydrateAsync), and
    // rehydration ORDER between IRehydratable services is unspecified — a service rehydrating its
    // own cache via ReadAllNodesAsync(repo-scoped kind) before the registry rehydrated would see an
    // empty bound set and silently miss every repo-room entity. Instead, this store derives
    // bindings from its own persisted RepositoryV1 rows (kind "studio/repository/v1" — the exact
    // rows the registry itself rehydrates from), which are by construction available the moment
    // this store is initialized. This also removes any IStudioNodeStore <->
    // IRepositoryRegistryService dependency cycle concern outright.

    // Guards one-shot hydrate + legacy migration so every public method can call
    // EnsureInitializedAsync unconditionally without racing itself. Scoped to this singleton
    // instance (one engine room, "studio") — no per-room dictionary needed, unlike
    // RuntimeDagPersistenceService which serves many rooms.
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    public NodalMergeStudioNodeStore(
        IRuntimeCommandBridge bridge,
        RuntimeDagPersistenceService dagPersistence,
        INodeStoreProvider legacyNodeStore,
        IStudioReplicationOutbound replicationOutbound,
        ILogger<NodalMergeStudioNodeStore> logger)
    {
        // ReplayNamespaceFor: canonical-resolution entries carry no namespace of their own (see
        // ReplayCanonicalResolutionIntoLiveMap's original comment, now on EngineRoomMap) — this room
        // is the sole writer into two namespaces ("studio" and "studio-meta"), and the only keys it
        // ever puts outside MapNamespace are the well-known MigrationMarkerKey literal and (6.3) the
        // per-repo-room "repo-migrated/{roomId}" markers, none of which can collide with a
        // "{kind}/{entityId}" key (every StudioNodeKind constant starts with "studio/", so
        // BuildKey's output never starts with "migrated-v1" or "repo-migrated/").
        _roomMap = new EngineRoomMap(
            StudioRoomId,
            key => string.Equals(key, MigrationMarkerKey, StringComparison.Ordinal)
                   || key.StartsWith(RepoRoomMigrationMarkerKeyPrefix, StringComparison.Ordinal)
                ? MetaNamespace
                : MapNamespace,
            bridge, dagPersistence, replicationOutbound, logger);
        _legacyNodeStore = legacyNodeStore;
        _logger = logger;
        _bridge = bridge;
        _dagPersistence = dagPersistence;
        _replicationOutbound = replicationOutbound;
    }

    // Slice 6.3 — every repo room uses the same single-namespace shape ("studio", no "-meta" split;
    // migration markers are a workspace-level concept and live in the local "studio" room's
    // MetaNamespace, never inside a repo room). First creation of a room also runs the one-shot
    // per-room migration of pre-6.3 repo-scoped entries out of the "studio" engine map — see
    // MigrateStudioEntriesIntoRepoRoomAsync.
    private Task<EngineRoomMap> GetOrCreateRepoRoomMapAsync(string repoRoomId, CancellationToken cancellationToken) =>
        _repoRoomMaps.GetOrAdd(repoRoomId, id => new Lazy<Task<EngineRoomMap>>(async () =>
        {
            var roomMap = new EngineRoomMap(id, _ => MapNamespace, _bridge, _dagPersistence, _replicationOutbound, _logger);
            await roomMap.HydrateAndReplayAsync(cancellationToken).ConfigureAwait(false);
            await MigrateStudioEntriesIntoRepoRoomAsync(roomMap, cancellationToken).ConfigureAwait(false);
            return roomMap;
        }, LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    // Slice 6.3 migration, part 2 of 2 (part 1 — routing of pre-6.1a legacy AcceptedNodeRecord rows
    // — lives in MigrateLegacyRowsIfNeededAsync below): existing 6.1a+ workspaces hold repo-scoped
    // entries in the "studio" room's engine map, written before repo rooms existed. Following the
    // 6.1a precedent exactly: one-shot (marker-guarded, durable across restarts), copy-never-move
    // (the "studio"-room entries are never rewritten or deleted — AP-5; the fallback read in
    // ReadNodeAsync/ReadAllNodesAsync keeps them reachable for any repo that never resolves a
    // binding), bounded by the binding cache (only entries whose payload RepositoryId resolves —
    // via the registry rows carrying WorkgroupRepoId — to exactly this room are copied).
    //
    // Lazy-per-room rather than one global pass, deliberately: a global pass at store-init time
    // would race registry rehydration (the registry itself reads through this store, so at first
    // init its binding cache is still empty and nothing would resolve — a global marker written
    // then would permanently skip everything). Running at room-creation time instead means the
    // binding that led here is by construction already known, and a repo bound long after the
    // workspace upgraded still gets its entries migrated on first touch.
    private async Task MigrateStudioEntriesIntoRepoRoomAsync(EngineRoomMap repoRoomMap, CancellationToken cancellationToken)
    {
        var markerKey = RepoRoomMigrationMarkerKeyPrefix + repoRoomMap.RoomId;
        if (_roomMap.TryGetValueRawJson(MetaNamespace, markerKey) is not null)
            return;

        var migratedCount = 0;
        {
            // Every *local-candidate* RepositoryId bound to exactly this room (more than one local
            // candidate may bind to the same workgroup repo) — derived from this store's own
            // RepositoryV1 rows, same reasoning as the class-comment NOTE on binding resolution.
            var localIds = new HashSet<string>(StringComparer.Ordinal);
            var repoRowPrefix = StudioNodeKind.RepositoryV1 + "/";
            foreach (var (repoRowKey, repoRowValueRawJson) in _roomMap.AllEntries(MapNamespace))
            {
                if (!repoRowKey.StartsWith(repoRowPrefix, StringComparison.Ordinal))
                    continue;
                var workgroupRepoId = TryExtractStringProperty(ExtractPayloadRawText(repoRowValueRawJson), "WorkgroupRepoId");
                if (workgroupRepoId is not null
                    && string.Equals(BoundRepoRooms.RoomIdFor(workgroupRepoId), repoRoomMap.RoomId, StringComparison.Ordinal))
                    localIds.Add(repoRowKey[repoRowPrefix.Length..]);
            }

            foreach (var row in await LoadLatestLegacyRowsAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!string.Equals(row.Kind, StudioNodeKind.RepositoryV1, StringComparison.Ordinal))
                    continue;
                var workgroupRepoId = TryExtractStringProperty(row.PayloadJson, "WorkgroupRepoId");
                if (workgroupRepoId is not null
                    && string.Equals(BoundRepoRooms.RoomIdFor(workgroupRepoId), repoRoomMap.RoomId, StringComparison.Ordinal))
                    localIds.Add(row.EntityId);
            }

            foreach (var (key, valueRawJson) in _roomMap.AllEntries(MapNamespace))
            {
                string? kind = null;
                string? repositoryId = null;
                try
                {
                    using var envelopeDoc = JsonDocument.Parse(valueRawJson);
                    if (envelopeDoc.RootElement.TryGetProperty("kind", out var kindEl) && kindEl.ValueKind == JsonValueKind.String)
                        kind = kindEl.GetString();
                    if (kind is null || !StudioNodeKind.RepoScopedKinds.Contains(kind))
                        continue;
                    if (envelopeDoc.RootElement.TryGetProperty("payload", out var payloadEl)
                        && payloadEl.ValueKind == JsonValueKind.Object
                        && payloadEl.TryGetProperty("RepositoryId", out var repoIdEl)
                        && repoIdEl.ValueKind == JsonValueKind.String)
                        repositoryId = repoIdEl.GetString();

                    if (repositoryId is null || !localIds.Contains(repositoryId))
                        continue;
                    if (repoRoomMap.TryGetValueRawJson(MapNamespace, key) is not null)
                        continue; // never clobber — same partial-migration defensiveness as 6.1a.

                    var status = repoRoomMap.Set(MapNamespace, key, envelopeDoc.RootElement);
                    if (status == AsStatus.Ok)
                        migratedCount += 1;
                    else
                        _logger.LogWarning(
                            "studio node store repo-room migration MapSet failed room={Room} key={Key} status={Status}",
                            repoRoomMap.RoomId, key, status);
                }
                catch (JsonException)
                {
                    // Not an envelope this store wrote — skip, same tolerance as the 6.1a migration.
                }
            }

            if (migratedCount > 0)
            {
                var (promoteStatus, promotedNodeIdHex) = repoRoomMap.PromoteLatestCheckpointToGraph();
                if (promoteStatus != AsStatus.Ok)
                {
                    _logger.LogWarning(
                        "studio node store repo-room migration checkpoint promotion failed room={Room} status={Status}",
                        repoRoomMap.RoomId, promoteStatus);
                }

                await repoRoomMap.PersistAndReplicateAsync(promotedNodeIdHex, cancellationToken).ConfigureAwait(false);
            }
        }

        var markerStatus = _roomMap.Set(MetaNamespace, markerKey, new
        {
            v = 1,
            migratedAtUtc = DateTimeOffset.UtcNow,
            entriesMigrated = migratedCount
        });
        if (markerStatus == AsStatus.Ok)
        {
            var (studioPromoteStatus, studioPromotedNodeIdHex) = _roomMap.PromoteLatestCheckpointToGraph();
            if (studioPromoteStatus == AsStatus.Ok)
                await _roomMap.PersistAndReplicateAsync(studioPromotedNodeIdHex, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "studio node store repo-room migration complete room={Room} entries_migrated={Migrated}",
            repoRoomMap.RoomId, migratedCount);
    }

    public Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default) =>
        WriteToRoomAsync(_roomMap, kind, entityId, payloadJson, cancellationToken);

    // Slice 6.3 — IStudioNodeStore's repo-room routing overload. repositoryId is the *local-candidate*
    // id (see StudioNodeKind.RepoScopedKinds' own comment); BoundRepoRooms resolves it to the bound
    // workgroup room. Falls back to the local "studio" room room when repositoryId is null/blank or
    // doesn't (yet) resolve to a bound room — never blocks a write on workgroup binding, matching
    // RepositoryRegistryService's own degrade-gracefully stance.
    public async Task WriteNodeAsync(
        string kind, string entityId, string payloadJson, string? repositoryId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var repoRoomId = await TryResolveRepoRoomIdCoreAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        if (repoRoomId is null)
        {
            await WriteNodeAsync(kind, entityId, payloadJson, cancellationToken).ConfigureAwait(false);
            return;
        }

        var repoRoomMap = await GetOrCreateRepoRoomMapAsync(repoRoomId, cancellationToken).ConfigureAwait(false);
        await WriteToRoomAsync(repoRoomMap, kind, entityId, payloadJson, cancellationToken).ConfigureAwait(false);
    }

    // Core binding resolution off this store's own RepositoryV1 rows (see the class-comment NOTE
    // for why the registry is deliberately not consulted). No init gate — callable both from
    // public methods (already initialized) and from inside EnsureInitializedAsync's own migration
    // (init gate held; a re-entrant EnsureInitializedAsync call would deadlock the SemaphoreSlim).
    private async Task<string?> TryResolveRepoRoomIdCoreAsync(string? repositoryId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(repositoryId))
            return null;

        var payloadJson = await ReadStudioRoomOrLegacyPayloadAsync(
            StudioNodeKind.RepositoryV1, repositoryId, cancellationToken).ConfigureAwait(false);
        if (payloadJson is null)
            return null;

        var workgroupRepoId = TryExtractStringProperty(payloadJson, "WorkgroupRepoId");
        return workgroupRepoId is null ? null : BoundRepoRooms.RoomIdFor(workgroupRepoId);
    }

    // Every bound repo room per this store's own RepositoryV1 rows — same source of truth the
    // registry rehydrates from, minus the rehydration-ordering hazard. RepositoryV1 is not itself
    // a repo-scoped kind (see StudioNodeKind.RepoScopedKinds' comment — it's the row that MAPS a
    // candidate to a room), so this never recurses back into repo-room reads.
    private async Task<IReadOnlyList<string>> GetBoundRepoRoomIdsCoreAsync(CancellationToken cancellationToken)
    {
        var rooms = new HashSet<string>(StringComparer.Ordinal);
        var prefix = StudioNodeKind.RepositoryV1 + "/";

        foreach (var (mapKey, valueRawJson) in _roomMap.AllEntries(MapNamespace))
        {
            if (!mapKey.StartsWith(prefix, StringComparison.Ordinal))
                continue;
            var workgroupRepoId = TryExtractStringProperty(ExtractPayloadRawText(valueRawJson), "WorkgroupRepoId");
            if (workgroupRepoId is not null)
                rooms.Add(BoundRepoRooms.RoomIdFor(workgroupRepoId));
        }

        foreach (var row in await LoadLatestLegacyRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!string.Equals(row.Kind, StudioNodeKind.RepositoryV1, StringComparison.Ordinal))
                continue;
            var workgroupRepoId = TryExtractStringProperty(row.PayloadJson, "WorkgroupRepoId");
            if (workgroupRepoId is not null)
                rooms.Add(BoundRepoRooms.RoomIdFor(workgroupRepoId));
        }

        return rooms.ToList();
    }

    // Studio-room-then-legacy read of a single (kind, entityId) payload, with no init gate and no
    // repo-room fan-out — the primitive both core helpers above build on.
    private async Task<string?> ReadStudioRoomOrLegacyPayloadAsync(string kind, string entityId, CancellationToken cancellationToken)
    {
        var envelopeRawJson = _roomMap.TryGetValueRawJson(MapNamespace, BuildKey(kind, entityId));
        if (envelopeRawJson is not null)
            return ExtractPayloadRawText(envelopeRawJson);

        foreach (var row in await LoadLatestLegacyRowsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (string.Equals(row.Kind, kind, StringComparison.Ordinal)
                && string.Equals(row.EntityId, entityId, StringComparison.Ordinal))
                return row.PayloadJson;
        }

        return null;
    }

    private static string? TryExtractStringProperty(string json, string propertyName)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty(propertyName, out var el)
                && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task WriteToRoomAsync(
        EngineRoomMap roomMap, string kind, string entityId, string payloadJson, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var key = BuildKey(kind, entityId);

        using var payloadDoc = JsonDocument.Parse(payloadJson);
        var status = await roomMap.WriteEntryAsync(
            MapNamespace, key, new { v = 1, kind, payload = payloadDoc.RootElement }, cancellationToken)
            .ConfigureAwait(false);
        if (status != AsStatus.Ok)
        {
            _logger.LogWarning(
                "studio node store MapSet failed room={Room} kind={Kind} entityId={EntityId} status={Status}",
                roomMap.RoomId, kind, entityId, status);
            throw new InvalidOperationException(
                $"studio node store MapSet failed for room={roomMap.RoomId} kind={kind} entityId={entityId} status={status}");
        }
    }

    public async Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var key = BuildKey(kind, entityId);

        // Slice 6.3 — a repo-scoped kind may live in one of the peer's bound repo rooms instead of
        // "studio". The 3-arg interface carries no repositoryId, so this fans out across every
        // bound room (BoundRepoRooms) rather than looking one up — see the class comment's note 2.
        // Repo rooms are consulted BEFORE "studio": after the one-shot repo-room migration, the
        // repo-room entry is the live one and any remaining "studio"-room copy is frozen at
        // migration time (never rewritten, AP-5), so the repo room must win.
        if (StudioNodeKind.RepoScopedKinds.Contains(kind))
        {
            foreach (var repoRoomId in await GetBoundRepoRoomIdsAsync(cancellationToken).ConfigureAwait(false))
            {
                var repoRoomMap = await GetOrCreateRepoRoomMapAsync(repoRoomId, cancellationToken).ConfigureAwait(false);
                var repoEnvelopeRawJson = repoRoomMap.TryGetValueRawJson(MapNamespace, key);
                if (repoEnvelopeRawJson is not null)
                    return ExtractPayloadRawText(repoEnvelopeRawJson);
            }
        }

        var envelopeRawJson = _roomMap.TryGetValueRawJson(MapNamespace, key);
        if (envelopeRawJson is not null)
            return ExtractPayloadRawText(envelopeRawJson);

        // Fallback — anything not (yet) present in any engine map. Under normal operation the
        // one-shot migration already covers every legacy row, so this only matters defensively
        // (STUDIO_ROOM_SCHEMA.md (a): legacy rows are never rewritten and must stay readable).
        var legacyRows = await LoadLatestLegacyRowsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in legacyRows)
        {
            if (string.Equals(row.Kind, kind, StringComparison.Ordinal)
                && string.Equals(row.EntityId, entityId, StringComparison.Ordinal))
                return row.PayloadJson;
        }

        return null;
    }

    public async Task<IReadOnlyList<(string EntityId, string PayloadJson)>> ReadAllNodesAsync(
        string kind, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var prefix = kind + "/";
        var results = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (mapKey, valueRawJson) in _roomMap.AllEntries(MapNamespace))
        {
            if (!mapKey.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            results[mapKey[prefix.Length..]] = ExtractPayloadRawText(valueRawJson);
        }

        // Slice 6.3 — aggregate across every bound repo room too (see class comment note 2).
        // Assigned via the indexer (not TryAdd) so a repo-room entry OVERWRITES any same-entity
        // "studio"-room copy collected above: after the one-shot repo-room migration, the repo-room
        // entry is the live one and the "studio" copy is frozen at migration time (AP-5 — never
        // rewritten, so it can only ever be equal-or-older).
        if (StudioNodeKind.RepoScopedKinds.Contains(kind))
        {
            foreach (var repoRoomId in await GetBoundRepoRoomIdsAsync(cancellationToken).ConfigureAwait(false))
            {
                var repoRoomMap = await GetOrCreateRepoRoomMapAsync(repoRoomId, cancellationToken).ConfigureAwait(false);
                foreach (var (mapKey, valueRawJson) in repoRoomMap.AllEntries(MapNamespace))
                {
                    if (!mapKey.StartsWith(prefix, StringComparison.Ordinal))
                        continue;

                    results[mapKey[prefix.Length..]] = ExtractPayloadRawText(valueRawJson);
                }
            }
        }

        // Fallback — legacy entities of this kind no engine map has (see ReadNodeAsync).
        var legacyRows = await LoadLatestLegacyRowsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in legacyRows)
        {
            if (string.Equals(row.Kind, kind, StringComparison.Ordinal))
                results.TryAdd(row.EntityId, row.PayloadJson);
        }

        return results.Select(kv => (EntityId: kv.Key, PayloadJson: kv.Value)).ToList();
    }

    // Slice 6.3 — targeted single-room read for callers that already know exactly which repo room
    // an entity lives in (PinnedReferenceResolver: the pinned triple carries the repoId, so its
    // generation-node lookup goes straight to repo/{repoId} per the frozen resolution chain,
    // instead of the bound-set fan-out ReadNodeAsync does for room-agnostic callers). Works for a
    // room this peer has never locally bound (no registry entry required) — the room's state just
    // has to exist locally (persisted packs from replication, or seeded directly in tests).
    // Returns the payload JSON or null; never consults the legacy "studio" fallback (a pinned
    // reference is a post-6.3 concept — its target room is authoritative).
    internal async Task<string?> TryReadNodeFromRoomAsync(
        string roomId, string kind, string entityId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var roomMap = string.Equals(roomId, StudioRoomId, StringComparison.Ordinal)
            ? _roomMap
            : await GetOrCreateRepoRoomMapAsync(roomId, cancellationToken).ConfigureAwait(false);

        var envelopeRawJson = roomMap.TryGetValueRawJson(MapNamespace, BuildKey(kind, entityId));
        return envelopeRawJson is null ? null : ExtractPayloadRawText(envelopeRawJson);
    }

    private async Task<IReadOnlyList<string>> GetBoundRepoRoomIdsAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetBoundRepoRoomIdsCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "studio node store failed to enumerate bound repo rooms — repo-scoped reads may be incomplete this call");
            return [];
        }
    }

    internal static string BuildKey(string kind, string entityId) => $"{kind}/{entityId}";

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            // EngineRoomMap.HydrateAndReplayAsync both hydrates the sync graph from persisted packs
            // and bridges the room_maps gap (see EngineRoomMap's class comment, quirk 2) by
            // replaying GetCanonicalResolution's entries back through MapSet — required before
            // anything (including migration, below) relies on MapGet/MapAll seeing prior state.
            await _roomMap.HydrateAndReplayAsync(cancellationToken).ConfigureAwait(false);

            await MigrateLegacyRowsIfNeededAsync(cancellationToken).ConfigureAwait(false);

            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    // One-shot import of legacy rows into the engine map. Guarded by a marker written to a separate
    // engine-map namespace ("studio-meta"), which survives process restarts via the ordinary
    // hydrate path — so this is exactly-once per workspace, not just per process lifetime.
    private async Task MigrateLegacyRowsIfNeededAsync(CancellationToken cancellationToken)
    {
        if (_roomMap.TryGetValueRawJson(MetaNamespace, MigrationMarkerKey) is not null)
        {
            _logger.LogDebug("studio node store legacy migration already recorded — skipping");
            return;
        }

        var legacyRows = await LoadLatestLegacyRowsAsync(cancellationToken).ConfigureAwait(false);
        var migratedCount = 0;
        // Slice 6.3 — every repo room a migrated row actually landed in needs its own
        // promote+persist tail below (the local "studio" room's own tail always runs, since the
        // migration marker itself always lives there regardless of where rows migrated to).
        var touchedRepoRooms = new HashSet<EngineRoomMap>();

        foreach (var row in legacyRows)
        {
            var key = BuildKey(row.Kind, row.EntityId);

            // Slice 6.3 — a repo-scoped kind's legacy row migrates into its owning repo room
            // instead of "studio", when its RepositoryId resolves to one (design option (b): there
            // is no live caller to ask at migration time, so this parses the row's own payload —
            // see StudioNodeKind.RepoScopedKinds' comment for why "RepositoryId" is a safe literal
            // property name to trust here). Unresolvable (null RepositoryId, not yet bound to the
            // workgroup, or not a repo-scoped kind at all) falls back to "studio", unchanged from
            // pre-6.3 behavior.
            var targetRoomMap = _roomMap;
            if (StudioNodeKind.RepoScopedKinds.Contains(row.Kind))
            {
                var repoRoomId = await TryResolveRepoRoomIdCoreAsync(
                    TryExtractRepositoryId(row.PayloadJson), cancellationToken).ConfigureAwait(false);
                if (repoRoomId is not null)
                {
                    targetRoomMap = await GetOrCreateRepoRoomMapAsync(repoRoomId, cancellationToken).ConfigureAwait(false);
                    touchedRepoRooms.Add(targetRoomMap);
                }
            }

            // Never clobber an existing engine-side entry with an older legacy snapshot — matters if
            // a prior migration attempt partially completed (wrote some MapSets, crashed before the
            // marker), or if a write already landed on this key between hydrate and migration.
            if (targetRoomMap.TryGetValueRawJson(MapNamespace, key) is not null)
                continue;

            JsonDocument payloadDoc;
            try
            {
                payloadDoc = JsonDocument.Parse(row.PayloadJson);
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(
                    ex,
                    "studio node store migration skipped unparseable legacy row kind={Kind} entityId={EntityId}",
                    row.Kind, row.EntityId);
                continue;
            }

            using (payloadDoc)
            {
                var status = targetRoomMap.Set(MapNamespace, key, new { v = 1, kind = row.Kind, payload = payloadDoc.RootElement });
                if (status == AsStatus.Ok)
                {
                    migratedCount += 1;
                }
                else
                {
                    _logger.LogWarning(
                        "studio node store migration MapSet failed room={Room} kind={Kind} entityId={EntityId} status={Status}",
                        targetRoomMap.RoomId, row.Kind, row.EntityId, status);
                }
            }
        }

        var markerStatus = _roomMap.Set(MetaNamespace, MigrationMarkerKey, new
        {
            v = 1,
            migratedAtUtc = DateTimeOffset.UtcNow,
            legacyRowsMigrated = migratedCount
        });

        if (markerStatus != AsStatus.Ok)
        {
            _logger.LogWarning(
                "studio node store migration marker write failed status={Status} — migration will re-attempt next start",
                markerStatus);
        }

        var (migrationPromoteStatus, migrationPromotedNodeIdHex) = _roomMap.PromoteLatestCheckpointToGraph();
        if (migrationPromoteStatus != AsStatus.Ok)
        {
            _logger.LogWarning(
                "studio node store migration checkpoint promotion failed status={Status} — migrated rows may not survive a restart",
                migrationPromoteStatus);
        }

        await _roomMap.PersistAndReplicateAsync(migrationPromotedNodeIdHex, cancellationToken).ConfigureAwait(false);

        foreach (var repoRoomMap in touchedRepoRooms)
        {
            var (repoPromoteStatus, repoPromotedNodeIdHex) = repoRoomMap.PromoteLatestCheckpointToGraph();
            if (repoPromoteStatus != AsStatus.Ok)
            {
                _logger.LogWarning(
                    "studio node store migration checkpoint promotion failed room={Room} status={Status} — migrated rows may not survive a restart",
                    repoRoomMap.RoomId, repoPromoteStatus);
            }

            await repoRoomMap.PersistAndReplicateAsync(repoPromotedNodeIdHex, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "studio node store legacy migration complete legacy_rows_seen={Seen} rows_migrated={Migrated} repo_rooms_touched={RepoRooms}",
            legacyRows.Count, migratedCount, touchedRepoRooms.Count);
    }

    // Slice 6.3 — best-effort extraction of a "RepositoryId" JSON string property from a legacy
    // row's raw payload, used only by migration (see MigrateLegacyRowsIfNeededAsync's own comment
    // for why this is the one place design option (b) — parse-the-payload — is used instead of an
    // explicit caller-supplied id). Never throws; returns null for anything not shaped as expected.
    private static string? TryExtractRepositoryId(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.TryGetProperty("RepositoryId", out var el) && el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // Latest-per-(kind, entityId) legacy row, mirroring the pre-6.1a ReadNodeAsync/ReadAllNodesAsync
    // OrderByDescending(AcceptedAtUtc) + GroupBy semantics exactly, just computed once for reuse by
    // both migration and the fallback read paths.
    private async Task<IReadOnlyList<LegacyRow>> LoadLatestLegacyRowsAsync(CancellationToken cancellationToken)
    {
        var snapshot = await _legacyNodeStore.LoadRoomSnapshotAsync(StudioRoomId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null || snapshot.Nodes.Count == 0)
            return [];

        var latest = new Dictionary<(string Kind, string EntityId), LegacyRow>();

        foreach (var node in snapshot.Nodes)
        {
            if (!string.Equals(node.PayloadKind, LegacyPayloadKind, StringComparison.Ordinal))
                continue;
            if (!TryParseLegacyNodeId(node.NodeIdHex, out var kind, out var entityId))
                continue;

            var acceptedAt = node.AcceptedAtUtc ?? DateTimeOffset.MinValue;
            var mapKey = (kind, entityId);
            if (!latest.TryGetValue(mapKey, out var existing) || acceptedAt > existing.AcceptedAtUtc)
            {
                latest[mapKey] = new LegacyRow(kind, entityId, Encoding.UTF8.GetString(node.Payload), acceptedAt);
            }
        }

        return latest.Values.ToList();
    }

    // Legacy node id shape: "studio:{kind}:{entityId}:{ticksD20}", where kind is itself one of the
    // closed StudioNodeKind strings (which may contain "/") and entityId may also contain "/" or
    // ":". Longest-kind-first prefix match, per STUDIO_ROOM_SCHEMA.md (a)'s parsing rule (stated
    // there for the new "{kind}/{entityId}" engine-map key, reused here for the legacy scheme, which
    // has the same "kind is an opaque, possibly-slashed known constant" ambiguity).
    private static bool TryParseLegacyNodeId(string nodeIdHex, out string kind, out string entityId)
    {
        kind = string.Empty;
        entityId = string.Empty;

        if (!nodeIdHex.StartsWith(LegacyRoomPrefix, StringComparison.Ordinal))
            return false;

        foreach (var candidate in KnownKinds)
        {
            var legacyPrefix = LegacyRoomPrefix + candidate + ":";
            if (!nodeIdHex.StartsWith(legacyPrefix, StringComparison.Ordinal))
                continue;
            if (nodeIdHex.Length <= legacyPrefix.Length + LegacyTickSuffixLength)
                continue;

            kind = candidate;
            entityId = nodeIdHex[legacyPrefix.Length..^LegacyTickSuffixLength];
            return true;
        }

        return false;
    }

    // IStudioNodeStoreReplicationSink — called by RoomPeerClient after applying an inbound pack
    // (ImportPack + PersistInboundPackAsync) so this peer's own IStudioNodeStore reads reflect the
    // just-received remote state. See EngineRoomMap.ReplayCanonicalResolutionIntoLiveMap's own
    // comment; this is just a public, re-runnable entry point into the exact same logic
    // EnsureInitializedAsync uses once at startup.
    //
    // Slice 6.3 — roomId may now be the local "studio" room OR a bound repo room ("repo/{repoId}");
    // this class owns both (see class comment). A repo room this peer has never touched before is
    // hydrated (and replayed once, as part of hydrate) via GetOrCreateRepoRoomMapAsync; an
    // already-cached instance gets an explicit extra replay here regardless, since Lazy<> only runs
    // the hydrate-and-replay factory once and a subsequent inbound pack still needs a fresh replay.
    // (RoomReplicationDispatcher — NodalMerge.Studio.Storage — routes "workgroup" to
    // WorkgroupRepositoryDirectory instead of here; this method never sees that roomId in practice.)
    public async Task RehydrateLiveMapFromCanonicalResolutionAsync(string roomId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        if (string.Equals(roomId, StudioRoomId, StringComparison.Ordinal))
        {
            _roomMap.ReplayCanonicalResolutionIntoLiveMap();
            return;
        }

        var repoRoomMap = await GetOrCreateRepoRoomMapAsync(roomId, cancellationToken).ConfigureAwait(false);
        repoRoomMap.ReplayCanonicalResolutionIntoLiveMap();
    }

    private static string ExtractPayloadRawText(string envelopeRawJson)
    {
        using var doc = JsonDocument.Parse(envelopeRawJson);
        return doc.RootElement.GetProperty("payload").GetRawText();
    }

    private sealed record LegacyRow(string Kind, string EntityId, string PayloadJson, DateTimeOffset AcceptedAtUtc);
}
