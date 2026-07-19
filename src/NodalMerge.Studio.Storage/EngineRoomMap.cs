using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.2 (plans/cas-distribution-and-storage.md Phase 6) — extracted from
// NodalMergeStudioNodeStore (slice 6.1a/6.1b), which established this exact bridging pattern for
// the "studio" room and needed it again, unchanged, for the workgroup room's repositories map
// (docs/STUDIO_ROOM_SCHEMA.md (b)). Rather than re-deriving the same three engine-behavior quirks a
// second time (or copy-pasting NodalMergeStudioNodeStore's private methods), both rooms now go
// through this one class. See NodalMergeStudioNodeStore's own class comment for the full narrative
// of how these quirks were discovered; summarized here:
//
//   1. MapSet/MapGet/MapAll operate on the engine's live "room_maps" state, which has no
//      persistence of its own. A MapSet mutation only reaches the persisted pack once the
//      checkpoint containing it is promoted into the sync graph
//      (HostCommand::PromoteCheckpointToGraph{selector:"latest"}) — so every mutating write must
//      promote-then-persist, or the write is lost on restart despite looking "live" in-process.
//   2. ImportPack (driven by RuntimeDagPersistenceService.HydrateRoomIfNeededAsync) only
//      repopulates the sync graph, never "room_maps" — so immediately after hydrate, MapGet/MapAll
//      see nothing until GetCanonicalResolution's result (which *does* resolve correctly off the
//      hydrated graph) is replayed back through MapSet once.
//   3. Outbound replication (IStudioReplicationOutbound) must be handed the exact node id
//      PromoteCheckpointToGraph itself returns (CheckpointPromoted.node_id_hex) — not a
//      caller-derived delta; see PromoteLatestCheckpointToGraph's own comment for why a "known
//      ids" delta isn't viable to track correctly from the .NET side.
//
// Deliberately does NOT own an init-gate, a "kind" concept, or a legacy-migration story — those
// vary per caller (NodalMergeStudioNodeStore's one-shot legacy-row migration must run between
// replay and "ready"; WorkgroupRepositoryDirectory has no migration step at all) and stay with the
// owning class. One instance per room; every public method here is safe to call as soon as
// HydrateAndReplayAsync has completed once for that instance.
internal sealed class EngineRoomMap(
    string roomId,
    Func<string, string> resolveNamespaceForReplay,
    IRuntimeCommandBridge bridge,
    RuntimeDagPersistenceService dagPersistence,
    IStudioReplicationOutbound replicationOutbound,
    ILogger logger)
{
    public string RoomId { get; } = roomId;

    // Hydrates this room's sync graph from persisted packs (if any), then bridges the room_maps gap
    // described above (quirk 2) by replaying GetCanonicalResolution's entries back through MapSet.
    // Callers own their own idempotency (an init-gate around this call) since the right point to
    // sequence any caller-specific post-hydrate step (e.g. legacy-row migration) differs per room.
    public async Task HydrateAndReplayAsync(CancellationToken cancellationToken)
    {
        await dagPersistence.HydrateRoomIfNeededAsync(RoomId, cancellationToken).ConfigureAwait(false);
        ReplayCanonicalResolutionIntoLiveMap();
    }

    // Re-runs just the replay half of HydrateAndReplayAsync, without re-hydrating — the shape
    // IStudioNodeStoreReplicationSink-style inbound-pack handlers need (the pack was already
    // ImportPack'd by the caller; only the room_maps replay is missing).
    public void ReplayCanonicalResolutionIntoLiveMap()
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = RoomId,
            command = "GetCanonicalResolution"
        });

        var response = bridge.ProcessJsonCommand(commandJson);
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
                // are not JSON at all (raw hash/LE-integer bytes) and are never a real caller key —
                // skip them rather than fail trying to parse them as JSON below.
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
                    Set(resolveNamespaceForReplay(key), key, valueDoc.RootElement);
                }
            }

            return;
        }
    }

    public string? TryGetValueRawJson(string @namespace, string key)
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = RoomId,
            command = new
            {
                MapGet = new { @namespace, key }
            }
        });

        var response = bridge.ProcessJsonCommand(commandJson);
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

    public IReadOnlyList<(string Key, string ValueRawJson)> AllEntries(string @namespace)
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = RoomId,
            command = new
            {
                MapAll = new { @namespace }
            }
        });

        var response = bridge.ProcessJsonCommand(commandJson);
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

    public AsStatus Set(string @namespace, string key, object value)
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = RoomId,
            command = new
            {
                MapSet = new { @namespace, key, value }
            }
        });

        return bridge.ProcessJsonCommand(commandJson).Status;
    }

    // See class comment quirk 3. Idempotent: re-promoting an unchanged checkpoint returns the
    // existing node (engine-side promotion_id dedup keyed on room/seq/canonical_hash), so calling
    // this after every write is cheap when nothing changed (e.g. a concurrent promoter tick already
    // did it).
    public (AsStatus Status, string? NodeIdHex) PromoteLatestCheckpointToGraph()
    {
        var commandJson = JsonSerializer.Serialize(new
        {
            room_id = RoomId,
            command = new
            {
                PromoteCheckpointToGraph = new { selector = new { selector = "latest" } }
            }
        });

        var response = bridge.ProcessJsonCommand(commandJson);
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
            logger.LogWarning(ex, "engine room map failed to parse CheckpointPromoted event room={Room} — outbound replication will be skipped for this write", RoomId);
        }

        return (response.Status, nodeIdHex);
    }

    // Persists this write as an incremental DELTA (just the promoted checkpoint node) and, when a
    // checkpoint was actually promoted, notifies the outbound replication seam. Best-effort: a
    // persistence/replication hiccup here never fails the write itself (the MapSet already succeeded),
    // only logs.
    //
    // Integration-checkpoint model (plans/room-snapshot-checkpoint-redesign.md): a full-room snapshot
    // is no longer taken per write — that re-serialized the entire growing room on every entity write
    // and was the primary source of the O(n^2) DB bloat. The promoted node is the same self-applying
    // single-node pack this method already hands to outbound replication, so persisting it locally is
    // symmetric to how an inbound peer delta is persisted; hydration replays these on top of the last
    // full checkpoint (minted only at goal-complete / merge-to-main). A null promotedNodeIdHex means
    // nothing new reached the sync graph (promotion failed / no-op) — there is nothing to persist or
    // replicate, and the old full-snapshot here would only re-serialize already-persisted state.
    public async Task PersistAndReplicateAsync(string? promotedNodeIdHex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(promotedNodeIdHex))
            return;

        await dagPersistence.PersistPromotedNodeDeltaAsync(RoomId, promotedNodeIdHex, cancellationToken).ConfigureAwait(false);

        try
        {
            await replicationOutbound.NotifyLocalWriteAsync(RoomId, promotedNodeIdHex, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "engine room map outbound replication notify failed room={Room}", RoomId);
        }
    }

    // Integration-checkpoint (plans/room-snapshot-checkpoint-redesign.md): mints one full-room
    // server-pack snapshot for this room as a "last known good" hydrate checkpoint. This is the
    // full-room snapshot that the per-write path (PersistAndReplicateAsync) no longer takes — invoked
    // only at integration points (merge to main / goal completion) so the delta chain replayed on the
    // next hydrate stays bounded.
    public Task PersistCheckpointAsync(CancellationToken cancellationToken) =>
        dagPersistence.PersistRoomSnapshotAsync(RoomId, cancellationToken).AsTask();

    // Convenience combining Set + PromoteLatestCheckpointToGraph + PersistAndReplicateAsync — the
    // exact tail every single-entry mutating write in this codebase performs. Batched multi-row
    // writes (e.g. NodalMergeStudioNodeStore's legacy-row migration) call the three steps
    // separately instead, so N row writes cost one promote+persist, not N.
    public async Task<AsStatus> WriteEntryAsync(string @namespace, string key, object value, CancellationToken cancellationToken)
    {
        var status = Set(@namespace, key, value);
        if (status != AsStatus.Ok)
            return status;

        var (promoteStatus, nodeIdHex) = PromoteLatestCheckpointToGraph();
        if (promoteStatus != AsStatus.Ok)
        {
            logger.LogWarning(
                "engine room map checkpoint promotion failed room={Room} namespace={Namespace} key={Key} status={Status} — write is live but may not survive a restart",
                RoomId, @namespace, key, promoteStatus);
        }

        await PersistAndReplicateAsync(nodeIdHex, cancellationToken).ConfigureAwait(false);
        return status;
    }
}
