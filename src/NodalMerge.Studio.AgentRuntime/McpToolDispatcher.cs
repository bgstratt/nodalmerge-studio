using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class McpToolDispatcher(
    IWorkUnitService workUnits,
    IOrchestratorService orchestrator,
    ITaskService tasks,
    IBranchService branches,
    IMergeService merge,
    IWorkspaceService workspace,
    ISnapshotService snapshots,
    IAgentControlService agentControl)
{
    public async Task<string> DispatchAsync(string toolName, JsonElement input, CancellationToken ct)
    {
        try
        {
            return toolName switch
            {
                McpToolNames.WorkUnitGet      => await WorkUnitGetAsync(input, ct),
                McpToolNames.WorkUnitCreate   => await WorkUnitCreateAsync(input, ct),
                McpToolNames.WorkUnitUpdate   => await WorkUnitUpdateAsync(input, ct),
                McpToolNames.WorkUnitList     => await WorkUnitListAsync(input, ct),
                McpToolNames.TaskCreate       => await TaskCreateAsync(input, ct),
                McpToolNames.TaskList         => await TaskListAsync(input, ct),
                McpToolNames.TaskUpdate       => await TaskUpdateAsync(input, ct),
                McpToolNames.TaskAssign       => await TaskAssignAsync(input, ct),
                McpToolNames.BranchCreate     => await BranchCreateAsync(input, ct),
                McpToolNames.BranchList       => await BranchListAsync(ct),
                McpToolNames.BranchStatus     => await BranchStatusAsync(input, ct),
                McpToolNames.AgentSpawn       => await AgentSpawnAsync(input, ct),
                McpToolNames.AgentStatus      => await AgentStatusAsync(input, ct),
                McpToolNames.AgentStop        => await AgentStopAsync(input, ct),
                McpToolNames.MergePropose     => await MergeProposeAsync(input, ct),
                McpToolNames.MergeValidate    => await MergeValidateAsync(input, ct),
                McpToolNames.WorkspaceSummary => await WorkspaceSummaryAsync(input, ct),
                McpToolNames.SnapshotGet      => await SnapshotGetAsync(input, ct),
                _ => ToError($"Tool '{toolName}' is not dispatched by the agent runtime.")
            };
        }
        catch (Exception ex)
        {
            return ToError(ex.Message);
        }
    }

    private async Task<string> WorkUnitGetAsync(JsonElement input, CancellationToken ct)
    {
        var wu = await workUnits.GetAsync(Str(input, "workUnitId")!, ct).ConfigureAwait(false);
        return wu is null ? ToError("Work unit not found.") : ToJson(wu);
    }

    private async Task<string> WorkUnitCreateAsync(JsonElement input, CancellationToken ct)
    {
        var wu = await orchestrator.CreateWorkUnitAsync(
            Str(input, "goal")!, Str(input, "owner") ?? "studio",
            Str(input, "successCriteria"), ct).ConfigureAwait(false);
        wu = wu with { BranchId = Str(input, "branchId") ?? wu.BranchId };
        await workUnits.CreateAsync(wu, ct).ConfigureAwait(false);
        return ToJson(new { workUnitId = wu.WorkUnitId, branchId = wu.BranchId });
    }

    private async Task<string> WorkUnitUpdateAsync(JsonElement input, CancellationToken ct)
    {
        var workUnitId = Str(input, "workUnitId")!;
        var statusStr = Str(input, "status");
        if (statusStr is not null && Enum.TryParse<WorkUnitStatus>(statusStr, true, out var s))
            await workUnits.UpdateStatusAsync(workUnitId, s, ct).ConfigureAwait(false);
        var assignedAgent = Str(input, "assignedAgent");
        if (assignedAgent is not null)
            await orchestrator.AssignWorkAsync(workUnitId, assignedAgent, ct).ConfigureAwait(false);
        var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        return ToJson(wu);
    }

    private async Task<string> WorkUnitListAsync(JsonElement input, CancellationToken ct)
    {
        var list = await workUnits.ListAsync(Str(input, "branchId"), ct).ConfigureAwait(false);
        return ToJson(list.Select(w => w.WorkUnitId));
    }

    private async Task<string> TaskCreateAsync(JsonElement input, CancellationToken ct)
    {
        var task = new StudioTask(
            Guid.NewGuid().ToString("N"),
            Str(input, "workUnitId")!,
            Str(input, "title")!,
            Str(input, "description")!,
            StudioTaskStatus.Open,
            null,
            Int(input, "priority") ?? 0);
        var created = await tasks.CreateAsync(task, ct).ConfigureAwait(false);
        return ToJson(new { taskId = created.TaskId });
    }

    private async Task<string> TaskListAsync(JsonElement input, CancellationToken ct)
    {
        var list = await tasks.ListAsync(Str(input, "workUnitId"), ct).ConfigureAwait(false);
        return ToJson(list);
    }

    private async Task<string> TaskUpdateAsync(JsonElement input, CancellationToken ct)
    {
        var taskId = Str(input, "taskId")!;
        var existing = await tasks.GetAsync(taskId, ct).ConfigureAwait(false);
        if (existing is null) return ToError($"Task '{taskId}' not found.");
        var statusStr = Str(input, "status");
        var status = statusStr is not null && Enum.TryParse<StudioTaskStatus>(statusStr, true, out var s)
            ? s : existing.Status;
        var updated = existing with
        {
            Title = Str(input, "title") ?? existing.Title,
            Description = Str(input, "description") ?? existing.Description,
            Status = status,
            Priority = Int(input, "priority") ?? existing.Priority
        };
        var result = await tasks.UpdateAsync(updated, ct).ConfigureAwait(false);
        return ToJson(result);
    }

    private async Task<string> TaskAssignAsync(JsonElement input, CancellationToken ct)
    {
        var assigned = await tasks.AssignAsync(
            Str(input, "taskId")!, Str(input, "agentId")!, ct).ConfigureAwait(false);
        return ToJson(assigned);
    }

    private async Task<string> BranchCreateAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = await branches.CreateBranchAsync(
            Str(input, "name")!, Str(input, "fromBranch"), ct).ConfigureAwait(false);
        return ToJson(new { branchId });
    }

    private async Task<string> BranchListAsync(CancellationToken ct)
    {
        var list = await branches.ListBranchesAsync(ct).ConfigureAwait(false);
        return ToJson(list);
    }

    private async Task<string> BranchStatusAsync(JsonElement input, CancellationToken ct)
    {
        var status = await branches.GetStatusAsync(Str(input, "branchId")!, ct).ConfigureAwait(false);
        return ToJson(status);
    }

    private async Task<string> AgentSpawnAsync(JsonElement input, CancellationToken ct)
    {
        var agentId = await agentControl.SpawnAsync(
            Str(input, "agentType")!, Str(input, "workUnitId")!,
            Str(input, "taskId"), Str(input, "model"), Str(input, "baseUrl"), Str(input, "apiKey"),
            Str(input, "provider"), ct).ConfigureAwait(false);
        return ToJson(new { agentId });
    }

    private async Task<string> AgentStatusAsync(JsonElement input, CancellationToken ct)
    {
        var agentId = Str(input, "agentId")!;
        var status = await agentControl.GetStatusAsync(agentId, ct).ConfigureAwait(false);
        return ToJson(new { agentId, status });
    }

    private async Task<string> AgentStopAsync(JsonElement input, CancellationToken ct)
    {
        var agentId = Str(input, "agentId")!;
        await agentControl.StopAsync(agentId, ct).ConfigureAwait(false);
        return ToJson(new { agentId, status = "stopped" });
    }

    private async Task<string> MergeProposeAsync(JsonElement input, CancellationToken ct)
    {
        var proposalId = $"MP-{Guid.NewGuid():N}";
        var summary = Str(input, "summary")!;
        var proposal = new MergeProposal(
            proposalId,
            Str(input, "sourceBranch")!,
            Str(input, "targetBranch")!,
            Str(input, "goal") ?? summary,
            summary,
            Str(input, "changeDescription") ?? summary,
            null, null, null,
            MergeProposalStatus.Draft);
        var created = await merge.ProposeAsync(proposal, ct).ConfigureAwait(false);
        return ToJson(new { proposalId = created.ProposalId, status = created.Status.ToString() });
    }

    private async Task<string> MergeValidateAsync(JsonElement input, CancellationToken ct)
    {
        var proposal = await merge.ValidateAsync(Str(input, "proposalId")!, ct).ConfigureAwait(false);
        return ToJson(proposal);
    }

    private async Task<string> WorkspaceSummaryAsync(JsonElement input, CancellationToken ct)
    {
        var summary = await workspace.GetSummaryAsync(Str(input, "branchId"), ct).ConfigureAwait(false);
        return ToJson(summary);
    }

    private async Task<string> SnapshotGetAsync(JsonElement input, CancellationToken ct)
    {
        var snap = await snapshots.GetAsync(
            Str(input, "agentId")!, Str(input, "workUnitId")!, ct).ConfigureAwait(false);
        return ToJson(snap);
    }

    private static string? Str(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static int? Int(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.TryGetInt32(out var v) ? v : null;

    private static string ToJson(object? data) => JsonSerializer.Serialize(data);
    private static string ToError(string message) => JsonSerializer.Serialize(new { error = message });
}
