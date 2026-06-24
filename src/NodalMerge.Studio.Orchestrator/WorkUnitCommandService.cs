using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Orchestrator;

// Slice 15b — the one implementation of "create a work unit" shared by the MCP tool, the REST
// endpoint, and the agent-loop's in-process dispatcher. See
// plans/phase-6.5-command-surface-hardening.md.
//
// Stop controls (CancelAsync/CancelAllActiveAsync) live here too rather than on
// IWorkUnitService — cancelling a goal is a cross-cutting command (work unit status + agents +
// review timers), the same shape as CreateAsync's "one entry point for three transports" role.
public sealed class WorkUnitCommandService(
    IOrchestratorService orchestrator,
    IWorkUnitService workUnits,
    IAgentControlService agentControl,
    IReviewTimerService reviewTimers) : IWorkUnitCommandService
{
    private static readonly HashSet<WorkUnitStatus> TerminalStatuses = new()
    {
        WorkUnitStatus.Completed, WorkUnitStatus.Merged, WorkUnitStatus.Cancelled,
    };

    public async Task<WorkUnit> CreateAsync(WorkUnitCreateCommand command, CancellationToken cancellationToken = default)
    {
        // A work unit with no explicit seed otherwise gets an empty branch directory —
        // FileSystemWorkspaceService.InitBranchAsync only copies from seedFromBranchId when one
        // is given, and only falls back to SeedRepositoryPath for branchId "main" itself. Without
        // this, every fresh goal starts blind to every previously applied/merged change (and even
        // to the originally seeded repo), and every fork-without-a-parent-seed starts blind to
        // whatever its parent is currently holding. Fan-out/steering/fork-from-node bypass this
        // service and call IOrchestratorService directly with their own explicit seed already, so
        // this default only ever reaches a genuinely fresh, human- or tool-initiated creation.
        var seedFromBranchId = command.SeedFromBranchId;
        if (seedFromBranchId is null)
        {
            if (command.ParentWorkUnitId is null)
            {
                seedFromBranchId = "main";
            }
            else
            {
                var parent = await workUnits.GetAsync(command.ParentWorkUnitId, cancellationToken).ConfigureAwait(false);
                seedFromBranchId = parent?.BranchId;
            }
        }

        return await orchestrator.CreateWorkUnitAsync(
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
            seedFromBranchId: seedFromBranchId,
            expectedOutputKind: command.ExpectedOutputKind ?? WorkUnitExpectedOutputKind.FileChange,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkUnit>> CancelAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        var root = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Work unit '{workUnitId}' was not found.");

        var subtree = new List<WorkUnit> { root };
        var frontier = new Queue<string>();
        frontier.Enqueue(workUnitId);
        while (frontier.Count > 0)
        {
            var children = await workUnits.GetChildrenAsync(frontier.Dequeue(), cancellationToken).ConfigureAwait(false);
            foreach (var child in children)
            {
                subtree.Add(child);
                frontier.Enqueue(child.WorkUnitId);
            }
        }

        var cancelled = new List<WorkUnit>();
        foreach (var workUnit in subtree)
        {
            if (TerminalStatuses.Contains(workUnit.Status))
                continue;

            var updated = await workUnits.UpdateStatusAsync(workUnit.WorkUnitId, WorkUnitStatus.Cancelled, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            cancelled.Add(updated);
        }

        var cancelledIds = cancelled.Select(w => w.WorkUnitId).ToHashSet();
        if (cancelledIds.Count == 0)
            return cancelled;

        var activeAgents = await agentControl.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        foreach (var agent in activeAgents.Where(a => cancelledIds.Contains(a.WorkUnitId)))
            await agentControl.StopAsync(agent.AgentId, cancellationToken).ConfigureAwait(false);

        var pendingTimers = await reviewTimers.ListPendingAsync(ct: cancellationToken).ConfigureAwait(false);
        foreach (var timer in pendingTimers.Where(t => cancelledIds.Contains(t.WorkUnitId)))
            await reviewTimers.TryCancelAsync(timer.ProposalId, cancellationToken).ConfigureAwait(false);

        return cancelled;
    }

    public async Task<IReadOnlyList<WorkUnit>> CancelAllActiveAsync(CancellationToken cancellationToken = default)
    {
        var all = await workUnits.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        var roots = all.Where(w => w.ParentWorkUnitId is null && !TerminalStatuses.Contains(w.Status));

        var cancelled = new List<WorkUnit>();
        foreach (var root in roots)
            cancelled.AddRange(await CancelAsync(root.WorkUnitId, cancellationToken).ConfigureAwait(false));

        return cancelled;
    }
}
