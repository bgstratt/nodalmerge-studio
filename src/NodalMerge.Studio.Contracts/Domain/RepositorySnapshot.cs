namespace NodalMerge.Studio.Contracts.Domain;

public sealed record RepositorySnapshot(
    string SnapshotId,
    string RepositoryId,
    string TreeHash,
    long Generation,
    DateTimeOffset CreatedAt,
    string? BaseSnapshotId = null,
    string? GitCommit = null,
    string? WorkUnitId = null,
    // "Bootstrap" | "WorkUnitCompletion" | "ImportedFromFilesystem"
    string? Source = null,
    // path → blobId map; null for pre-Phase-2 bootstrap snapshots written without this field, and
    // null for slice-1.1-and-later ("cas-tree") snapshots that store the map in the CAS instead
    // (see TreeFormat). Legacy fallback and cas-tree resolution both go through
    // ISnapshotTreeResolver.ResolveTreeAsync — no reader should dereference this field directly.
    IReadOnlyDictionary<string, string>? TreeEntries = null,
    // plans/cas-distribution-and-storage.md Phase 1 slice 1.1 — "cas-tree" marks a snapshot whose
    // tree map lives in the CAS as a blob (TreeHash = BLAKE3 of that blob's bytes, canonical v1
    // flat encoding per docs/TREE_OBJECT_FORMAT.md). Null for every snapshot written before this
    // slice (inline TreeEntries) and for pre-Phase-2 snapshots (neither field set) — existing nodes
    // are never rewritten (append-only, AP-5).
    string? TreeFormat = null);
