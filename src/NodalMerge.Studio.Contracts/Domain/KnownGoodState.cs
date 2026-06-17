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
    string? SnapshotBranchId = null);
