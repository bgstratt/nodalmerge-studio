using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Orchestrator;

// Slice 23a — pauses a running agent, records a steering decision, and creates a sibling fork
// with the injected constraint so the original work unit's decision log stays immutable.
public sealed class SteeringService(
    IAgentControlService agentControl,
    IOrchestratorService orchestrator,
    ISteeringDecisionService steeringDecisions,
    ISchedulerCommandService scheduler,
    IWorkUnitService workUnits) : ISteeringService
{
    public async Task<SteeringResult> PauseAndRedirectAsync(
        SteeringCommand command, CancellationToken ct = default)
    {
        // 1. Pause the running agent. Failure is non-fatal — the agent may already be idle.
        try
        {
            await agentControl.PauseAsync(command.AgentId, ct).ConfigureAwait(false);
        }
        catch (KeyNotFoundException)
        {
            // agent already stopped; proceed with fork anyway
        }

        // 2. Look up the original work unit to inherit its context
        var original = await workUnits.GetAsync(command.WorkUnitId, ct).ConfigureAwait(false);
        if (original is null)
            throw new KeyNotFoundException($"Work unit '{command.WorkUnitId}' not found.");

        // 3. Create a sibling fork with the injected constraint
        var forkedGoal = $"[steered] {original.Goal}";
        var forkMeta = new Dictionary<string, string>
        {
            ["steeredFromWorkUnitId"] = command.WorkUnitId,
            ["steeredConstraint"]     = command.InjectedConstraint,
            ["steeredBy"]             = "user",
        };

        var fork = await orchestrator.CreateWorkUnitAsync(
            goal:             forkedGoal,
            owner:            original.Owner,
            parentWorkUnitId: original.ParentWorkUnitId,
            forkType:         HypothesisForkType.Reasoning,
            metadata:         forkMeta,
            reviewPolicy:     original.ReviewPolicy,
            cancellationToken: ct).ConfigureAwait(false);

        // 4. Record the steering decision in the DAG
        var steeringId = $"steer-{Guid.NewGuid():N}";
        var decision = new SteeringDecision(
            SteeringDecisionId:   steeringId,
            WorkUnitId:           command.WorkUnitId,
            AgentId:              command.AgentId,
            InjectedConstraint:   command.InjectedConstraint,
            NewChildWorkUnitId:   fork.WorkUnitId,
            SteeredAt:            DateTimeOffset.UtcNow,
            SessionId:            command.SessionId);
        await steeringDecisions.RecordAsync(decision, ct).ConfigureAwait(false);

        // 5. Auto-enqueue the forked work unit if a profile is provided
        if (command.ProfileId is not null)
        {
            await scheduler.EnqueueAsync(
                fork.WorkUnitId,
                command.ProfileId,
                sessionId: command.SessionId,
                ct: ct).ConfigureAwait(false);
        }

        return new SteeringResult(
            SteeringDecisionId: steeringId,
            NewWorkUnitId:      fork.WorkUnitId,
            OriginalWorkUnitId: command.WorkUnitId);
    }

    public async Task<SteeringResult> ForkFromNodeAsync(
        ForkFromNodeCommand command, CancellationToken ct = default)
    {
        // Slice 23b — fork from a specific decision node in the trajectory
        var original = await workUnits.GetAsync(command.WorkUnitId, ct).ConfigureAwait(false);
        if (original is null)
            throw new KeyNotFoundException($"Work unit '{command.WorkUnitId}' not found.");

        var forkMeta = new Dictionary<string, string>
        {
            ["forkedFromNodeId"]      = command.NodeId ?? "",
            ["forkedFromWorkUnitId"]  = command.WorkUnitId,
            ["forkedFromProposalId"]  = command.ProposalId ?? "",
        };
        if (command.ConstraintText is not null)
            forkMeta["constraint"] = command.ConstraintText;

        var forkGoal = command.NewGoal ?? $"[forked] {original.Goal}";

        // When a proposal is specified, seed the fork's branch from that proposal's base state
        // (the same base/{proposalId} snapshot IOrchestratorService.CreateWorkUnitAsync already
        // knows how to seed from) and record the lineage via branchedFromProposalId directly,
        // rather than mutating the returned record after the fact.
        var fork = await orchestrator.CreateWorkUnitAsync(
            goal:                   forkGoal,
            owner:                  original.Owner,
            parentWorkUnitId:       original.ParentWorkUnitId,
            seedFromBranchId:       command.ProposalId is not null ? $"base/{command.ProposalId}" : null,
            branchedFromProposalId: command.ProposalId,
            forkType:               HypothesisForkType.Reasoning,
            metadata:               forkMeta,
            reviewPolicy:           original.ReviewPolicy,
            cancellationToken: ct).ConfigureAwait(false);

        if (command.ProfileId is not null)
        {
            await scheduler.EnqueueAsync(
                fork.WorkUnitId,
                command.ProfileId,
                sessionId: command.SessionId,
                ct: ct).ConfigureAwait(false);
        }

        var steeringId = $"forknode-{Guid.NewGuid():N}";
        var decision = new SteeringDecision(
            SteeringDecisionId:   steeringId,
            WorkUnitId:           command.WorkUnitId,
            AgentId:              null,
            InjectedConstraint:   command.ConstraintText ?? command.NewGoal ?? "[fork from node]",
            NewChildWorkUnitId:   fork.WorkUnitId,
            SteeredAt:            DateTimeOffset.UtcNow,
            SessionId:            command.SessionId);
        await steeringDecisions.RecordAsync(decision, ct).ConfigureAwait(false);

        return new SteeringResult(
            SteeringDecisionId: steeringId,
            NewWorkUnitId:      fork.WorkUnitId,
            OriginalWorkUnitId: command.WorkUnitId);
    }
}