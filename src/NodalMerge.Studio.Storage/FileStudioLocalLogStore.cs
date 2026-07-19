using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace NodalMerge.Studio.Storage;

// L2.1 (plans/room-persistence-bloat.md) — durable local append log backed by one JSONL file per
// kind under StudioLocalLogOptions.Directory. Deliberately dependency-free (no SQLite ADO stack to
// conflict with the engine's own SQLitePCLRaw provider): each line is a small envelope
// {Id, Ts, Payload}; upsert semantics are last-write-per-id resolved on read. A per-kind semaphore
// serializes writes/rewrites to a file; the data is telemetry / rebuildable cache / append-only
// history, so a torn last line after a crash is tolerated (unparseable lines are skipped on read).
public sealed class FileStudioLocalLogStore : IStudioLocalLogStore
{
    private readonly string _dir;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public FileStudioLocalLogStore(StudioLocalLogOptions options) => _dir = options.Directory;

    private sealed record Line(string Id, string Ts, string Payload);

    private SemaphoreSlim LockFor(string kind) => _locks.GetOrAdd(kind, _ => new SemaphoreSlim(1, 1));

    private string PathFor(string kind)
    {
        var safe = new string(kind.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
        return Path.Combine(_dir, safe + ".jsonl");
    }

    public async Task AppendAsync(
        string kind, string id, string payloadJson, DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        var line = JsonSerializer.Serialize(
            new Line(id, occurredAt.ToString("O", CultureInfo.InvariantCulture), payloadJson));
        var sem = LockFor(kind);
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_dir);
            await File.AppendAllTextAsync(PathFor(kind), line + "\n", cancellationToken).ConfigureAwait(false);
        }
        finally { sem.Release(); }
    }

    public async Task<string?> GetAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        foreach (var (rowId, payload) in await ReadAllAsync(kind, cancellationToken).ConfigureAwait(false))
            if (rowId == id)
                return payload;
        return null;
    }

    public async Task<IReadOnlyList<(string Id, string PayloadJson)>> ReadAllAsync(
        string kind, CancellationToken cancellationToken = default)
    {
        var sem = LockFor(kind);
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (order, latest) = await LoadLatestAsync(kind, cancellationToken).ConfigureAwait(false);
            return order.Select(id => (id, latest[id].Payload)).ToList();
        }
        finally { sem.Release(); }
    }

    public async Task<int> PruneOlderThanAsync(
        string kind, DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        var sem = LockFor(kind);
        await sem.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var (order, latest) = await LoadLatestAsync(kind, cancellationToken).ConfigureAwait(false);
            var kept = new List<string>();
            var removed = 0;
            foreach (var id in order)
            {
                var (ts, payload) = latest[id];
                if (ts < olderThan) { removed++; continue; }
                kept.Add(JsonSerializer.Serialize(
                    new Line(id, ts.ToString("O", CultureInfo.InvariantCulture), payload)));
            }

            if (removed == 0)
                return 0;

            var path = PathFor(kind);
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(
                tmp, kept.Count > 0 ? string.Join("\n", kept) + "\n" : string.Empty, cancellationToken)
                .ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
            return removed;
        }
        finally { sem.Release(); }
    }

    // Reads the file once, resolving last-write-per-id and preserving first-appearance order.
    // Caller must hold the per-kind lock. Unparseable (e.g. crash-torn) lines are skipped.
    private async Task<(List<string> Order, Dictionary<string, (DateTimeOffset Ts, string Payload)> Latest)>
        LoadLatestAsync(string kind, CancellationToken cancellationToken)
    {
        var order = new List<string>();
        var latest = new Dictionary<string, (DateTimeOffset, string)>(StringComparer.Ordinal);

        var path = PathFor(kind);
        if (!File.Exists(path))
            return (order, latest);

        foreach (var raw in await File.ReadAllLinesAsync(path, cancellationToken).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            Line? entry;
            try { entry = JsonSerializer.Deserialize<Line>(raw); }
            catch (JsonException) { continue; }
            if (entry is null)
                continue;

            var ts = DateTimeOffset.TryParse(
                entry.Ts, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : DateTimeOffset.MinValue;

            if (!latest.ContainsKey(entry.Id))
                order.Add(entry.Id);
            latest[entry.Id] = (ts, entry.Payload);
        }

        return (order, latest);
    }
}
