namespace NodalMerge.Studio.Storage;

// Layer 2 L2.1 (plans/room-persistence-bloat.md) — a studio-owned, durable, LOCAL append log for
// the high-volume, peer-local kinds (ConversationLogV1, ExecutionEventV1, OrchestrationEventV1,
// ProjectionSnapshotV1). These used to ride IStudioNodeStore → the embedded engine's CRDT room /
// sync graph, which promotes a new immutable node on every write and never GCs — bloating the
// peer-private `studio` room (the snapshot-on-mutation storm). They are non-replicating telemetry /
// rebuildable projection cache / reasoning history, not authoritative replicated state, so they
// belong in a plain local log OFF the sync graph entirely.
//
// The owning services keep their in-memory indexes and use this store only to persist (Append) and
// to rebuild on startup (ReadAll); all secondary-key querying stays in the services, so the surface
// here is deliberately tiny. Swapping the backing implementation (file → SQLite, say) is contained
// behind this interface.
public interface IStudioLocalLogStore
{
    // Upsert by (kind, id). `occurredAt` (the record's own OccurredAt/CreatedAt) drives
    // PruneOlderThanAsync retention.
    Task AppendAsync(
        string kind, string id, string payloadJson, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default);

    // Latest payload for (kind, id), or null.
    Task<string?> GetAsync(string kind, string id, CancellationToken cancellationToken = default);

    // Full-kind scan (latest payload per id) used by each owning service's RehydrateAsync to rebuild
    // its in-memory dict on startup. No ordering guarantee — callers re-sort.
    Task<IReadOnlyList<(string Id, string PayloadJson)>> ReadAllAsync(
        string kind, CancellationToken cancellationToken = default);

    // Retention: drop rows whose occurredAt is strictly older than `olderThan`. Returns removed count.
    Task<int> PruneOlderThanAsync(
        string kind, DateTimeOffset olderThan, CancellationToken cancellationToken = default);
}

// Directory for the per-kind append logs. Relative paths resolve against the host's working
// directory, same convention as NodalMerge:Storage:Sqlite:DbPath. NodalMerge.Studio.Host binds
// NodalMerge:Studio:LocalLog:Directory over this default (last-AddSingleton-wins).
public sealed class StudioLocalLogOptions
{
    public string Directory { get; set; } = System.IO.Path.Combine("data", "local-log");
}
