using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.2 (plans/cas-distribution-and-storage.md Phase 6, D1/D2; docs/STUDIO_ROOM_SCHEMA.md (b))
// — the workgroup room's repositories map: repoId -> {label, repoRoomId, hints}. Lives in a
// *different* engine room ("workgroup") and namespace ("repositories") than
// NodalMergeStudioNodeStore's "studio" room, but rides the exact same EngineRoomMap bridging
// (see that class's comment) since the underlying engine quirks (room_maps/sync-graph split,
// promote-then-persist, outbound notify) apply to every room identically.

// One entry in the workgroup repositories map — mirrors docs/STUDIO_ROOM_SCHEMA.md (b)'s frozen
// JSON value shape {v, label, repoRoomId, hints:{rootShas, remotes}} (the "v" envelope version is
// an on-the-wire concern handled by WorkgroupRepositoryDirectory's own (de)serialization, not
// surfaced on this in-memory shape).
public sealed record WorkgroupRepositoryEntry(
    string RepoId,
    string? Label,
    string RepoRoomId,
    RepositoryIdentityHints Hints);

// Outcome of docs/STUDIO_ROOM_SCHEMA.md (b)'s matching flow (D2 is the authority). A closed,
// exhaustive set — every IWorkgroupRepositoryDirectory.MatchAsync call returns exactly one of
// these three, never throws for an ordinary "no/ambiguous match" outcome.
public abstract record RepositoryMatchResult
{
    private RepositoryMatchResult()
    {
    }

    // Exactly one registered entry's root-SHA set intersects the local hints (or, after fork-tiebreak,
    // exactly one candidate's remote set also intersects).
    public sealed record Matched(WorkgroupRepositoryEntry Entry) : RepositoryMatchResult;

    // No registered entry shares any root SHA with the local hints. Per D2, a remote-URL match with
    // no root-SHA corroboration is NEVER treated as identity on its own — this is NoMatch, not Matched,
    // even if some registered entry's remotes happen to intersect the local ones.
    public sealed record NoMatch : RepositoryMatchResult;

    // Either: (a) more than one registered entry shares a root SHA with the local hints and the
    // remote-URL tiebreak didn't narrow it to exactly one (fork ambiguity), or (b) the local hints
    // are degraded (both root SHAs and remotes empty) — "no algorithm can know", honest per D2,
    // never a silent mint. Candidates is the narrowed set worth offering the caller: the
    // remote-tiebreak survivors in case (a) if any remote matched, otherwise the full root-SHA-tied
    // set; every registered entry in case (b) (there is no narrower answer).
    public sealed record NeedsDisambiguation(IReadOnlyList<WorkgroupRepositoryEntry> Candidates) : RepositoryMatchResult;
}

// Pure matching logic (docs/STUDIO_ROOM_SCHEMA.md (b) "Matching flow", D2) — deliberately
// independent of storage so both the engine-backed WorkgroupRepositoryDirectory and the in-memory
// test double share one implementation, and so it's directly unit-testable against a plain list of
// entries with no engine/DI machinery at all.
public static class RepositoryIdentityMatcher
{
    public static RepositoryMatchResult Match(IReadOnlyList<WorkgroupRepositoryEntry> registered, RepositoryIdentityHints hints)
    {
        // Degraded input (both hint components empty: shallow clone with no remote, empty repo,
        // etc.) — honest "can't tell", per D2. Offering every registered entry as a candidate is
        // the only truthful answer; a caller with zero registered entries (nothing to disambiguate
        // against) is expected to treat that specially — see RepositoryRegistryService's binding
        // flow for the single-user-continuity handling of that specific degenerate sub-case.
        if (hints.IsDegraded)
            return new RepositoryMatchResult.NeedsDisambiguation(registered);

        var localRoots = hints.RootShas.ToHashSet(StringComparer.Ordinal);
        var rootMatches = localRoots.Count == 0
            ? []
            : registered.Where(e => e.Hints.RootShas.Any(localRoots.Contains)).ToList();

        switch (rootMatches.Count)
        {
            case 1:
                return new RepositoryMatchResult.Matched(rootMatches[0]);

            case 0:
                // No root-SHA match at all. Per D2: a remote-only match is NOT identity — never
                // silently bind on remotes alone, even if some registered entry's remotes intersect.
                return new RepositoryMatchResult.NoMatch();

            default:
            {
                // Fork ambiguity — remote-URL tiebreak among the root-SHA-ambiguous candidates only
                // (remotes are never consulted outside this narrowing role, per D2).
                var localRemotes = hints.Remotes.ToHashSet(StringComparer.Ordinal);
                var remoteMatches = localRemotes.Count == 0
                    ? []
                    : rootMatches.Where(e => e.Hints.Remotes.Any(localRemotes.Contains)).ToList();

                if (remoteMatches.Count == 1)
                    return new RepositoryMatchResult.Matched(remoteMatches[0]);

                // Still ambiguous (no remote to tiebreak with, an unrecognized remote, or more than
                // one remote-matched candidate) — one-time disambiguation, never a silent pick.
                return new RepositoryMatchResult.NeedsDisambiguation(
                    remoteMatches.Count > 0 ? remoteMatches : rootMatches);
            }
        }
    }
}

