using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.Host.Composition;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Thrown when a GC run can't even start because CAS storage isn't configured
// (WorkspaceOptions.CasRootPath is null) — deliberately NOT an InvalidOperationException, so the
// REST layer can tell "not configured" (400, a caller error) apart from
// IWorkspaceCacheManager.GetLiveBlobHashesAsync's fail-closed InvalidOperationException ("an
// incomplete live set, skip this round" — 409, same convention /studio/cache/gc and
// /studio/cas/reconcile already established before this slice).
public sealed class BlobGcNotConfiguredException(string message) : Exception(message);

// plans/cas-distribution-and-storage.md Phase 5 slice 5.2 — staged local blob GC + run ledger.
//
// Mode -> FileBlobGcCoordinator mapping (the honest collapse the plan asks for, since the
// installed package's coordinator only exposes a grace-window length and a
// RequireTombstoneBeforeDelete flag over one mark-then-sweep LiveRun call, not a distinct
// per-mode coordinator surface):
//
//   DryRun    -> coordinator.DryRun(live, now). The coordinator already computes the exact same
//                Marked/Cleared/DeleteCandidates/Deleted counts a live run would act on without
//                writing anything — that is what "DryRun reports the same candidate set the live
//                run would act on" means, verbatim, not something reimplemented here.
//   MarkOnly  -> coordinator.LiveRun(live, now) with GraceWindow = TimeSpan.MaxValue. Newly
//                non-live blobs DO get a real tombstone file written this time (unlike DryRun),
//                but the grace-elapsed check (`tombAge < GraceWindow`) can never be false, so
//                Deleted is always 0.
//   SweepSoft -> coordinator.LiveRun(live, now) with GraceWindow = BlobGcOptions.GraceHours
//                (default 24h) — the coordinator's own default two-phase behavior: mark this run,
//                delete only blobs a *previous* run already tombstoned once grace has elapsed.
//   SweepHard -> coordinator.LiveRun(live, now) with GraceWindow = TimeSpan.Zero. A blob
//                tombstoned by an *earlier* call (even moments earlier) is immediately eligible
//                for deletion on this call. RequireTombstoneBeforeDelete stays true even here:
//                the coordinator has no knob to delete a hash the very first time it's observed
//                non-live in a single call — doing so would let one stale/incomplete live-set
//                computation destroy bytes with zero recovery window, which is exactly the
//                mark-then-sweep separation the package's own design exists to prevent. So
//                "hard" reclaims aggressively fast (two calls, zero wait between them) rather
//                than eliding the two-phase design entirely — reported here rather than silently
//                assumed away.
public sealed class BlobGcService(
    IWorkspaceCacheManager cacheManager,
    ISnapshotRetentionPolicy retentionPolicy,
    IStudioNodeStore nodeStore,
    WorkspaceOptions options,
    BlobGcOptions? gcOptions = null,
    TimeProvider? clock = null,
    ILogger<BlobGcService>? logger = null) : IBlobGcService
{
    public async Task<BlobGcRunRecord> RunAsync(BlobGcMode? modeOverride = null, CancellationToken ct = default)
    {
        var casRoot = options.CasRootPath;
        if (string.IsNullOrEmpty(casRoot))
            throw new BlobGcNotConfiguredException(
                "CAS storage is not configured (WorkspaceOptions.CasRootPath is null).");

        var mode = modeOverride ?? gcOptions?.Mode ?? BlobGcMode.DryRun;
        var clockToUse = clock ?? TimeProvider.System;
        var startedAt = clockToUse.GetUtcNow();
        var runId = $"gcrun-{startedAt:yyyyMMddHHmmssfffffff}-{Guid.NewGuid():N}";

        IReadOnlySet<string> live;
        SnapshotRetentionReport report;
        try
        {
            // Slice 5.2 — GetLiveBlobHashesAsync's fail-closed contract (throws InvalidOperationException
            // rather than return a partial set) is unchanged by this slice; it's exactly what must
            // abort this run with zero coordinator calls (so zero deletes) when a retained
            // snapshot's tree can't be resolved.
            live = await cacheManager.GetLiveBlobHashesAsync(ct).ConfigureAwait(false);
            report = await retentionPolicy.ClassifyAsync(ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            logger?.LogWarning(ex,
                "Blob GC run {RunId} aborted — the live blob set could not be computed (fail-closed).",
                runId);
            await RecordFailureAsync(runId, mode, startedAt, ex.Message, clockToUse, ct).ConfigureAwait(false);
            throw;
        }

        var policy = BuildPolicy(mode, gcOptions);
        var coordinator = new FileBlobGcCoordinator(casRoot, policy);
        var fileReport = mode == BlobGcMode.DryRun
            ? coordinator.DryRun(live, startedAt)
            : coordinator.LiveRun(live, startedAt);

        var finishedAt = clockToUse.GetUtcNow();
        var record = new BlobGcRunRecord(
            RunId: runId,
            Mode: mode,
            StartedAt: startedAt,
            FinishedAt: finishedAt,
            Scanned: fileReport.Scanned,
            Marked: fileReport.Marked,
            Cleared: fileReport.Cleared,
            DeleteCandidates: fileReport.DeleteCandidates,
            Deleted: fileReport.Deleted,
            LiveSetSize: live.Count,
            RetainedSnapshotCount: report.RetainedSnapshotIds.Count,
            AnomalyCount: report.Anomalies.Count,
            DurationMs: (long)(finishedAt - startedAt).TotalMilliseconds,
            Success: true);

        await nodeStore.WriteNodeAsync(StudioNodeKind.GcRunV1, runId, JsonSerializer.Serialize(record), ct)
            .ConfigureAwait(false);

        logger?.LogInformation(
            "Blob GC run {RunId} complete: mode={Mode} scanned={Scanned} marked={Marked} deleted={Deleted} liveSetSize={LiveSetSize} retainedSnapshots={RetainedSnapshotCount}",
            runId, mode, fileReport.Scanned, fileReport.Marked, fileReport.Deleted, live.Count, report.RetainedSnapshotIds.Count);

        return record;
    }

    public async Task<IReadOnlyList<BlobGcRunRecord>> GetRecentRunsAsync(int limit = 20, CancellationToken ct = default)
    {
        var nodes = await nodeStore.ReadAllNodesAsync(StudioNodeKind.GcRunV1, ct).ConfigureAwait(false);
        var records = new List<BlobGcRunRecord>(nodes.Count);
        foreach (var (_, json) in nodes)
        {
            try
            {
                var record = JsonSerializer.Deserialize<BlobGcRunRecord>(json);
                if (record is not null) records.Add(record);
            }
            catch { /* malformed row — skip, same tolerant-read convention as everywhere else */ }
        }

        return records
            .OrderByDescending(r => r.FinishedAt)
            .Take(Math.Max(0, limit))
            .ToList();
    }

    private async Task RecordFailureAsync(
        string runId, BlobGcMode mode, DateTimeOffset startedAt, string error, TimeProvider clockToUse, CancellationToken ct)
    {
        var finishedAt = clockToUse.GetUtcNow();
        var record = new BlobGcRunRecord(
            RunId: runId, Mode: mode, StartedAt: startedAt, FinishedAt: finishedAt,
            Scanned: 0, Marked: 0, Cleared: 0, DeleteCandidates: 0, Deleted: 0,
            LiveSetSize: 0, RetainedSnapshotCount: 0, AnomalyCount: 0,
            DurationMs: (long)(finishedAt - startedAt).TotalMilliseconds,
            Success: false, Error: error);

        try
        {
            await nodeStore.WriteNodeAsync(StudioNodeKind.GcRunV1, runId, JsonSerializer.Serialize(record), ct)
                .ConfigureAwait(false);
        }
        catch
        {
            // Best-effort ledger write — never mask the original fail-closed exception over this.
        }
    }

    private static FileBlobGcPolicy BuildPolicy(BlobGcMode mode, BlobGcOptions? opts)
    {
        var maxDeletes = opts?.MaxDeletesPerRun ?? FileBlobGcPolicy.Default.MaxDeletesPerRun;
        return mode switch
        {
            BlobGcMode.MarkOnly => new FileBlobGcPolicy(
                GraceWindow: TimeSpan.MaxValue, MaxDeletesPerRun: maxDeletes, RequireTombstoneBeforeDelete: true),
            BlobGcMode.SweepSoft => new FileBlobGcPolicy(
                GraceWindow: TimeSpan.FromHours(opts?.GraceHours ?? 24), MaxDeletesPerRun: maxDeletes, RequireTombstoneBeforeDelete: true),
            BlobGcMode.SweepHard => new FileBlobGcPolicy(
                GraceWindow: TimeSpan.Zero, MaxDeletesPerRun: maxDeletes, RequireTombstoneBeforeDelete: true),
            // DryRun never applies changes, so the policy's grace/delete knobs are inert — Default
            // is used only for its MaxDeletesPerRun-independent DeleteCandidates accounting.
            _ => FileBlobGcPolicy.Default,
        };
    }
}
