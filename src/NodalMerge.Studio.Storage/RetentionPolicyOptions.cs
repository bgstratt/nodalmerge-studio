namespace NodalMerge.Studio.Storage;

// Phase 5 slice 5.1 — configuration for ISnapshotRetentionPolicy. Mirrors WorkspaceOptions'
// plain-class-with-defaults pattern (see WorkspaceOptions.cs): registered as a singleton with
// sensible defaults so every caller can take it as a required-or-null dependency, and a host
// (e.g. NodalMerge.Studio.Host's StudioServiceCollectionExtensions) may later bind it from
// configuration and re-register (last AddSingleton registration wins, same convention
// WorkspaceOptions documents).
public sealed class RetentionPolicyOptions
{
    // How long an Intermediate-class generation stays materializable after its branch reaches a
    // terminal state (or, absent a better timestamp, after the snapshot's own CreatedAt) — see
    // SnapshotRetentionPolicy's own doc comment for exactly which timestamp is used.
    public int RetainIntermediateDays { get; set; } = 30;

    // Admin pin seam (plan's Phase 5 slice 5.1 row: "admin pins — define the seam ... empty
    // default — don't build pin management UI"). Any SnapshotId listed here is always classified
    // Pinned, regardless of every other rule. Deliberately a plain list, not a service interface:
    // there is no UI or command to populate it yet: whatever process eventually manages pins can
    // configure this the same way every other WorkspaceOptions list-valued knob is configured
    // (e.g. DocFetchAllowedDomains), and callers needing a fixed answer (Yes we truly never need
    // to pin anything programmatically) inherit the correct empty-set default for free.
    public List<string> AdminPinnedSnapshotIds { get; set; } = [];
}