public interface IWorkgroupRepositoryDirectory
{
    Task<IReadOnlyList<WorkgroupRepositoryEntry>> ListAsync(CancellationToken cancellationToken = default);

    // Mints a new repoId (repo-{guid:N}, per docs/STUDIO_ROOM_SCHEMA.md (b)) and records the entry,
    // unless preferredRepoId is supplied and not already taken — see RepositoryRegistryService's
    // binding flow for why (single-user/standalone continuity: an existing local-candidate id
    // should become the workgroup id too, rather than minting a second identity for the same repo).
    Task<WorkgroupRepositoryEntry> RegisterAsync(
        string? label, RepositoryIdentityHints hints, string? preferredRepoId = null, CancellationToken cancellationToken = default);

    Task<RepositoryMatchResult> MatchAsync(RepositoryIdentityHints hints, CancellationToken cancellationToken = default);
}

// In-memory test double — same role as InMemoryStudioNodeStore. Two instances can share one
// backing dictionary (pass the same instance to both constructors) to model "two peers observing
// the same workgroup map state" at the service level, without spinning up a real engine room —
// exactly what the 6.2 acceptance bar's two-peer convergence test needs (plans/cas-distribution-
// and-storage.md's 6.2 row explicitly calls this sufficient: "in-memory store level is fine").
public sealed class InMemoryWorkgroupRepositoryDirectory(WorkgroupRepositoryStore? shared = null) : IWorkgroupRepositoryDirectory
{
    private readonly WorkgroupRepositoryStore _store = shared ?? new WorkgroupRepositoryStore();

    public Task<IReadOnlyList<WorkgroupRepositoryEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<WorkgroupRepositoryEntry>>(_store.Snapshot());

    public Task<WorkgroupRepositoryEntry> RegisterAsync(
        string? label, RepositoryIdentityHints hints, string? preferredRepoId = null, CancellationToken cancellationToken = default)
    {
        var entry = _store.Register(label, hints, preferredRepoId);
        return Task.FromResult(entry);
    }

    public async Task<RepositoryMatchResult> MatchAsync(RepositoryIdentityHints hints, CancellationToken cancellationToken = default)
    {
        var registered = await ListAsync(cancellationToken).ConfigureAwait(false);
        return RepositoryIdentityMatcher.Match(registered, hints);
    }
}

// Thread-safe backing store shared by one or more InMemoryWorkgroupRepositoryDirectory instances.
// Split out from the directory class itself purely so "two peers, one shared map" tests can pass
// one store to two directory instances without exposing dictionary internals on the public
// interface.
public sealed class WorkgroupRepositoryStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WorkgroupRepositoryEntry> _entries = new();

    public IReadOnlyList<WorkgroupRepositoryEntry> Snapshot() => _entries.Values.ToList();

    public WorkgroupRepositoryEntry Register(string? label, RepositoryIdentityHints hints, string? preferredRepoId)
    {
        var repoId = preferredRepoId is not null && !_entries.ContainsKey(preferredRepoId)
            ? preferredRepoId
            : $"repo-{Guid.NewGuid():N}";

        var entry = new WorkgroupRepositoryEntry(repoId, label, $"repo/{repoId}", hints);
        _entries[repoId] = entry;
        return entry;
    }
}

