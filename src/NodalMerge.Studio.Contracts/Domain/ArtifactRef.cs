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
}

public enum ArtifactStatus
{
    Active,
    Approved,
    Rejected,
    Superseded,
    Applied,
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
    string? Body = null);
