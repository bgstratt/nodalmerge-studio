using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Orchestrator;

// Slice 15b — the one implementation of "create a work unit" shared by the MCP tool, the REST
// endpoint, and the agent-loop's in-process dispatcher. See
// plans/phase-6.5-command-surface-hardening.md.
public sealed class WorkUnitCommandService(IOrchestratorService orchestrator) : IWorkUnitCommandService
{
    public Task<WorkUnit> CreateAsync(WorkUnitCreateCommand command, CancellationToken cancellationToken = default) =>
        orchestrator.CreateWorkUnitAsync(
            command.Goal,
            command.Owner,
            command.BranchId,
            command.SuccessCriteria,
            command.RepositoryPath,
            command.ParentWorkUnitId,
            command.DependsOn,
            command.FileScope,
            forkType: command.ForkType,
            reviewPolicy: command.ReviewPolicy,
            bypassPromotionBranch: command.BypassPromotionBranch,
            seedFromBranchId: command.SeedFromBranchId,
            expectedOutputKind: command.ExpectedOutputKind ?? WorkUnitExpectedOutputKind.FileChange,
            cancellationToken: cancellationToken);
}
