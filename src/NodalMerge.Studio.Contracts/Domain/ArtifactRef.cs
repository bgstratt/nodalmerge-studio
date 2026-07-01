namespace NodalMerge.Studio.Contracts.Domain;

public enum ArtifactType
{
    Goal,
    Plan,
    Task,
    Research,
    Decision,
    Constraint,
    BranchChangeset,
    MergeProposal,
    MergeResult,
    ChangeIntent,
    ExternalChangeset,
}

public enum ArtifactStatus
{
    Active,
    Approved,
    Rejected,
    Superseded,
    Applied,
    Invalidated,
}

public sealed record ArtifactRef(
    string ArtifactId,
    ArtifactType Type,
    string? ParentArtifactId,
    ArtifactStatus Status,
    DateTimeOffset CreatedAt,
    string? OwnedByWorkUnitId,
    string? OwnedByAgentId,
    string? Title = null,
    string? Body = null,
    // Capability-gap fix — set on a descendant (in the ParentArtifactId chain) when an ancestor is
    // invalidated. Distinct from Status: the descendant's own status (e.g. a MergeProposal's
    // Applied) is left untouched, this only flags that something it was built on is now stale.
    string? InvalidatedByArtifactId = null);
