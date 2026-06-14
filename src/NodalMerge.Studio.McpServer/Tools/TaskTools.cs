using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class TaskTools(ITaskService tasks)
{
    [McpServerTool(Name = McpToolNames.TaskCreate), Description("Create a task for a work unit.")]
    public async Task<string> CreateAsync(
        string workUnitId,
        string title,
        string description,
        string? branchId = null,
        int priority = 0,
        CancellationToken cancellationToken = default)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var task = new StudioTask(taskId, workUnitId, title, description, StudioTaskStatus.Open, null, priority);
        var created = await tasks.CreateAsync(task, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { taskId = created.TaskId, branchId });
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
        var existing = (await tasks.ListAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
            .FirstOrDefault(t => t.TaskId == taskId);

        if (existing is null)
        {
            return McpJson.Error(McpToolNames.TaskUpdate, $"Task '{taskId}' was not found.");
        }

        var taskStatus = existing.Status;
        if (status is not null &&
            !Enum.TryParse<StudioTaskStatus>(status, ignoreCase: true, out taskStatus))
        {
            return McpJson.Error(McpToolNames.TaskUpdate, "Invalid task status.");
        }

        var updated = existing with
        {
            Title = title ?? existing.Title,
            Description = description ?? existing.Description,
            Status = taskStatus,
            Priority = priority ?? existing.Priority
        };

        var result = await tasks.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(result);
    }

    [McpServerTool(Name = McpToolNames.TaskList), Description("List tasks, optionally filtered by work unit.")]
    public async Task<string> ListAsync(string? workUnitId = null, string? branchId = null, CancellationToken cancellationToken = default) =>
        McpJson.Ok(await tasks.ListAsync(workUnitId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = McpToolNames.TaskAssign), Description("Assign a task to an agent.")]
    public async Task<string> AssignAsync(
        string taskId,
        string agentId,
        string? branchId = null,
        CancellationToken cancellationToken = default)
    {
        var assigned = await tasks.AssignAsync(taskId, agentId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { assigned, branchId });
    }
}