// Engine-backed production implementation — the workgroup room's repositories map ridden through
// EngineRoomMap exactly as NodalMergeStudioNodeStore rides the "studio" room's "studio" namespace.
// See docs/STUDIO_ROOM_SCHEMA.md (b) for the frozen room id ("workgroup" — WorkgroupRoomId below is
// the one place this constant is defined; 6.4 replaces the literal with `Room:Workgroup` config,
// per that slice's row in plans/cas-distribution-and-storage.md) and value shape.
//
// What this class does NOT do (out of scope for 6.2, see the slice's own task brief): join the
// workgroup room's WS channel upstream. The room is created/hydrated locally on demand and writes
// go through IStudioReplicationOutbound exactly like NodalMergeStudioNodeStore's "studio" room
// writes do — but RoomPeerClient (the only IStudioReplicationOutbound implementation that actually
// talks to a server) is wired to a single room id (HeadlessPeerOptions.RoomId, "studio" today) and
// silently ignores NotifyLocalWriteAsync calls for any other room (see RoomPeerClient.
// NotifyLocalWriteAsync's room-id equality check). So today, workgroup-room writes ARE promoted
// and persisted locally (durable, replay-safe across restarts) but are NOT pushed upstream or
// broadcast to other peers — real multi-peer convergence needs either a second RoomPeerClient-
// shaped connection for the workgroup room, or teaching RoomPeerClient to serve more than one room.
// That wiring is 6.3/6.4 territory per the task brief; this class is ready for it (it already calls
// the same outbound seam), nothing here needs to change when it lands.
public sealed class WorkgroupRepositoryDirectory : IWorkgroupRepositoryDirectory
{
    public const string WorkgroupRoomId = "workgroup";
    private const string RepositoriesNamespace = "repositories";

    private readonly EngineRoomMap _roomMap;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    public WorkgroupRepositoryDirectory(
        IRuntimeCommandBridge bridge,
        RuntimeDagPersistenceService dagPersistence,
        IStudioReplicationOutbound replicationOutbound,
        ILogger<WorkgroupRepositoryDirectory> logger)
    {
        // Single namespace room today (only "repositories" is defined by the frozen schema for
        // 6.2) — the replay-namespace resolver is a constant, unlike NodalMergeStudioNodeStore's
        // two-namespace ("studio"/"studio-meta") inference.
        _roomMap = new EngineRoomMap(WorkgroupRoomId, _ => RepositoriesNamespace, bridge, dagPersistence, replicationOutbound, logger);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
                return;

            await _roomMap.HydrateAndReplayAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<IReadOnlyList<WorkgroupRepositoryEntry>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        return _roomMap.AllEntries(RepositoriesNamespace)
            .Select(e => TryParseEntry(e.Key, e.ValueRawJson))
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();
    }

    public async Task<WorkgroupRepositoryEntry> RegisterAsync(
        string? label, RepositoryIdentityHints hints, string? preferredRepoId = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var repoId = preferredRepoId is not null && _roomMap.TryGetValueRawJson(RepositoriesNamespace, preferredRepoId) is null
            ? preferredRepoId
            : $"repo-{Guid.NewGuid():N}";

        var entry = new WorkgroupRepositoryEntry(repoId, label, $"repo/{repoId}", hints);

        await _roomMap.WriteEntryAsync(RepositoriesNamespace, repoId, ToEnvelope(entry), cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<RepositoryMatchResult> MatchAsync(RepositoryIdentityHints hints, CancellationToken cancellationToken = default)
    {
        var registered = await ListAsync(cancellationToken).ConfigureAwait(false);
        return RepositoryIdentityMatcher.Match(registered, hints);
    }

    // docs/STUDIO_ROOM_SCHEMA.md (b) value shape: {v, label, repoRoomId, hints:{rootShas, remotes}}.
    private static object ToEnvelope(WorkgroupRepositoryEntry entry) => new
    {
        v = 1,
        label = entry.Label,
        repoRoomId = entry.RepoRoomId,
        hints = new { rootShas = entry.Hints.RootShas, remotes = entry.Hints.Remotes }
    };

    private static WorkgroupRepositoryEntry? TryParseEntry(string repoId, string valueRawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(valueRawJson);
            var root = doc.RootElement;

            var label = root.TryGetProperty("label", out var labelEl) && labelEl.ValueKind == JsonValueKind.String
                ? labelEl.GetString()
                : null;
            var repoRoomId = root.TryGetProperty("repoRoomId", out var repoRoomIdEl) && repoRoomIdEl.ValueKind == JsonValueKind.String
                ? repoRoomIdEl.GetString()!
                : $"repo/{repoId}";

            var rootShas = new List<string>();
            var remotes = new List<string>();
            if (root.TryGetProperty("hints", out var hintsEl) && hintsEl.ValueKind == JsonValueKind.Object)
            {
                if (hintsEl.TryGetProperty("rootShas", out var shasEl) && shasEl.ValueKind == JsonValueKind.Array)
                    rootShas = shasEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
                if (hintsEl.TryGetProperty("remotes", out var remotesEl) && remotesEl.ValueKind == JsonValueKind.Array)
                    remotes = remotesEl.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            }

            return new WorkgroupRepositoryEntry(repoId, label, repoRoomId, new RepositoryIdentityHints(rootShas, remotes));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
