using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Orchestrator;

/// <summary>
/// Slice 25a — creates counterfactual work units by branching from a proposal's base state
/// and re-running with a different profile/model. The original proposal is untouched.
/// </summary>
public sealed class CounterfactualService(
    IMergeService merge,
    IOrchestratorService orchestrator,
    ISchedulerCommandService scheduler,
    IWorkUnitService workUnits) : ICounterfactualService
{
    public async Task<CounterfactualResult> CreateAsync(CounterfactualCommand command, CancellationToken ct = default)
    {
        // 1. Resolve the original proposal to get its base state and work unit
        var proposal = await merge.GetAsync(command.ProposalId, ct).ConfigureAwait(false);
        if (proposal is null)
            throw new KeyNotFoundException($"Proposal '{command.ProposalId}' not found.");

        if (proposal.WorkUnitId is null)
            throw new InvalidOperationException($"Proposal '{command.ProposalId}' has no associated work unit.");

        var originalWu = await workUnits.GetAsync(proposal.WorkUnitId, ct).ConfigureAwait(false);
        if (originalWu is null)
            throw new KeyNotFoundException($"Work unit '{proposal.WorkUnitId}' not found.");

        // 2. Derive the counterfactual goal — override if provided, otherwise annotate
        var counterfactualGoal = command.NewGoalOverride
            ?? $"[counterfactual] {originalWu.Goal}";

        var counterfactualMeta = new Dictionary<string, string>
        {
            ["counterfactualFromProposalId"] = command.ProposalId,
            ["counterfactualFromWorkUnitId"] = originalWu.WorkUnitId,
            ["counterfactualProfileId"]      = command.NewProfileId,
        };
        if (command.ConstraintText is not null)
            counterfactualMeta["constraint"] = command.ConstraintText;

        // 3. Create a sibling work unit branched from the proposal's base state.
        //    seedFromBranchId uses the "base/{proposalId}" snapshot that InMemoryMergeService
        //    writes at propose time — same as the existing /studio/merges/{id}/branch endpoint.
        var newWu = await orchestrator.CreateWorkUnitAsync(
            goal:                     counterfactualGoal,
            owner:                    originalWu.Owner,
            parentWorkUnitId:         originalWu.ParentWorkUnitId,
            seedFromBranchId:         $"base/{command.ProposalId}",
            branchedFromProposalId:   command.ProposalId,
            forkType:                 HypothesisForkType.Model,
            metadata:                 counterfactualMeta,
            taskReviewPolicy:      originalWu.TaskReviewPolicy,
            workspaceReviewPolicy: originalWu.WorkspaceReviewPolicy,
            taskReviewHybridTimeoutMinutes:      originalWu.TaskReviewHybridTimeoutMinutes,
            workspaceReviewHybridTimeoutMinutes: originalWu.WorkspaceReviewHybridTimeoutMinutes,
            // This fork inherits originalWu.ParentWorkUnitId (a sibling, not a child), so it must
            // also inherit RepositoryId — otherwise its own apply resolves the wrong (or no)
            // write-back path in a multi-repo setup when it ends up top-level.
            repositoryId: originalWu.RepositoryId,
            cancellationToken:        ct).ConfigureAwait(false);

        // 4. Enqueue the counterfactual work unit with the new profile
        await scheduler.EnqueueAsync(
            newWu.WorkUnitId,
            command.NewProfileId,
            sessionId: command.SessionId,
            ct: ct).ConfigureAwait(false);

        var counterfactualId = $"cf-{Guid.NewGuid():N}";

        return new CounterfactualResult(
            CounterfactualId:     counterfactualId,
            OriginalWorkUnitId:   originalWu.WorkUnitId,
            NewWorkUnitId:        newWu.WorkUnitId,
            OriginalProposalId:   command.ProposalId);
    }
}