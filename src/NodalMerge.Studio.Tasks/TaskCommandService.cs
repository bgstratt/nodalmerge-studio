using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using StudioArtifactStatus = NodalMerge.Studio.Contracts.Domain.ArtifactStatus;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Tasks;

// Slice 15c — the one implementation of task commands shared by the MCP tool, the REST
// endpoint, and the agent-loop's in-process dispatcher. Folds in the artifact-lineage
// record that previously only McpToolDispatcher.TaskCreateAsync wrote.
public sealed class TaskCommandService(ITaskService tasks, IArtifactLineageService artifactLineage) : ITaskCommandService
{
    public async Task<StudioTask> CreateAsync(TaskCreateCommand command, CancellationToken cancellationToken = default)
    {
        var taskId = Guid.NewGuid().ToString("N");
        var task = new StudioTask(taskId, command.WorkUnitId, command.Title, command.Description, StudioTaskStatus.Open, null, command.Priority);
        var created = await tasks.CreateAsync(task, cancellationToken).ConfigureAwait(false);

        // Slice 15c — artifact-lineage record was previously only written by the
        // agent-loop dispatcher path. Now recorded for every transport.
        await artifactLineage.RecordAsync(new ArtifactRef(
            created.TaskId,
            ArtifactType.Task,
            command.WorkUnitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            command.WorkUnitId,
            null), cancellationToken).ConfigureAwait(false);

        return created;
    }

    public async Task<StudioTask> UpdateAsync(
        string taskId,
        string? title = null,
        string? description = null,
        string? status = null,
        int? priority = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await tasks.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            throw new KeyNotFoundException($"Task '{taskId}' was not found.");

        var taskStatus = existing.Status;
        if (status is not null &&
            !Enum.TryParse<StudioTaskStatus>(status, ignoreCase: true, out taskStatus))
        {
            throw new InvalidOperationException("Invalid task status.");
        }

        var updated = existing with
        {
            Title = title ?? existing.Title,
            Description = description ?? existing.Description,
            Status = taskStatus,
            Priority = priority ?? existing.Priority
        };

        return await tasks.UpdateAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    public Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken cancellationToken = default) =>
        tasks.AssignAsync(taskId, agentId, cancellationToken);

    public Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken cancellationToken = default) =>
        tasks.ListAsync(workUnitId, cancellationToken);
}