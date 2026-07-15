using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 5 slice 5.2 — configuration for IBlobGcService (the staged local sweep) and
// BlobGcBackgroundService (the scheduled run). Mirrors WorkspaceOptions/RetentionPolicyOptions'
// plain-class-with-defaults pattern: registered as a singleton with sensible (safe) defaults so
// every caller can take it as a required-or-null dependency, and a host may later bind it from
// configuration and re-register (last AddSingleton registration wins, same convention
// WorkspaceOptions documents — see NodalMerge.Studio.Host.StudioServiceCollectionExtensions).
public sealed class BlobGcOptions
{
    // DryRun by default — a naked POST /studio/cache/gc (or an enabled scheduled run) must never
    // delete bytes until an operator deliberately configures/requests a live mode.
    public BlobGcMode Mode { get; set; } = BlobGcMode.DryRun;

    // Passed straight through to FileBlobGcPolicy.MaxDeletesPerRun.
    public int MaxDeletesPerRun { get; set; } = 100;

    // SweepSoft's tombstone grace window, in hours, before an already-marked blob becomes a
    // delete candidate. Matches FileBlobGcPolicy.Default's own 24h. Unused by DryRun/MarkOnly
    // (MarkOnly always uses an effectively-infinite grace so it never deletes) and by SweepHard
    // (always zero grace, by definition of the mode) — see BlobGcService.BuildPolicy.
    public int GraceHours { get; set; } = 24;

    // Interval for the scheduled background run (BlobGcBackgroundService). 0 = disabled (the
    // default) — an operator opts in explicitly, mirroring RetentionPolicyOptions' "empty default,
    // no seam built unless asked" stance.
    public int GcIntervalMinutes { get; set; } = 0;
}
