namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Hypothesis fork record stored under studio/hypothesis/v1 — captures a forked exploration
/// path with its type classification and lineage from a parent work unit or proposal.
/// </summary>
public sealed record HypothesisNode(
    string HypothesisId,
    string WorkUnitId,
    string Goal,
    HypothesisForkType ForkType,
    HypothesisStatus Status,
    string? ParentWorkUnitId,
    string? BranchedFromProposalId,
    string? Rationale,
    DateTimeOffset CreatedAt,
    string? SessionId = null);

public enum HypothesisForkType
{
    Code,
    Reasoning,
    Model,
    Research,
    Architecture,
    Library,
    Product
}

public enum HypothesisStatus
{
    Active,
    Proposed,
    Converged,
    Rejected,
    Superseded
}