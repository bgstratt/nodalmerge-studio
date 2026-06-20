namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Command to create a counterfactual — branch from a proposal's base state and re-run
/// with a different profile/model, leaving the original proposal untouched.
/// </summary>
public sealed record CounterfactualCommand(
    string ProposalId,
    string NewProfileId,
    string? NewGoalOverride = null,
    string? ConstraintText = null,
    string? SessionId = null);

/// <summary>
/// Result from creating a counterfactual work unit.
/// </summary>
public sealed record CounterfactualResult(
    string CounterfactualId,
    string OriginalWorkUnitId,
    string NewWorkUnitId,
    string OriginalProposalId);

/// <summary>
/// Creates counterfactual work units — branch from a proposal's base state and re-run
/// with a different profile/model to explore "what if" scenarios.
/// </summary>
public interface ICounterfactualService
{
    Task<CounterfactualResult> CreateAsync(CounterfactualCommand command, CancellationToken ct = default);
}