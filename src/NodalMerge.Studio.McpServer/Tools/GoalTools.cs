using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class GoalTools(
    IWorkUnitService workUnits,
    IWorkUnitCommandService workUnitCommands,
    IGoalNodeService goalNodes,
    IRepositoryRegistryService repositories)
{
    [McpServerTool(Name = McpToolNames.GoalCreate), Description("Create a decision-centric goal from a work unit.")]
    public async Task<string> CreateAsync(
        string goal,
        string? workUnitId = null,
        string? repositoryId = null,
        string? repositoryPath = null,
        // When set, a fresh repository (git init) is created at this path and used as the goal's
        // repository — takes priority over repositoryId/repositoryPath when given.
        string? newRepositoryPath = null,
        string? newRepositoryLabel = null,
        [Description("Read-only pointers into other registered repositories for context (style/examples) — only used when creating a fresh work unit (workUnitId is null).")] IReadOnlyList<FileReferenceV1>? referenceFiles = null,
        CancellationToken cancellationToken = default)
    {
        // If an existing work unit is referenced, look it up; otherwise create a new one.
        WorkUnit? workUnit;
        if (workUnitId is not null)
        {
            workUnit = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            if (workUnit is null)
                return McpJson.Error(McpToolNames.GoalCreate, $"Work unit '{workUnitId}' not found.");
        }
        else
        {
            if (newRepositoryPath is not null)
            {
                var created = await repositories.CreateAsync(
                    newRepositoryPath, newRepositoryLabel, cancellationToken).ConfigureAwait(false);
                repositoryId = created.RepositoryId;
            }

            workUnit = await workUnitCommands.CreateAsync(
                new WorkUnitCreateCommand(goal, "studio", RepositoryId: repositoryId, RepositoryPath: repositoryPath, ReferenceFiles: referenceFiles),
                cancellationToken).ConfigureAwait(false);
        }

        // Persist goal node
        var goalNode = new GoalNode(
            GoalId: workUnit.WorkUnitId,
            Goal: workUnit.Goal,
            WorkUnitId: workUnit.WorkUnitId,
            BranchId: workUnit.BranchId,
            Status: GoalStatus.Exploring,
            CreatedAt: workUnit.CreatedAt,
            UpdatedAt: workUnit.UpdatedAt,
            Owner: workUnit.Owner,
            ParentGoalId: workUnit.ParentWorkUnitId,
            RepositoryId: workUnit.RepositoryId); // #1 goal replication — route to the repo room.
        await goalNodes.RecordAsync(goalNode, cancellationToken).ConfigureAwait(false);

        return McpJson.Ok(new
        {
            goalId = workUnit.WorkUnitId,
            goal = workUnit.Goal,
            workUnitId = workUnit.WorkUnitId,
            branchId = workUnit.BranchId,
            status = goalNode.Status.ToString(),
            createdAt = workUnit.CreatedAt
        });
    }

    [McpServerTool(Name = McpToolNames.GoalList), Description("List goals, optionally filtered by session.")]
    public async Task<string> ListAsync(string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var storedGoals = await goalNodes.ListAsync(cancellationToken).ConfigureAwait(false);
        if (storedGoals.Count > 0)
        {
            var goals = storedGoals.Select(g => new
            {
                goalId = g.GoalId,
                goal = g.Goal,
                workUnitId = g.WorkUnitId,
                branchId = g.BranchId,
                status = g.Status.ToString(),
                parentGoalId = g.ParentGoalId,
                createdAt = g.CreatedAt
            }).ToList();
            return McpJson.Ok(new { goals, source = "goal-store" });
        }

        // Fallback to work units if no goals persisted yet
        var items = await workUnits.ListAsync(branchId: null, cancellationToken).ConfigureAwait(false);
        var fallback = items.Select(wu => new
        {
            goalId = wu.WorkUnitId,
            goal = wu.Goal,
            workUnitId = wu.WorkUnitId,
            branchId = wu.BranchId,
            status = wu.Status.ToString(),
            parentWorkUnitId = wu.ParentWorkUnitId,
            createdAt = wu.CreatedAt
        }).ToList();

        return McpJson.Ok(new { goals = fallback, source = "work-units" });
    }
}
