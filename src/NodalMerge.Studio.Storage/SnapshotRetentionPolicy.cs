using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 5 slice 5.1 — plans/cas-distribution-and-storage.md. Pure policy: reads the same
// StudioNodeKind.RepositorySnapshotV1/WorkUnitV1 rows IWorkspaceCacheManager.GetLiveBlobHashesAsync
// already reads (sibling read-model, same fail-closed philosophy adapted to classification
// instead of a single live/dead bit), and produces nothing more than a report. Nothing consumes
// this yet — that's slice 5.2.
//
// ── Fields confirmed in code before writing this (per the plan's "Open items" note) ──────────
//
// - Bootstrap-generation marker: RepositorySnapshot.Source == "Bootstrap" (RepositoryImportService.
//   BootstrapAsync — Case 1, the one-time CAS walk — is the only call site that passes this
//   literal, and it always creates Generation 0 for that repository, since RunSyncCoreAsync only
//   calls BootstrapAsync when GetLatestAsync(repositoryId) is null).
// - Applied-proposal -> snapshot link: NOT on MergeProposal. InMemoryMergeService.ApplyAsync's
//   BestEffortResyncAsync calls IRepositorySnapshotService.GetLatestAsync(writeBackPath) right
//   after the write-back resync and stamps that SnapshotId into the WORK UNIT's own
//   Metadata["appliedSnapshotId"] (see the SetMetadataAsync call a few lines later) — never onto
//   the MergeProposal record itself. WorkspaceCacheManager.PassesSafeEvictionInvariantAsync reads
//   this exact key; this policy reads the same key the same way.
// - Branch seed / merge base: as of Phase 6 slice 6.5 Part 2, WorkUnit.SeedSnapshotId carries this
//   exactly — InMemoryWorkUnitService.CreateWorkUnitAsync stamps the repository's actual
//   current-head RepositorySnapshot.SnapshotId at the moment the branch is seeded. ClassifyAsync
//   below uses it directly when present. Everything BELOW this line describes the proxy this
//   policy fell back to before 6.5 landed the real FK, kept verbatim because it is still the exact
//   fallback for every work unit created before 6.5 (or whose pinned id somehow fails to resolve —
//   see the pinned-then-fallback branch in the loop below): NO persisted field carried this.
//   WorkUnitFanOutInfo.SeedFromBranchId is a BranchId, not a RepositorySnapshot id, and
//   FileSystemWorkspaceService.InitBranchAsync seeds a branch by copying another branch's
//   DIRECTORY, never by recording which CAS generation it started from. RepositorySnapshot.WorkUnitId
//   exists on the record but no current CreateAsync call site (RepositoryImportService,
//   InMemoryRepositorySnapshotService.ConsiderCompactionAsync) ever passes a non-null value for it.
//   Absent the real FK, this policy derived the seed: for a non-terminal work unit, the protected
//   generation is the latest RepositorySnapshot for its repository whose CreatedAt is at or before
//   the work unit's own CreatedAt — the generation that was actually live when the branch was
//   created, which is what RepositorySyncService/RepositoryImportService would have
//   materialized/diffed against at that moment. "Merge base" collapses to the same reference:
//   nothing in the current apply/drift-check path (InMemoryMergeService's drift detection compares
//   against a "base/{proposalId}" file-workspace branch copy, never a RepositorySnapshot id) tracks
//   a separate proposal-time base generation.
// - Terminal work-unit statuses (for both the Active-class non-terminal check and the Intermediate
//   age-out clock): WorkUnitTransitions.CanTransition's switch has ZERO outgoing edges for
//   Completed, Merged, and Failed — no code path ever revives a work unit from any of these three,
//   so they're the true terminal set for retention purposes. This deliberately differs from
//   WorkspaceCacheManager.TerminalEvictableStatuses ({Completed, Merged, Cancelled}), which answers
//   a narrower question ("is this ephemeral working DIRECTORY safe to delete") — Cancelled's
//   content was "never merged" so its dir is always safe to evict, but Cancelled itself still has
//   live revival edges (Cancelled -> Queued/Executing) in the transition table, and DeadLettered
//   has even more (-> Retrying/Proposed/Merged/Queued/Executing) — both can resume the SAME branch
//   and would need its seed generation's bytes still present, so both count as non-terminal here.
// - Current head: not a separate sync-state pointer (RepositorySyncStateV1's own Generation field
//   is a distinct per-BranchId external-drift counter, scoped to "main" only, and unrelated to
//   RepositorySnapshot.Generation). Computed the same way
//   InMemoryRepositorySnapshotService.GetLatestAsync does internally: per RepositoryId, the stored
//   snapshot with the highest Generation.
//
// ── Intermediate age-out timestamp ────────────────────────────────────────────────────────────
//
// Best available per-snapshot timestamp for "its branch reaching a terminal state": if
// RepositorySnapshot.WorkUnitId names a work unit that exists and is in a terminal status, that
// work unit's UpdatedAt is used — WorkUnit.UpdatedAt is bumped on every status transition
// (InMemoryWorkUnitService.UpdateStatusAsync), unconditionally, unlike the optional
// WorkUnitStatusChanged ExecutionEventV1 row (only ever emitted when the caller passes a
// sessionId), making it the more reliable of the two "when did this become terminal" signals
// actually available. In today's wiring, no CreateAsync call site sets RepositorySnapshot.WorkUnitId
// yet (see above), so this falls back to the snapshot's own CreatedAt for essentially every
// existing snapshot in practice — exactly the fallback the plan calls for, ready to engage the
// moment a future slice (Phase 7's SnapshotOnWorkUnitCompletion, referenced in
// IRepositorySnapshotService's own doc comment) starts populating WorkUnitId at creation time.
public sealed class SnapshotRetentionPolicy(
    IStudioNodeStore nodeStore,
    IRepositoryRegistryService repositories,
    WorkspaceOptions? options = null,
    RetentionPolicyOptions? retentionOptions = null,
    TimeProvider? clock = null) : ISnapshotRetentionPolicy
{
    private static readonly HashSet<WorkUnitStatus> TerminalStatuses =
    [
        WorkUnitStatus.Completed,
        WorkUnitStatus.Merged,
        WorkUnitStatus.Failed,
    ];

    public async Task<SnapshotRetentionReport> ClassifyAsync(CancellationToken ct = default)
    {
        var retainDays = retentionOptions?.RetainIntermediateDays ?? new RetentionPolicyOptions().RetainIntermediateDays;
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var anomalies = new List<string>();

        // ── Load + tolerate malformed nodes (fail-closed philosophy adapted to classification:
        // a snapshot that can't be read is reported Active-with-anomaly below, never silently
        // skipped and never allowed to fall through as expirable). ─────────────────────────────
        var (validSnapshots, malformedSnapshotIds) = await ReadAllNodesTolerantAsync<RepositorySnapshot>(
            StudioNodeKind.RepositorySnapshotV1, ct).ConfigureAwait(false);

        var (validWorkUnits, malformedWorkUnitIds) = await ReadAllNodesTolerantAsync<WorkUnit>(
            StudioNodeKind.WorkUnitV1, ct).ConfigureAwait(false);

        if (malformedWorkUnitIds.Count > 0)
        {
            anomalies.Add(
                $"{malformedWorkUnitIds.Count} work-unit node(s) failed to parse — their branch " +
                "seed/merge-base contribution (if any) could not be computed. Fail-safe bias means " +
                "nothing was demoted because of this, but the affected work units' true generation " +
                "dependency is unknown.");
        }

        var workUnitsById = validWorkUnits.ToDictionary(w => w.WorkUnitId, StringComparer.Ordinal);

        // ── Current head per repository (highest Generation; CreatedAt then SnapshotId break
        // ties so the choice is fully deterministic). ──────────────────────────────────────────
        var headByRepo = new Dictionary<string, RepositorySnapshot>(StringComparer.Ordinal);
        foreach (var snap in validSnapshots)
        {
            if (!headByRepo.TryGetValue(snap.RepositoryId, out var existing)
                || snap.Generation > existing.Generation
                || (snap.Generation == existing.Generation && snap.CreatedAt > existing.CreatedAt)
                || (snap.Generation == existing.Generation && snap.CreatedAt == existing.CreatedAt
                    && string.CompareOrdinal(snap.SnapshotId, existing.SnapshotId) > 0))
            {
                headByRepo[snap.RepositoryId] = snap;
            }
        }

        // ── Pinned: bootstrap generation, applied-proposal stamp, admin pins. ──────────────────
        var pinnedReasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void AddPinned(string snapshotId, string reason)
        {
            if (!pinnedReasons.TryGetValue(snapshotId, out var list))
                pinnedReasons[snapshotId] = list = [];
            list.Add(reason);
        }

        foreach (var snap in validSnapshots)
        {
            if (string.Equals(snap.Source, "Bootstrap", StringComparison.Ordinal))
                AddPinned(snap.SnapshotId, $"Bootstrap generation for repository '{snap.RepositoryId}'.");
        }

        foreach (var wu in validWorkUnits)
        {
            if (wu.Metadata is { } metadata
                && metadata.TryGetValue("appliedSnapshotId", out var appliedSnapshotId)
                && !string.IsNullOrEmpty(appliedSnapshotId))
            {
                AddPinned(appliedSnapshotId,
                    $"Applied merge proposal write-back for work unit '{wu.WorkUnitId}' " +
                    "(Metadata[\"appliedSnapshotId\"] stamped by the apply-time resync).");
            }
        }

        foreach (var pinnedId in retentionOptions?.AdminPinnedSnapshotIds ?? [])
        {
            if (!string.IsNullOrWhiteSpace(pinnedId))
                AddPinned(pinnedId, "Admin pin (RetentionPolicyOptions.AdminPinnedSnapshotIds).");
        }

        // ── Active: current head per repo, and non-terminal work units' branch seed / merge base.
        var activeReasons = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        void AddActive(string snapshotId, string reason)
        {
            if (!activeReasons.TryGetValue(snapshotId, out var list))
                activeReasons[snapshotId] = list = [];
            list.Add(reason);
        }

        foreach (var (repoId, head) in headByRepo)
            AddActive(head.SnapshotId, $"Current head (Generation={head.Generation}) for repository '{repoId}'.");

        var snapshotsById = validSnapshots.ToDictionary(s => s.SnapshotId, StringComparer.Ordinal);

        foreach (var wu in validWorkUnits)
        {
            if (TerminalStatuses.Contains(wu.Status)) continue;

            // Slice 6.5 Part 2 — WorkUnit.SeedSnapshotId is the exact FK the class doc comment's
            // "5.1 findings" flagged as missing: every work unit created since that slice shipped
            // stamps its repository's actual current-head snapshot id at seed time
            // (InMemoryWorkUnitService.CreateWorkUnitAsync), so merge-base liveness for it is exact
            // rather than approximated. A pinned id that doesn't resolve to a known snapshot (should
            // never happen — snapshots are append-only, AP-5 — but node loss/corruption is exactly
            // what "fail toward protecting" means here) falls through to the timestamp proxy rather
            // than silently treating the work unit as having no seed at all.
            if (wu.SeedSnapshotId is { } pinnedSeedId && snapshotsById.TryGetValue(pinnedSeedId, out var pinnedSeed))
            {
                AddActive(pinnedSeed.SnapshotId,
                    $"Branch seed / merge base for non-terminal work unit '{wu.WorkUnitId}' " +
                    $"(Status={wu.Status}) — pinned via WorkUnit.SeedSnapshotId (exact, not the CreatedAt proxy).");
                continue;
            }

            var repositoryId = await ResolveRepositoryIdAsync(wu, ct).ConfigureAwait(false);

            // Legacy proxy (pre-6.5, or a pinned id that failed to resolve above): the generation
            // that was live for this repository at (or just before) the moment this work unit's
            // branch was created — see the class doc comment for why this is the best available
            // approximation of "branch seed / merge base" absent a persisted FK.
            RepositorySnapshot? seed = null;
            foreach (var snap in validSnapshots)
            {
                if (!string.Equals(snap.RepositoryId, repositoryId, StringComparison.Ordinal)) continue;
                if (snap.CreatedAt > wu.CreatedAt) continue;
                if (seed is null
                    || snap.CreatedAt > seed.CreatedAt
                    || (snap.CreatedAt == seed.CreatedAt && snap.Generation > seed.Generation))
                {
                    seed = snap;
                }
            }

            if (seed is null) continue; // no generation predates this unit yet — nothing to protect

            AddActive(seed.SnapshotId,
                $"Branch seed / merge base for non-terminal work unit '{wu.WorkUnitId}' " +
                $"(Status={wu.Status}, created {wu.CreatedAt:O}) — CreatedAt proxy (no SeedSnapshotId).");
        }

        // ── Assemble: Pinned > Active > Intermediate precedence per snapshot. ──────────────────
        var entries = new List<SnapshotRetentionEntry>();
        var retained = new HashSet<string>(StringComparer.Ordinal);
        int pinnedCount = 0, activeCount = 0, intermediateRetained = 0, intermediateExpired = 0;

        foreach (var snap in validSnapshots
            .OrderBy(s => s.RepositoryId, StringComparer.Ordinal)
            .ThenBy(s => s.Generation))
        {
            if (pinnedReasons.TryGetValue(snap.SnapshotId, out var pinnedWhy))
            {
                entries.Add(new SnapshotRetentionEntry(
                    snap.SnapshotId, snap.RepositoryId, SnapshotRetentionClass.Pinned,
                    string.Join(" ", pinnedWhy), Retained: true, ExpiresAt: null));
                retained.Add(snap.SnapshotId);
                pinnedCount++;
                continue;
            }

            if (activeReasons.TryGetValue(snap.SnapshotId, out var activeWhy))
            {
                entries.Add(new SnapshotRetentionEntry(
                    snap.SnapshotId, snap.RepositoryId, SnapshotRetentionClass.Active,
                    string.Join(" ", activeWhy), Retained: true, ExpiresAt: null));
                retained.Add(snap.SnapshotId);
                activeCount++;
                continue;
            }

            string basis;
            DateTimeOffset baseTimestamp;
            if (snap.WorkUnitId is { } producerId
                && workUnitsById.TryGetValue(producerId, out var producer)
                && TerminalStatuses.Contains(producer.Status))
            {
                baseTimestamp = producer.UpdatedAt;
                basis = $"work unit '{producerId}' terminal transition ({producer.Status} at {producer.UpdatedAt:O})";
            }
            else
            {
                baseTimestamp = snap.CreatedAt;
                basis = $"snapshot CreatedAt ({snap.CreatedAt:O}) — no linked terminal work unit";
            }

            var expiresAt = baseTimestamp + TimeSpan.FromDays(retainDays);
            var isRetained = now < expiresAt;
            if (isRetained) { retained.Add(snap.SnapshotId); intermediateRetained++; }
            else intermediateExpired++;

            entries.Add(new SnapshotRetentionEntry(
                snap.SnapshotId, snap.RepositoryId, SnapshotRetentionClass.Intermediate,
                $"Intermediate; age-out basis: {basis}; RetainIntermediateDays={retainDays}; " +
                $"expires {expiresAt:O}; {(isRetained ? "not yet expired" : "expired")}.",
                isRetained, expiresAt));
        }

        // ── Malformed snapshot nodes: fail-safe bias — Active, never silently dropped. ─────────
        foreach (var badId in malformedSnapshotIds)
        {
            entries.Add(new SnapshotRetentionEntry(
                badId, RepositoryId: null, SnapshotRetentionClass.Active,
                "Anomaly: snapshot node failed to deserialize (malformed JSON) — classified Active " +
                "per fail-safe bias rather than risk under-protecting live bytes.",
                Retained: true, ExpiresAt: null));
            retained.Add(badId);
            activeCount++;
        }

        return new SnapshotRetentionReport(
            entries, retained, pinnedCount, activeCount, intermediateRetained, intermediateExpired,
            now, anomalies);
    }

    // Mirrors WorkspaceCacheManager.GetRepositoryIdAsync exactly: prefers the work unit's own
    // registered repository (multi-repo goals) over the global default, matching the snapshot
    // store's actual RepositoryId convention (Path.GetFullPath of the physical repo path).
    private async Task<string> ResolveRepositoryIdAsync(WorkUnit workUnit, CancellationToken ct)
    {
        if (workUnit.RepositoryId is { } repositoryId)
        {
            var repository = await repositories.GetAsync(repositoryId, ct).ConfigureAwait(false);
            if (repository is not null)
                return Path.GetFullPath(repository.Path);
        }

        return Path.GetFullPath(options?.SeedRepositoryPath ?? Directory.GetCurrentDirectory());
    }

    // Deserializes every node of `kind`, tolerating malformed JSON per-node (same fail-closed
    // philosophy as WorkspaceCacheManager.GetLiveBlobHashesAsync's own per-node try/catch) —
    // returns the entity ids that failed to parse alongside the successfully parsed items, so
    // callers can decide how to report the failure instead of it being silently swallowed.
    private async Task<(List<T> Valid, List<string> MalformedEntityIds)> ReadAllNodesTolerantAsync<T>(
        string kind, CancellationToken ct) where T : class
    {
        var nodes = await nodeStore.ReadAllNodesAsync(kind, ct).ConfigureAwait(false);
        var valid = new List<T>(nodes.Count);
        var malformed = new List<string>();
        foreach (var (entityId, json) in nodes)
        {
            T? parsed;
            try { parsed = JsonSerializer.Deserialize<T>(json); }
            catch { parsed = null; }
            if (parsed is null) malformed.Add(entityId);
            else valid.Add(parsed);
        }
        return (valid, malformed);
    }
}
