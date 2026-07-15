using System.Reflection;
using System.Text;
using System.Text.Json;
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
public sealed class NodalMergeStudioNodeStore : IStudioNodeStore, IStudioNodeStoreReplicationSink
{
    // Engine room every StudioNodeKind is multiplexed into (unchanged from pre-6.1a — room-per-repo
    // is slice 6.3, out of scope here). Kept internal (not referenced elsewhere in this assembly
    // today) only because pre-6.1a code exposed it the same way.
    internal const string StudioRoomId = "studio";

    // Engine map namespace for ordinary studio-node entries, per STUDIO_ROOM_SCHEMA.md (a).
    private const string MapNamespace = "studio";

    // Separate namespace (not a key under MapNamespace) for the one-shot migration marker — cleaner
    // than a reserved key inside the kind keyspace, and structurally impossible to collide with any
    // "{kind}/{entityId}" key since MapAll("studio") never sees it.
    private const string MetaNamespace = "studio-meta";
    private const string MigrationMarkerKey = "migrated-v1";

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

    private readonly IRuntimeCommandBridge _bridge;
    private readonly RuntimeDagPersistenceService _dagPersistence;
    private readonly INodeStoreProvider _legacyNodeStore;
    private readonly IStudioReplicationOutbound _replicationOutbound;
    private readonly ILogger<NodalMergeStudioNodeStore> _logger;

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
        _bridge = bridge;
        _dagPersistence = dagPersistence;
        _legacyNodeStore = legacyNodeStore;
        _replicationOutbound = replicationOutbound;
        _logger = logger;
    }

    public async Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var key = BuildKey(kind, entityId);

        using var payloadDoc = JsonDocument.Parse(payloadJson);
        var status = SubmitMapSet(MapNamespace, key, new { v = 1, kind, payload = payloadDoc.RootElement });
        if (status != AsStatus.Ok)
        {
            _logger.LogWarning(
                "studio node store MapSet failed kind={Kind} entityId={EntityId} status={Status}",
                kind, entityId, status);
            throw new InvalidOperationException(
                $"studio node store MapSet failed for kind={kind} entityId={entityId} status={status}");
        }

        // Durability: simplest-correct for this slice — promote the mutated checkpoint into the sync
        // graph, then persist a fresh server-pack snapshot, immediately after every write. Promotion
        // is required (see class comment) for the mutation to reach the pack at all; the persist call
        // is the same established post-mutation call RuntimeWebSocketLoopRunner already makes for
        // WS-driven writes (source tag "snapshot-on-mutation" there). No batching/debounce: a process
        // crash between MapSet and this point would lose that one write, exactly as a crash between
        // the pre-6.1a PersistAcceptedNodesAsync call and its caller returning would have. 6.1b's
        // implementer may want to revisit batching once outbound pack emission exists.
        var (promoteStatus, promotedNodeIdHex) = PromoteLatestCheckpointToGraph();
        if (promoteStatus != AsStatus.Ok)
        {
            _logger.LogWarning(
                "studio node store checkpoint promotion failed kind={Kind} entityId={EntityId} status={Status} — write is live but may not survive a restart",
                kind, entityId, promoteStatus);
        }

        await _dagPersistence.PersistRoomSnapshotAsync(StudioRoomId, cancellationToken).ConfigureAwait(false);

        // Slice 6.1b — outbound replication seam. Best-effort: the write above is already durable
        // (MapSet + promote + persist all succeeded), so a replication hiccup here never fails the
        // write itself, only logs. No-op when standalone (NoopStudioReplicationOutbound).
        if (!string.IsNullOrWhiteSpace(promotedNodeIdHex))
        {
            try
            {
                await _replicationOutbound.NotifyLocalWriteAsync(StudioRoomId, promotedNodeIdHex, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "studio node store outbound replication notify failed kind={Kind} entityId={EntityId}", kind, entityId);
            }
        }
    }

    public async Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var key = BuildKey(kind, entityId);
        var envelopeRawJson = TryMapGetValueRawJson(MapNamespace, key);
        if (envelopeRawJson is not null)
            return ExtractPayloadRawText(envelopeRawJson);

        // Fallback — anything not (yet) present in the engine map. Under normal operation the
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

        foreach (var (mapKey, valueRawJson) in MapAllEntries(MapNamespace))
        {
            if (!mapKey.StartsWith(prefix, StringComparison.Ordinal))
                continue;

            results[mapKey[prefix.Length..]] = ExtractPayloadRawText(valueRawJson);
        }

        // Fallback — legacy entities of this kind the engine map doesn't have (see ReadNodeAsync).
        var legacyRows = await LoadLatestLegacyRowsAsync(cancellationToken).ConfigureAwait(false);
        foreach (var row in legacyRows)
        {
            if (string.Equals(row.Kind, kind, StringComparison.Ordinal))
                results.TryAdd(row.EntityId, row.PayloadJson);
        }

        return results.Select(kv => (EntityId: kv.Key, PayloadJson: kv.Value)).ToList();
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

            await _dagPersistence.HydrateRoomIfNeededAsync(StudioRoomId, cancellationToken).ConfigureAwait(false);

            // See class comment: hydrate only repopulates the sync graph, not the live map MapGet/
            // MapAll actually read. Bridge that gap before anything (including migration, below)
            // relies on MapGet/MapAll seeing prior state.
            ReplayCanonicalResolutionIntoLiveMap();

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
        if (TryMapGetValueRawJson(MetaNamespace, MigrationMarkerKey) is not null)
        {
            _logger.LogDebug("studio node store legacy migration already recorded — skipping");
            return;
        }

        var legacyRows = await LoadLatestLegacyRowsAsync(cancellationToken).ConfigureAwait(false);
        var migratedCount = 0;

        foreach (var row in legacyRows)
        {
            var key = BuildKey(row.Kind, row.EntityId);

            // Never clobber an existing engine-side entry with an older legacy snapshot — matters if
            // a prior migration attempt partially completed (wrote some MapSets, crashed before the
            // marker), or if a write already landed on this key between hydrate and migration.
            if (TryMapGetValueRawJson(MapNamespace, key) is not null)
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
                var status = SubmitMapSet(MapNamespace, key, new { v = 1, kind = row.Kind, payload = payloadDoc.RootElement });
                if (status == AsStatus.Ok)
                {
                    migratedCount += 1;
                }
                else
                {
                    _logger.LogWarning(
                        "studio node store migration MapSet failed kind={Kind} entityId={EntityId} status={Status}",
                        row.Kind, row.EntityId, status);
                }
            }
        }

        var markerStatus = SubmitMapSet(MetaNamespace, MigrationMarkerKey, new
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

        var (migrationPromoteStatus, migrationPromotedNodeIdHex) = PromoteLatestCheckpointToGraph();
        if (migrationPromoteStatus != AsStatus.Ok)
        {
            _logger.LogWarning(
                "studio node store migration checkpoint promotion failed status={Status} — migrated rows may not survive a restart",
                migrationPromoteStatus);
        }

        await _dagPersistence.PersistRoomSnapshotAsync(StudioRoomId, cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(migrationPromotedNodeIdHex))
        {
            try
            {
                await _replicationOutbound.NotifyLocalWriteAsync(StudioRoomId, migrationPromotedNodeIdHex, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "studio node store migration outbound replication notify failed");
            }
        }

        _logger.LogInformation(
            "studio node store legacy migration complete legacy_rows_seen={Seen} rows_migrated={Migrated}",
            legacyRows.Count, migratedCount);
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

    // Turns the room's current canonical checkpoint into a sync-graph node — see class comment for
    // why this is required before PersistRoomSnapshotAsync can export a pack that actually contains
    // the mutation. Idempotent: re-promoting an unchanged checkpoint returns the existing node
    // (engine-side promotion_id dedup keyed on room/seq/canonical_hash), so calling this after every
    // write is cheap when nothing changed (e.g. a concurrent promoter tick already did it).
    //
    // Slice 6.1b: also returns the promoted node's own id (CheckpointPromoted.node_id_hex), which
    // is the exact id the outbound replication seam needs — RoomPeerClient fetches precisely this
    // one node's bytes via HostCommand::MstDone{ids:[nodeIdHex]} rather than trying to compute a
    // "known ids" delta itself (HostCommand::RequestServerPack's missing_hashes is a flat diff over
    // every node id the room's sync graph has ever held, not a frontier/ancestry-aware one, so
    // tracking a correct cumulative "known" set from the .NET side would require decoding the
    // pack's binary node list — out of scope; MstDone{ids} sidesteps the problem entirely).
    private (AsStatus Status, string? NodeIdHex) PromoteLatestCheckpointToGraph()
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = StudioRoomId,
            command = new
            {
                PromoteCheckpointToGraph = new { selector = new { selector = "latest" } }
            }
        });

        var response = _bridge.ProcessJsonCommand(commandJson);
        if (response.Status != AsStatus.Ok)
            return (response.Status, null);

        string? nodeIdHex = null;
        try
        {
            using var eventsDoc = JsonDocument.Parse(response.EventsJson);
            if (eventsDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var evt in eventsDoc.RootElement.EnumerateArray())
                {
                    if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("CheckpointPromoted", out var promoted))
                        continue;
                    if (promoted.TryGetProperty("node_id_hex", out var nodeIdEl) && nodeIdEl.ValueKind == JsonValueKind.String)
                        nodeIdHex = nodeIdEl.GetString();
                    break;
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "studio node store failed to parse CheckpointPromoted event — outbound replication will be skipped for this write");
        }

        return (response.Status, nodeIdHex);
    }

    // IStudioNodeStoreReplicationSink — called by RoomPeerClient after applying an inbound pack
    // (ImportPack + PersistInboundPackAsync) so this peer's own IStudioNodeStore reads reflect the
    // just-received remote state. See ReplayCanonicalResolutionIntoLiveMap's own comment; this is
    // just a public, re-runnable entry point into the exact same logic EnsureInitializedAsync uses
    // once at startup.
    public Task RehydrateLiveMapFromCanonicalResolutionAsync(CancellationToken cancellationToken = default)
    {
        ReplayCanonicalResolutionIntoLiveMap();
        return Task.CompletedTask;
    }

    // Bridges the room_maps/sync-graph gap described in the class comment: GetCanonicalResolution
    // resolves correctly off the hydrated sync graph (unlike MapGet/MapAll, which read room_maps —
    // never touched by ImportPack), so re-issuing MapSet for every resolved entry once per startup
    // makes MapGet/MapAll correct again without inventing any new wire format.
    //
    // Namespace can't be read back from a canonical-resolution entry (canonical rows are keyed by
    // the bare MapSet `key`, with no namespace of their own — see engine.rs's MapSet handler), so it
    // is inferred here: this class is the sole writer into the "studio" room, and the only key it
    // ever puts outside MapNamespace is the single well-known MigrationMarkerKey literal, which can
    // never collide with a "{kind}/{entityId}" key (every StudioNodeKind constant starts with
    // "studio/", so BuildKey's output never equals the bare "migrated-v1" literal).
    private void ReplayCanonicalResolutionIntoLiveMap()
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = StudioRoomId,
            command = "GetCanonicalResolution"
        });

        var response = _bridge.ProcessJsonCommand(commandJson);
        if (response.Status != AsStatus.Ok)
            return;

        using var eventsDoc = JsonDocument.Parse(response.EventsJson);
        if (eventsDoc.RootElement.ValueKind != JsonValueKind.Array)
            return;

        foreach (var evt in eventsDoc.RootElement.EnumerateArray())
        {
            if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("CanonicalResolutionQueried", out var queried))
                continue;
            if (!queried.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                continue;

            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object
                    || !entry.TryGetProperty("key", out var keyEl)
                    || !entry.TryGetProperty("value_bytes_b64", out var valueBytesEl))
                    continue;

                var key = keyEl.GetString();
                // Engine-internal promotion markers ("_promotion/id", "_promotion/source_snapshot_seq")
                // are not JSON at all (raw hash/LE-integer bytes) and are never one of this class's
                // own keys — skip them rather than fail trying to parse them as JSON below.
                if (string.IsNullOrEmpty(key) || key.StartsWith('_'))
                    continue;

                var valueBytesB64 = valueBytesEl.GetString();
                if (string.IsNullOrEmpty(valueBytesB64))
                    continue;

                string valueJson;
                try
                {
                    valueJson = Encoding.UTF8.GetString(Convert.FromBase64String(valueBytesB64));
                }
                catch (FormatException)
                {
                    continue;
                }

                JsonDocument valueDoc;
                try
                {
                    valueDoc = JsonDocument.Parse(valueJson);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (valueDoc)
                {
                    var @namespace = string.Equals(key, MigrationMarkerKey, StringComparison.Ordinal)
                        ? MetaNamespace
                        : MapNamespace;
                    SubmitMapSet(@namespace, key, valueDoc.RootElement);
                }
            }

            return;
        }
    }

    private AsStatus SubmitMapSet(string @namespace, string key, object value)
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = StudioRoomId,
            command = new
            {
                MapSet = new { @namespace, key, value }
            }
        });

        return _bridge.ProcessJsonCommand(commandJson).Status;
    }

    // Returns the raw JSON text of the map value for (namespace, key), or null if absent. Callers
    // that need the studio-node envelope's inner payload go through ExtractPayloadRawText.
    private string? TryMapGetValueRawJson(string @namespace, string key)
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = StudioRoomId,
            command = new
            {
                MapGet = new { @namespace, key }
            }
        });

        var response = _bridge.ProcessJsonCommand(commandJson);
        if (response.Status != AsStatus.Ok)
            return null;

        using var eventsDoc = JsonDocument.Parse(response.EventsJson);
        if (eventsDoc.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var evt in eventsDoc.RootElement.EnumerateArray())
        {
            if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("MapValueRead", out var read))
                continue;

            if (!read.TryGetProperty("value", out var valueEl)
                || valueEl.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return null;

            return valueEl.GetRawText();
        }

        return null;
    }

    private IReadOnlyList<(string Key, string ValueRawJson)> MapAllEntries(string @namespace)
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = StudioRoomId,
            command = new
            {
                MapAll = new { @namespace }
            }
        });

        var response = _bridge.ProcessJsonCommand(commandJson);
        if (response.Status != AsStatus.Ok)
            return [];

        using var eventsDoc = JsonDocument.Parse(response.EventsJson);
        if (eventsDoc.RootElement.ValueKind != JsonValueKind.Array)
            return [];

        foreach (var evt in eventsDoc.RootElement.EnumerateArray())
        {
            if (evt.ValueKind != JsonValueKind.Object || !evt.TryGetProperty("MapEntriesListed", out var listed))
                continue;
            if (!listed.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                continue;

            return entries.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object
                    && e.TryGetProperty("key", out _)
                    && e.TryGetProperty("value", out _))
                .Select(e => (e.GetProperty("key").GetString()!, e.GetProperty("value").GetRawText()))
                .ToList();
        }

        return [];
    }

    private static string ExtractPayloadRawText(string envelopeRawJson)
    {
        using var doc = JsonDocument.Parse(envelopeRawJson);
        return doc.RootElement.GetProperty("payload").GetRawText();
    }

    private sealed record LegacyRow(string Kind, string EntityId, string PayloadJson, DateTimeOffset AcceptedAtUtc);
}
