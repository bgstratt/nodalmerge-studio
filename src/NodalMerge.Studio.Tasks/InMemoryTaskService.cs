using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
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
    private readonly ILogger<InMemoryTaskService>? _logger;

    // A4 (plans/test-suite-remediation-plan.md): the optional logger exists so a refused task
    // transition leaves a breadcrumb. Many callers wrap UpdateAsync/AssignAsync in
    // `catch (InvalidOperationException) {}` because a convergent caller retrying an already-applied
    // status is benign — but that same swallow hid a real caller bug (a task completed straight from
    // Open, skipping InProgress). Logging the refusal at the source, at Debug, makes the benign case
    // quiet-but-visible and the buggy case findable, without changing what is legal.
    public InMemoryTaskService(
        IWorkUnitService workUnits,
        IStudioNodeStore nodeStore,
        ILogger<InMemoryTaskService>? logger = null)
    {
        _workUnits = workUnits;
        _nodeStore  = nodeStore;
        _logger     = logger;
    }

    public async Task<StudioTask> CreateAsync(StudioTask task, CancellationToken cancellationToken = default)
    {
        var workUnit = await _workUnits.GetAsync(task.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (workUnit is null)
            throw new KeyNotFoundException($"Work unit '{task.WorkUnitId}' was not found.");

        // Slice 6.3a — a task always has exactly one WorkUnitId (required field), so this is a
        // direct copy of the already-resolved WorkUnit.RepositoryId, never a chain walk.
        var stored = task.RepositoryId is null ? task with { RepositoryId = workUnit.RepositoryId } : task;

        _tasks[stored.TaskId] = stored;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskV1,
            stored.TaskId,
            JsonSerializer.Serialize(stored),
            stored.RepositoryId,
            cancellationToken).ConfigureAwait(false);
        return stored;
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
            _logger?.LogDebug(
                "Refused task transition {TaskId}: {From} -> {To} (not a legal edge). If a caller "
                + "swallows this, the transition simply did not happen.",
                task.TaskId, current.Status, task.Status);
            throw new InvalidOperationException(
                $"Cannot transition task from {current.Status} to {task.Status}.");
        }

        // RepositoryId is effectively immutable once set at CreateAsync — preserve the current
        // value if the caller's `with`-copy somehow lost it.
        var stored = task.RepositoryId is null && current.RepositoryId is not null
            ? task with { RepositoryId = current.RepositoryId }
            : task;

        _tasks[stored.TaskId] = stored;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskV1,
            stored.TaskId,
            JsonSerializer.Serialize(stored),
            stored.RepositoryId,
            cancellationToken).ConfigureAwait(false);
        return stored;
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
            _logger?.LogDebug(
                "Refused task assignment {TaskId}: {From} -> InProgress (not a legal edge).",
                taskId, task.Status);
            throw new InvalidOperationException(
                $"Cannot assign task '{taskId}': status {task.Status} cannot transition to InProgress.");
        }

        var updated = task with { Assignee = agentId, Status = TaskStatus.InProgress };
        _tasks[taskId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.TaskV1,
            taskId,
            JsonSerializer.Serialize(updated),
            updated.RepositoryId,
            cancellationToken).ConfigureAwait(false);
        return updated;
    }

    // Slice 6.5 Part 1 — see InMemoryWorkUnitService.RehydratedKinds' doc comment.
    public IReadOnlyCollection<string> RehydratedKinds => [StudioNodeKind.TaskV1];

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
