namespace NodalMerge.Studio.Contracts.Mcp.State;

public sealed record StateMarkKnownGoodRequest(
    string BranchId,
    string Description,
    string? VerificationResults = null,
    string? CreatedBy = null);

public sealed record StateFindKnownGoodRequest(string BranchId);

public sealed record StateCheckoutKnownGoodRequest(string KnownGoodStateId);

public sealed record StateMarkKnownGoodResponse(string StateId);
