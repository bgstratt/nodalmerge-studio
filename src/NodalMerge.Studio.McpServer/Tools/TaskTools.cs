using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class TaskTools(ITaskCommandService taskCommands)
{
    [McpServerTool(Name = McpToolNames.TaskCreate), Description("Create a task for a work unit and record an artifact-lineage entry for it.")]
    public async Task<string> CreateAsync(
        string workUnitId,
        string title,
        string description,
        string? branchId = null,
        int priority = 0,
        CancellationToken cancellationToken = default)
    {
        var task = await taskCommands.CreateAsync(
            new TaskCreateCommand(workUnitId, title, description, priority),
            cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { taskId = task.TaskId, branchId });
    }

    [McpServerTool(Name = McpToolNames.TaskUpdate), Description("Update an existing task.")]
    public async Task<string> UpdateAsync(
        string taskId,
        string? status = null,
        string? title = null,
        string? description = null,
        int? priority = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await taskCommands.UpdateAsync(
                taskId, title, description, status, priority, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(result);
        }
        catch (KeyNotFoundException)
        {
            return McpJson.Error(McpToolNames.TaskUpdate, $"Task '{taskId}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpToolNames.TaskUpdate, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.TaskList), Description("List tasks, optionally filtered by work unit.")]
    public async Task<string> ListAsync(string? workUnitId = null, string? branchId = null, CancellationToken cancellationToken = default) =>
        McpJson.Ok(await taskCommands.ListAsync(workUnitId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = McpToolNames.TaskAssign), Description("Assign a task to an agent.")]
    public async Task<string> AssignAsync(
        string taskId,
        string agentId,
        string? branchId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var assigned = await taskCommands.AssignAsync(taskId, agentId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new { assigned, branchId });
        }
        catch (KeyNotFoundException)
        {
            return McpJson.Error(McpToolNames.TaskAssign, $"Task '{taskId}' was not found.");
        }
        catch (InvalidOperationException ex)
        {
            return McpJson.Error(McpToolNames.TaskAssign, ex.Message);
        }
    }
}
