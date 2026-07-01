namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Evidence record stored under studio/evidence/v1 — attaches build/test verification data
/// to a work unit or proposal, forming the evidentiary basis for decisions.
/// </summary>
public sealed record EvidenceNode(
    string EvidenceId,
    string WorkUnitId,
    string? ProposalId,
    EvidenceKind Kind,
    string Summary,
    string? DetailJson,
    DateTimeOffset AttachedAt,
    string? SessionId = null);

public enum EvidenceKind
{
    Build,
    Test,
    Lint,
    PolicyEvaluation,
    AutomatedReview
}