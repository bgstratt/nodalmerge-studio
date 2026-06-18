using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class HypothesisTools(IWorkUnitService workUnits, IWorkUnitCommandService workUnitCommands)
{
    [McpServerTool(Name = McpToolNames.HypothesisFork), Description("Fork a hypothesis from a parent work unit or proposal.")]
    public async Task<string> ForkAsync(
        string goal,
        string forkType,
        string? parentWorkUnitId = null,
        string? branchedFromProposalId = null,
        string? rationale = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<HypothesisForkType>(forkType, ignoreCase: true, out var hypothesisType))
            return McpJson.Error(McpToolNames.HypothesisFork, $"Invalid forkType '{forkType}'. Use Code, Reasoning, Model, Research, Architecture, or Product.");

        var workUnit = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand(goal, "studio", ParentWorkUnitId: parentWorkUnitId, ForkType: hypothesisType),
            cancellationToken).ConfigureAwait(false);

        return McpJson.Ok(new
        {
            hypothesisId = workUnit.WorkUnitId,
            workUnitId = workUnit.WorkUnitId,
            goal = workUnit.Goal,
            forkType = hypothesisType.ToString(),
            parentWorkUnitId,
            branchedFromProposalId,
            rationale,
            status = "Active",
            createdAt = workUnit.CreatedAt
        });
    }

    [McpServerTool(Name = McpToolNames.HypothesisList), Description("List hypothesis forks.")]
    public async Task<string> ListAsync(string? parentWorkUnitId = null, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<WorkUnit> items;
        if (parentWorkUnitId is not null)
        {
            items = await workUnits.GetChildrenAsync(parentWorkUnitId, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            items = await workUnits.ListAsync(branchId: null, cancellationToken).ConfigureAwait(false);
        }

        var hypotheses = items.Select(wu => new
        {
            hypothesisId = wu.WorkUnitId,
            workUnitId = wu.WorkUnitId,
            goal = wu.Goal,
            forkType = wu.ForkType?.ToString() ?? "Unknown",
            status = wu.Status.ToString(),
            parentWorkUnitId = wu.ParentWorkUnitId,
            branchedFromProposalId = wu.BranchedFromProposalId,
            createdAt = wu.CreatedAt
        }).ToList();

        return McpJson.Ok(new { hypotheses });
    }
}