using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Tasks;

public sealed class InMemoryTaskService : ITaskService
{
    private readonly ConcurrentDictionary<string, StudioTask> _tasks = new();

    public Task<StudioTask> CreateAsync(StudioTask task, CancellationToken cancellationToken = default)
    {
        _tasks[task.TaskId] = task;
        return Task.FromResult(task);
    }

    public Task<StudioTask> UpdateAsync(StudioTask task, CancellationToken cancellationToken = default)
    {
        _tasks[task.TaskId] = task;
        return Task.FromResult(task);
    }

    public Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken cancellationToken = default)
    {
        var items = _tasks.Values
            .Where(t => workUnitId is null || t.WorkUnitId == workUnitId)
            .OrderByDescending(t => t.Priority)
            .ToList();

        return Task.FromResult<IReadOnlyList<StudioTask>>(items);
    }

    public Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken cancellationToken = default)
    {
        if (!_tasks.TryGetValue(taskId, out var task))
        {
            throw new KeyNotFoundException($"Task '{taskId}' was not found.");
        }

        var updated = task with { Assignee = agentId, Status = NodalMerge.Studio.Contracts.Domain.TaskStatus.InProgress };
        _tasks[taskId] = updated;
        return Task.FromResult(updated);
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioTasks(this IServiceCollection services)
    {
        services.AddSingleton<ITaskService, InMemoryTaskService>();
        return services;
    }
}
