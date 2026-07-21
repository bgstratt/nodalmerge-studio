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
//   age-out clock): only Completed and Merged — the frozen `terminal_status_names` set (see the
//   TerminalStatuses field below for the full rationale). This ORIGINALLY also listed Failed on the
//   belief that it "has zero outgoing edges", but that was wrong: WorkUnit.cs's
//   `(_, Cancelled) when from is not Completed and not Merged` rule gives Failed a live revival edge
//   (Failed -> Cancelled -> Queued/Executing), so a failed work unit IS resumable and its seed must
//   survive. finding #30 corrected the Rust GC and the frozen vector; this comment and the C# set
//   are the belated C#-side correction. Also non-terminal for the same reason: Cancelled
//   (-> Queued/Executing) and DeadLettered (-> Retrying/Proposed/Merged/Queued/Executing) — both can
//   resume the SAME branch and would need its seed generation's bytes still present.
//   This deliberately differs from WorkspaceCacheManager.TerminalEvictableStatuses
//   ({Completed, Merged, Cancelled}), which answers a narrower question ("is this ephemeral working
//   DIRECTORY safe to delete") — Cancelled's content was "never merged" so its dir is always safe to
//   evict, even though Cancelled's seed generation (a different, CAS-plane thing) must be retained.
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
    // The GC/retention-terminal set: a work unit whose seed/merge-base snapshot can be aged out
    // because the unit can never come back to need it. This MUST match the frozen contract
    // `terminal_status_names` (work-unit-status-vectors.v1.json) = {Completed, Merged}, which nodalmerge's
    // Rust GC coordinator (server/server/src/studio_live_hashes.rs) also uses — the two GC systems
    // delete real blobs and must agree, or one deletes a seed the other considers live.
    //
    // Deliberately NOT terminal here (all have live revival edges in WorkUnitTransitions, so a human
    // can bring them back and their seed must survive): Cancelled (-> Queued/Executing), DeadLettered
    // (-> Retrying/Queued/Executing/...), and Failed. Failed was the straggler: finding #30 (slice 1.6)
    // removed it from the Rust set and the frozen vector because `Failed -> Cancelled -> Queued`/
    // `Executing` is a real human revival path ("a failed work unit IS resumable"), but this C# policy
    // — which BlobGcService and WorkspaceCacheManager both delete/evict against — still listed it, so
    // it aged out and deleted the seed blobs of revivable Failed units. Removing it aligns C# GC with
    // the Rust GC and the contract, and errs toward retaining (never deleting a still-revivable seed).
    private static readonly HashSet<WorkUnitStatus> TerminalStatuses =
    [
        WorkUnitStatus.Completed,
        WorkUnitStatus.Merged,
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

            // Slice 7.2 — ResolveRepositoryIdAsync now throws RepositoryIdentityUnresolvedException
            // for a work unit whose RepositoryId can't be bound anywhere on this peer (rather than
            // silently guessing the global default). Retention's own fail-safe bias (see the class
            // doc comment: malformed nodes -> Active, never silently dropped) means an unresolvable
            // identity here must NOT abort classification for every other repository's snapshots —
            // it degrades to "this work unit's CreatedAt-proxy seed couldn't be computed this pass",
            // recorded as an anomaly, exactly like a malformed work-unit node above.
            string repositoryId;
            try
            {
                repositoryId = await ResolveRepositoryIdAsync(wu, ct).ConfigureAwait(false);
            }
            catch (RepositoryIdentityUnresolvedException ex)
            {
                anomalies.Add(
                    $"Work unit '{wu.WorkUnitId}' RepositoryId '{ex.RepositoryId}' could not be " +
                    "resolved to a CAS identity on this peer — its CreatedAt-proxy branch seed / " +
                    "merge-base generation could not be computed this pass.");
                continue;
            }

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

    // Slice 7.2 — mirrors WorkspaceCacheManager.GetRepositoryIdAsync exactly: resolves the work
    // unit's own repository (multi-repo goals) via the workgroup-portable identity chain
    // (IRepositoryRegistryService.ResolveCasIdentityAsync — sticky to any already-existing chain,
    // so pre-7.2 single-peer workspaces resolve exactly as before), rather than the old convention
    // that only ever looked at THIS peer's own registry cache (silently falling back to the global
    // default for a FOREIGN RepositoryId replicated from a different peer).
    private async Task<string> ResolveRepositoryIdAsync(WorkUnit workUnit, CancellationToken ct)
    {
        if (workUnit.RepositoryId is { } repositoryId)
        {
            var resolved = await repositories.ResolveCasIdentityAsync(repositoryId, null, ct).ConfigureAwait(false);
            if (resolved is not null)
                return resolved;
            throw new RepositoryIdentityUnresolvedException(repositoryId);
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
