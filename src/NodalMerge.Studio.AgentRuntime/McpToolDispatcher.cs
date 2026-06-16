using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using StudioArtifactStatus = NodalMerge.Studio.Contracts.Domain.ArtifactStatus;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.AgentRuntime;

internal sealed class McpToolDispatcher(
    IWorkUnitService workUnits,
    IOrchestratorService orchestrator,
    ITaskService tasks,
    IBranchService branches,
    IMergeService merge,
    IWorkspaceService workspace,
    IFileWorkspaceService fileWorkspace,
    ISnapshotService snapshots,
    IAgentControlService agentControl,
    IArtifactRefService artifactRefs,
    IProjectionManager projections,
    IWorkScheduler scheduler,
    IExecutionEventStream events)
{
    public async Task<string> DispatchAsync(
        string toolName,
        JsonElement input,
        IReadOnlyList<string>? allowedTools,
        CancellationToken ct,
        string? sessionId = null)
    {
        if (allowedTools is { Count: > 0 } && !allowedTools.Contains(toolName))
            return ToError($"Tool '{toolName}' is not permitted by this agent's profile.");

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
                McpToolNames.MergePropose     => await MergeProposeAsync(input, ct, sessionId),
                McpToolNames.MergeValidate    => await MergeValidateAsync(input, ct),
                McpToolNames.WorkspaceSummary => await WorkspaceSummaryAsync(input, ct),
                McpToolNames.SnapshotGet      => await SnapshotGetAsync(input, ct),
                McpToolNames.ProjectionGet    => await ProjectionGetAsync(input, ct),
                McpToolNames.MergeApply       => await MergeApplyAsync(input, ct, sessionId),
                McpToolNames.WorkspaceRead    => await WorkspaceReadAsync(input, ct),
                McpToolNames.WorkspaceWrite   => await WorkspaceWriteAsync(input, ct),
                McpToolNames.WorkspaceDelete  => await WorkspaceDeleteAsync(input, ct),
                McpToolNames.WorkspaceList    => await WorkspaceListAsync(input, ct),
                McpToolNames.WorkspaceDiff    => await WorkspaceDiffAsync(input, ct),
                McpToolNames.WorkspaceExists  => await WorkspaceExistsAsync(input, ct),
                McpToolNames.SchedulerEnqueue => await SchedulerEnqueueAsync(input, ct, sessionId),
                McpToolNames.SchedulerPending => await SchedulerPendingAsync(ct),
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
            Str(input, "successCriteria"), cancellationToken: ct).ConfigureAwait(false);
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
        var workUnitId = Str(input, "workUnitId")!;
        var task = new StudioTask(
            Guid.NewGuid().ToString("N"),
            workUnitId,
            Str(input, "title")!,
            Str(input, "description")!,
            StudioTaskStatus.Open,
            null,
            Int(input, "priority") ?? 0);
        var created = await tasks.CreateAsync(task, ct).ConfigureAwait(false);
        await artifactRefs.WriteAsync(new ArtifactRef(
            created.TaskId,
            ArtifactType.Task,
            workUnitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            workUnitId,
            null), ct).ConfigureAwait(false);
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
            Str(input, "provider"), Str(input, "profileId"), ct).ConfigureAwait(false);
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

    private async Task<string> MergeProposeAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var proposalId   = $"MP-{Guid.NewGuid():N}";
        var summary      = Str(input, "summary")!;
        var sourceBranch = Str(input, "sourceBranch")!;
        var targetBranch = Str(input, "targetBranch")!;
        var workUnitId   = Str(input, "workUnitId");
        var agentId      = Str(input, "agentId");

        string? workspaceChanges = null;
        DateTimeOffset? diffGeneratedAt = null;
        try
        {
            workspaceChanges = await fileWorkspace.DiffAsync(sourceBranch, targetBranch, ct).ConfigureAwait(false);
            diffGeneratedAt  = DateTimeOffset.UtcNow;
        }
        catch { /* branch dirs may not exist yet; diff is optional */ }

        var proposal = new MergeProposal(
            proposalId,
            sourceBranch,
            targetBranch,
            Str(input, "goal") ?? summary,
            summary,
            Str(input, "changeDescription") ?? summary,
            null, null, null,
            MergeProposalStatus.Draft,
            WorkspaceChanges: workspaceChanges,
            DiffGeneratedAt:  diffGeneratedAt,
            AgentId:          agentId,
            Model:            Str(input, "model"),
            Provider:         Str(input, "provider"),
            SessionId:        sessionId,
            WorkUnitId:       workUnitId);
        var created = await merge.ProposeAsync(proposal, ct).ConfigureAwait(false);

        if (workUnitId is not null)
        {
            await artifactRefs.WriteAsync(new ArtifactRef(
                created.ProposalId,
                ArtifactType.MergeProposal,
                workUnitId,
                StudioArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                workUnitId,
                agentId), ct).ConfigureAwait(false);

            if (sessionId is not null)
            {
                await events.AppendAsync(
                    sessionId,
                    workUnitId,
                    ExecutionEventKind.ArtifactProposed,
                    new ArtifactProposedPayload(created.ProposalId, workUnitId, []),
                    ct: ct).ConfigureAwait(false);
            }
        }

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

    private async Task<string> WorkspaceReadAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId and path are required.");
        try
        {
            var content = await fileWorkspace.ReadAsync(branchId, path, ct).ConfigureAwait(false);
            return content is null
                ? ToError($"File '{path}' not found in branch '{branchId}'.")
                : ToJson(new { content });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceWriteAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        var path     = Str(input, "path");
        var content  = Str(input, "content");
        if (branchId is null || path is null || content is null) return ToError("branchId, path, and content are required.");
        try
        {
            await fileWorkspace.WriteAsync(branchId, path, content, ct).ConfigureAwait(false);
            return ToJson(new { written = true, path });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceDeleteAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId and path are required.");
        try
        {
            await fileWorkspace.DeleteAsync(branchId, path, ct).ConfigureAwait(false);
            return ToJson(new { deleted = true, path });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceListAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        if (branchId is null) return ToError("branchId is required.");
        try
        {
            var files = await fileWorkspace.ListAsync(branchId, Str(input, "path"), ct).ConfigureAwait(false);
            return ToJson(new { files });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceDiffAsync(JsonElement input, CancellationToken ct)
    {
        var sourceBranchId = Str(input, "branchId") ?? Str(input, "sourceBranchId");
        var targetBranchId = Str(input, "targetBranchId");
        if (sourceBranchId is null || targetBranchId is null) return ToError("branchId and targetBranchId are required.");
        try
        {
            var diff = await fileWorkspace.DiffAsync(sourceBranchId, targetBranchId, ct).ConfigureAwait(false);
            return ToJson(new { diff });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> WorkspaceExistsAsync(JsonElement input, CancellationToken ct)
    {
        var branchId = Str(input, "branchId");
        var path     = Str(input, "path");
        if (branchId is null || path is null) return ToError("branchId and path are required.");
        try
        {
            var exists = await fileWorkspace.ExistsAsync(branchId, path, ct).ConfigureAwait(false);
            return ToJson(new { exists });
        }
        catch (Exception ex) { return ToError(ex.Message); }
    }

    private async Task<string> SnapshotGetAsync(JsonElement input, CancellationToken ct)
    {
        var snap = await snapshots.GetAsync(
            Str(input, "agentId")!, Str(input, "workUnitId")!, ct).ConfigureAwait(false);
        return ToJson(snap);
    }

    private async Task<string> ProjectionGetAsync(JsonElement input, CancellationToken ct)
    {
        var typeStr  = Str(input, "projectionType") ?? Str(input, "type") ?? "WorkUnit";
        var levelStr = Str(input, "projectionLevel") ?? Str(input, "level") ?? "Normal";
        if (!Enum.TryParse<ProjectionType>(typeStr, ignoreCase: true, out var projType))
            return ToError($"Unknown projectionType '{typeStr}'.");
        if (!Enum.TryParse<ProjectionLevel>(levelStr, ignoreCase: true, out var projLevel))
            return ToError($"Unknown projectionLevel '{levelStr}'.");
        var result = await projections.GetAsync(
            new ProjectionRequest(projType, projLevel, Str(input, "workUnitId"), Str(input, "branchId"), Str(input, "agentId")),
            ct).ConfigureAwait(false);
        return result.DataJson;
    }

    private async Task<string> MergeApplyAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        try
        {
            var result = await merge.ApplyAsync(Str(input, "proposalId")!, ct).ConfigureAwait(false);
            return ToJson(new { proposalId = result.ProposalId, status = result.Status.ToString() });
        }
        catch (KeyNotFoundException ex) { return ToError(ex.Message); }
        catch (InvalidOperationException ex) { return ToError(ex.Message); }
    }

    private async Task<string> SchedulerEnqueueAsync(JsonElement input, CancellationToken ct, string? sessionId)
    {
        var workUnitId = Str(input, "workUnitId");
        var profileId  = Str(input, "profileId");
        if (workUnitId is null || profileId is null)
            return ToError("workUnitId and profileId are required.");

        await scheduler.EnqueueAsync(
            workUnitId,
            profileId,
            taskId:    Str(input, "taskId"),
            model:     Str(input, "model"),
            baseUrl:   Str(input, "baseUrl"),
            apiKey:    Str(input, "apiKey"),
            provider:  Str(input, "provider"),
            sessionId: sessionId,
            ct:        ct).ConfigureAwait(false);

        return ToJson(new { workUnitId, profileId, status = "enqueued" });
    }

    private async Task<string> SchedulerPendingAsync(CancellationToken ct)
    {
        var items = await scheduler.ListPendingAsync(ct).ConfigureAwait(false);
        return ToJson(items);
    }

    private static string? Str(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    private static int? Int(JsonElement input, string key) =>
        input.TryGetProperty(key, out var p) && p.TryGetInt32(out var v) ? v : null;

    private static string ToJson(object? data) => JsonSerializer.Serialize(data);
    private static string ToError(string message) => JsonSerializer.Serialize(new { error = message });
}
