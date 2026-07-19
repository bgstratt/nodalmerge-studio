namespace NodalMerge.Studio.Contracts.Domain;

public sealed record KnownGoodState(
    string StateId,
    string BranchId,
    string Description,
    string? VerificationResults,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    // The branch holding a point-in-time copy of BranchId's files as of MarkKnownGoodAsync —
    // what CheckoutKnownGoodAsync restores from. Null for states persisted before 13e.
    string? SnapshotBranchId = null,
    // Slice 6.3a — resolved from BranchId's own stored BranchV1.RepositoryId at MarkKnownGoodAsync
    // time (a known-good state has no WorkUnitId of its own to chain through). Null when BranchId's
    // own RepositoryId never resolved (an ad hoc/global branch, or a pre-6.3a BranchV1 row), or the
    // state predates 6.3a.
    string? RepositoryId = null);
