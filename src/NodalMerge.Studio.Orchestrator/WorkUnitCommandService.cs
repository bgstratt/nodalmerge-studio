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
    IReviewTimerService reviewTimers,
    IRepositorySyncService repositorySync,
    NodalMerge.Studio.Storage.IRepositoryRegistryService repositories) : IWorkUnitCommandService
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
        var isFreshTopLevelGoal = false;
        if (seedFromBranchId is null)
        {
            if (command.ParentWorkUnitId is null)
            {
                seedFromBranchId = "main";
                isFreshTopLevelGoal = true;
            }
            else
            {
                var parent = await workUnits.GetAsync(command.ParentWorkUnitId, cancellationToken).ConfigureAwait(false);
                seedFromBranchId = parent?.BranchId;
            }
        }

        // Slice 19 — RepositoryId references an already-registered repo (IRepositoryRegistryService)
        // and takes priority over a raw RepositoryPath when both are given: resolve it to a path
        // once here, so everything below (the sync call and the final repositoryPath argument)
        // stays exactly as it was for ad hoc paths.
        var effectiveRepositoryPath = command.RepositoryPath;
        if (command.RepositoryId is not null)
        {
            var repository = await repositories.GetAsync(command.RepositoryId, cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"Repository '{command.RepositoryId}' was not found.");
            effectiveRepositoryPath = repository.Path;
        }

        // Only the implicit "fresh top-level goal" path triggers a live repository sync — forks
        // (explicit SeedFromBranchId) and children (ParentWorkUnitId set) intentionally inherit
        // whatever their seed branch already holds, not a freshly re-synced "main". The sync must
        // run before orchestrator.CreateWorkUnitAsync below so this new work unit's own branch
        // seeds from a fresh "main". Its return value isn't used directly for metadata — a no-op
        // sync still needs the generation this goal was planned against — so GetStateAsync is
        // re-read regardless of whether SyncBranchFromRepositoryAsync itself did anything.
        IReadOnlyDictionary<string, string>? metadata = null;
        if (isFreshTopLevelGoal && !string.IsNullOrWhiteSpace(effectiveRepositoryPath))
        {
            await repositorySync.SyncBranchFromRepositoryAsync(
                seedFromBranchId!, effectiveRepositoryPath, SyncTrigger.GoalCreation, cancellationToken)
                .ConfigureAwait(false);

            var syncState = await repositorySync.GetStateAsync(seedFromBranchId!, cancellationToken).ConfigureAwait(false);
            if (syncState is not null)
            {
                var meta = new Dictionary<string, string> { ["workspaceGeneration"] = syncState.Generation.ToString() };
                if (syncState.LatestExternalChangesetId is not null)
                    meta["lastExternalChangesetId"] = syncState.LatestExternalChangesetId;
                metadata = meta;
            }
        }

        return await orchestrator.CreateWorkUnitAsync(
            command.Goal,
            command.Owner,
            command.BranchId,
            command.SuccessCriteria,
            effectiveRepositoryPath,
            command.ParentWorkUnitId,
            command.DependsOn,
            command.FileScope,
            metadata: metadata,
            forkType: command.ForkType,
            reviewPolicy: command.ReviewPolicy,
            bypassPromotionBranch: command.BypassPromotionBranch,
            seedFromBranchId: seedFromBranchId,
            expectedOutputKind: command.ExpectedOutputKind ?? WorkUnitExpectedOutputKind.FileChange,
            repositoryId: command.RepositoryId,
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
