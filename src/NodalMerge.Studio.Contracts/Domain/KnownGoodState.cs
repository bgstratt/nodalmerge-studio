namespace NodalMerge.Studio.Contracts.Domain;

public sealed record KnownGoodState(
    string StateId,
    string BranchId,
    string Description,
    string? VerificationResults,
    DateTimeOffset CreatedAt,
    string CreatedBy);
