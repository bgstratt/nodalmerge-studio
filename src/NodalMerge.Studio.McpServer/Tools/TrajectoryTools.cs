using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class TrajectoryTools(IWorkUnitService workUnits, IReplayService replay)
{
    [McpServerTool(Name = McpToolNames.TrajectoryCreate), Description("Create a trajectory record for a work unit's lifecycle.")]
    public async Task<string> CreateAsync(
        string workUnitId,
        string phase,
        string? agentId = null,
        string? model = null,
        string? provider = null,
        CancellationToken cancellationToken = default)
    {
        var wu = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        if (wu is null)
            return McpJson.Error(McpToolNames.TrajectoryCreate, $"Work unit '{workUnitId}' not found.");

        if (!Enum.TryParse<TrajectoryPhase>(phase, ignoreCase: true, out var trajectoryPhase))
            return McpJson.Error(McpToolNames.TrajectoryCreate, $"Invalid phase '{phase}'. Use GoalDefined, Decomposed, Executing, Proposed, Converging, Converged, Forked, or Abandoned.");

        return McpJson.Ok(new
        {
            trajectoryId = wu.WorkUnitId,
            workUnitId,
            branchId = wu.BranchId,
            goal = wu.Goal,
            phase = trajectoryPhase.ToString(),
            agentId,
            model,
            provider,
            startedAt = wu.CreatedAt
        });
    }

    [McpServerTool(Name = McpToolNames.TrajectoryReplay), Description("Replay the trajectory of decisions with mode: Linear, BranchExplorer, or Counterfactual.")]
    public async Task<string> ReplayAsync(
        string? workUnitId = null,
        string? branchId = null,
        string? mode = "Linear",
        CancellationToken cancellationToken = default)
    {
        var normalizedMode = mode?.Trim().ToLowerInvariant() ?? "linear";

        if (normalizedMode == "branchexplorer")
            return await BuildBranchExplorerAsync(workUnitId, cancellationToken).ConfigureAwait(false);

        if (normalizedMode == "counterfactual")
            return await BuildCounterfactualAsync(workUnitId, cancellationToken).ConfigureAwait(false);

        // Default: Linear mode — use existing replay infrastructure
        var targetBranchId = branchId;
        if (targetBranchId is null && workUnitId is not null)
        {
            var wu = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            targetBranchId = wu?.BranchId;
        }

        if (targetBranchId is not null)
        {
            var replayJson = await replay.RangeAsync(targetBranchId, cancellationToken: cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { mode = "Linear", replayJson });
        }

        // No branch specified — list all work units as summary trajectory
        var allUnits = await workUnits.ListAsync(branchId: null, cancellationToken).ConfigureAwait(false);
        var nodes = allUnits.Select(wu => new
        {
            workUnitId = wu.WorkUnitId,
            branchId = wu.BranchId,
            goal = wu.Goal,
            phase = wu.CurrentStage?.ToString() ?? wu.Status.ToString(),
            startedAt = wu.CreatedAt,
            completedAt = wu.Status is WorkUnitStatus.Completed or WorkUnitStatus.Merged ? (DateTimeOffset?)wu.UpdatedAt : null
        }).ToList();

        return McpJson.Ok(new { mode = "Linear", nodes });
    }

    private async Task<string> BuildBranchExplorerAsync(string? workUnitId, CancellationToken ct)
    {
        var allUnits = await workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);

        // Group work units by branch for a branch-centric view
        var byBranch = allUnits.GroupBy(wu => wu.BranchId).Select(g => new
        {
            branchId = g.Key,
            workUnitCount = g.Count(),
            goals = g.Select(wu => new
            {
                workUnitId = wu.WorkUnitId,
                goal = wu.Goal,
                status = wu.Status.ToString(),
                phase = wu.CurrentStage?.ToString(),
                hasChildren = allUnits.Any(c => c.ParentWorkUnitId == wu.WorkUnitId),
                createdAt = wu.CreatedAt
            }).OrderBy(w => w.createdAt).ToList()
        }).OrderByDescending(b => b.workUnitCount).ToList();

        return McpJson.Ok(new { mode = "BranchExplorer", branches = byBranch });
    }

    private async Task<string> BuildCounterfactualAsync(string? workUnitId, CancellationToken ct)
    {
        var allUnits = await workUnits.ListAsync(branchId: null, ct).ConfigureAwait(false);

        WorkUnit? target = null;
        if (workUnitId is not null)
        {
            target = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        }

        // For counterfactual view, find siblings (same parent) and forks from proposals
        var counterfactuals = new List<object>();

        if (target?.ParentWorkUnitId is not null)
        {
            var siblings = allUnits.Where(wu => wu.ParentWorkUnitId == target.ParentWorkUnitId && wu.WorkUnitId != target.WorkUnitId).ToList();
            counterfactuals.AddRange(siblings.Select(s => new
            {
                workUnitId = s.WorkUnitId,
                goal = s.Goal,
                status = s.Status.ToString(),
                relationship = "sibling",
                forkType = s.ForkType?.ToString(),
                branchedFromProposalId = s.BranchedFromProposalId,
                createdAt = s.CreatedAt
            }));
        }

        // Also include any work unit forked from a proposal as alternative timeline
        var forkedFromProposals = allUnits.Where(wu => wu.BranchedFromProposalId is not null && wu.WorkUnitId != workUnitId).ToList();
        counterfactuals.AddRange(forkedFromProposals.Select(f => new
        {
            workUnitId = f.WorkUnitId,
            goal = f.Goal,
            status = f.Status.ToString(),
            relationship = "forked-from-proposal",
            forkType = f.ForkType?.ToString(),
            branchedFromProposalId = f.BranchedFromProposalId,
            createdAt = f.CreatedAt
        }));

        return McpJson.Ok(new
        {
            mode = "Counterfactual",
            targetWorkUnitId = workUnitId,
            targetGoal = target?.Goal,
            alternatives = counterfactuals
        });
    }
}