using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.DotNetHost.Ffi;
using NodalMerge.DotNetHost.Runtime;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 6.3 (plans/cas-distribution-and-storage.md Phase 6, D3) — the minimal shape of D3's
// "workgroup-level goal node referencing N work units, one per repo room": a cross-repo goal
// coordination record, written to the "workgroup" room's own "goals" namespace via the same
// EngineRoomMap bridging every other room in this codebase rides. Deliberately minimal per the
// slice's acceptance bar ("model the workgroup goal node minimally... without building goal-fanout
// UX") — this is NOT wired into IOrchestratorService/goal creation. A real cross-repo goal-fanout
// feature (creating the N per-repo work units, tracking "all N landed", ordering) is out of scope
// here and left to whichever future slice builds that UX; this class only proves the room/record
// shape exists and is exercisable, per D3's coordination note ("all N landed" is goal-level state
// in the workgroup room).
public sealed record WorkgroupGoalRepoWorkUnit(string RepoId, string WorkUnitId);

public sealed record WorkgroupGoalEntry(
    string GoalId,
    IReadOnlyList<WorkgroupGoalRepoWorkUnit> RepoWorkUnits,
    DateTimeOffset CreatedAt);

public interface IWorkgroupGoalDirectory
{
    Task<WorkgroupGoalEntry> RecordAsync(
        string goalId, IReadOnlyList<WorkgroupGoalRepoWorkUnit> repoWorkUnits, CancellationToken cancellationToken = default);

    Task<WorkgroupGoalEntry?> GetAsync(string goalId, CancellationToken cancellationToken = default);
}

// In-memory test double — same role as InMemoryWorkgroupRepositoryDirectory.
public sealed class InMemoryWorkgroupGoalDirectory : IWorkgroupGoalDirectory
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, WorkgroupGoalEntry> _entries = new(StringComparer.Ordinal);

    public Task<WorkgroupGoalEntry> RecordAsync(
        string goalId, IReadOnlyList<WorkgroupGoalRepoWorkUnit> repoWorkUnits, CancellationToken cancellationToken = default)
    {
        var entry = new WorkgroupGoalEntry(goalId, repoWorkUnits, DateTimeOffset.UtcNow);
        _entries[goalId] = entry;
        return Task.FromResult(entry);
    }

    public Task<WorkgroupGoalEntry?> GetAsync(string goalId, CancellationToken cancellationToken = default)
    {
        _entries.TryGetValue(goalId, out var entry);
        return Task.FromResult(entry);
    }
}

// Engine-backed production implementation — rides the "workgroup" room exactly like
// WorkgroupRepositoryDirectory does, in a separate namespace ("goals") so the two never collide.
public sealed class WorkgroupGoalDirectory : IWorkgroupGoalDirectory
{
    private const string GoalsNamespace = "goals";

    private readonly EngineRoomMap _roomMap;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    public WorkgroupGoalDirectory(
        IRuntimeCommandBridge bridge,
        RuntimeDagPersistenceService dagPersistence,
        IStudioReplicationOutbound replicationOutbound,
        ILogger<WorkgroupGoalDirectory> logger)
    {
        _roomMap = new EngineRoomMap(BoundRepoRooms.WorkgroupRoomId, _ => GoalsNamespace, bridge, dagPersistence, replicationOutbound, logger);
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

    public async Task<WorkgroupGoalEntry> RecordAsync(
        string goalId, IReadOnlyList<WorkgroupGoalRepoWorkUnit> repoWorkUnits, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var entry = new WorkgroupGoalEntry(goalId, repoWorkUnits, DateTimeOffset.UtcNow);
        await _roomMap.WriteEntryAsync(GoalsNamespace, goalId, ToEnvelope(entry), cancellationToken).ConfigureAwait(false);
        return entry;
    }

    public async Task<WorkgroupGoalEntry?> GetAsync(string goalId, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        var rawJson = _roomMap.TryGetValueRawJson(GoalsNamespace, goalId);
        return rawJson is null ? null : TryParseEntry(goalId, rawJson);
    }

    private static object ToEnvelope(WorkgroupGoalEntry entry) => new
    {
        v = 1,
        goalId = entry.GoalId,
        repoWorkUnits = entry.RepoWorkUnits.Select(r => new { repoId = r.RepoId, workUnitId = r.WorkUnitId }),
        createdAtUtc = entry.CreatedAt
    };

    private static WorkgroupGoalEntry? TryParseEntry(string goalId, string valueRawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(valueRawJson);
            var root = doc.RootElement;

            var repoWorkUnits = new List<WorkgroupGoalRepoWorkUnit>();
            if (root.TryGetProperty("repoWorkUnits", out var listEl) && listEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var entryEl in listEl.EnumerateArray())
                {
                    if (entryEl.ValueKind != JsonValueKind.Object) continue;
                    if (!entryEl.TryGetProperty("repoId", out var repoIdEl) || repoIdEl.ValueKind != JsonValueKind.String) continue;
                    if (!entryEl.TryGetProperty("workUnitId", out var workUnitIdEl) || workUnitIdEl.ValueKind != JsonValueKind.String) continue;
                    repoWorkUnits.Add(new WorkgroupGoalRepoWorkUnit(repoIdEl.GetString()!, workUnitIdEl.GetString()!));
                }
            }

            var createdAt = root.TryGetProperty("createdAtUtc", out var createdAtEl) && createdAtEl.TryGetDateTimeOffset(out var parsed)
                ? parsed
                : DateTimeOffset.UtcNow;

            return new WorkgroupGoalEntry(goalId, repoWorkUnits, createdAt);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
