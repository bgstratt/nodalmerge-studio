using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;
using TaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Tasks;

public sealed class InMemoryTaskService : ITaskService, IRehydratable
{
    private readonly ConcurrentDictionary<string, StudioTask> _tasks = new();
    private readonly IWorkUnitService _workUnits;
    private readonly IStudioNodeStore _nodeStore;

    public InMemoryTaskService(IWorkUnitService workUnits, IStudioNodeStore nodeStore)
    {
        _workUnits = workUnits;
        _nodeStore  = nodeStore;
    }

    public async Task<StudioTask> CreateAsync(StudioTask task, CancellationToken cancellationToken = default)
    {
        var workUnit = await _workUnits.GetAsync(task.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (workUnit is null)
            throw new KeyNotFoundException($"Work unit '{task.WorkUnitId}' was not found.");

        _tasks[task.TaskId] = task;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskV1,
            task.TaskId,
            JsonSerializer.Serialize(task),
            cancellationToken).ConfigureAwait(false);
        return task;
    }

    public Task<StudioTask?> GetAsync(string taskId, CancellationToken cancellationToken = default)
    {
        _tasks.TryGetValue(taskId, out var task);
        return Task.FromResult(task);
    }

    public async Task<StudioTask> UpdateAsync(StudioTask task, CancellationToken cancellationToken = default)
    {
        var current = GetRequired(task.TaskId);

        if (current.Status != task.Status &&
            !TaskTransitions.CanTransition(current.Status, task.Status))
        {
            throw new InvalidOperationException(
                $"Cannot transition task from {current.Status} to {task.Status}.");
        }

        _tasks[task.TaskId] = task;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskV1,
            task.TaskId,
            JsonSerializer.Serialize(task),
            cancellationToken).ConfigureAwait(false);
        return task;
    }

    public Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken cancellationToken = default)
    {
        var items = _tasks.Values
            .Where(t => workUnitId is null || t.WorkUnitId == workUnitId)
            .OrderByDescending(t => t.Priority)
            .ToList();

        return Task.FromResult<IReadOnlyList<StudioTask>>(items);
    }

    public async Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken cancellationToken = default)
    {
        var task = GetRequired(taskId);

        if (!TaskTransitions.CanTransition(task.Status, TaskStatus.InProgress))
        {
            throw new InvalidOperationException(
                $"Cannot assign task '{taskId}': status {task.Status} cannot transition to InProgress.");
        }

        var updated = task with { Assignee = agentId, Status = TaskStatus.InProgress };
        _tasks[taskId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskV1,
            taskId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);
        return updated;
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.TaskV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var task = JsonSerializer.Deserialize<StudioTask>(payloadJson);
            if (task is not null)
                _tasks[entityId] = task;
        }
    }

    private StudioTask GetRequired(string taskId)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
            throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        return task;
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioTasks(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryTaskService>();
                services.AddSingleton<ITaskService>(sp => sp.GetRequiredService<InMemoryTaskService>());
                services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryTaskService>());
                services.AddSingleton<ITaskCommandService, TaskCommandService>();
                return services;
    }
}
