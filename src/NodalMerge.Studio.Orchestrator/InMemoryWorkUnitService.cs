using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Orchestrator;

public sealed class InMemoryWorkUnitService : IWorkUnitService, IOrchestratorService, IWorkspaceService
{
    private readonly ConcurrentDictionary<string, WorkUnit> _workUnits = new();
    private readonly IBranchService _branchService;

    public InMemoryWorkUnitService(IBranchService branchService)
    {
        _branchService = branchService;
    }

    public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default)
    {
        _workUnits[workUnit.WorkUnitId] = workUnit;
        return Task.FromResult(workUnit);
    }

    public Task<WorkUnit> UpdateStatusAsync(
        string workUnitId,
        WorkUnitStatus status,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        if (!WorkUnitTransitions.CanTransition(workUnit.Status, status))
        {
            throw new InvalidOperationException($"Cannot transition work unit from {workUnit.Status} to {status}.");
        }

        var updated = workUnit with { Status = status, UpdatedAt = DateTimeOffset.UtcNow };
        _workUnits[workUnitId] = updated;
        return Task.FromResult(updated);
    }

    public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        _workUnits.TryGetValue(workUnitId, out var workUnit);
        return Task.FromResult(workUnit);
    }

    public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken cancellationToken = default)
    {
        var items = _workUnits.Values
            .Where(w => branchId is null || w.BranchId == branchId)
            .OrderByDescending(w => w.UpdatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<WorkUnit>>(items);
    }

    public async Task<WorkUnit> CreateWorkUnitAsync(
        string goal,
        string owner,
        string? successCriteria = null,
        CancellationToken cancellationToken = default)
    {
        var branchId = await _branchService.CreateBranchAsync($"work-{Guid.NewGuid():N}", cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        var workUnit = new WorkUnit(
            WorkUnitId: Guid.NewGuid().ToString("N"),
            Goal: goal,
            BranchId: branchId,
            Status: WorkUnitStatus.Created,
            CreatedAt: now,
            UpdatedAt: now,
            Owner: owner,
            AssignedAgent: null,
            SuccessCriteria: successCriteria,
            Metadata: null);

        return await CreateAsync(workUnit, cancellationToken).ConfigureAwait(false);
    }

    public Task AssignWorkAsync(string workUnitId, string agentId, CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        _workUnits[workUnitId] = workUnit with
        {
            AssignedAgent = agentId,
            Status = WorkUnitStatus.Active,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return Task.CompletedTask;
    }

    public Task<WorkspaceSummary> GetSummaryAsync(string? branchId = null, CancellationToken cancellationToken = default)
    {
        var units = _workUnits.Values
            .Where(w => branchId is null || w.BranchId == branchId)
            .Where(w => w.Status is WorkUnitStatus.Created or WorkUnitStatus.Active or WorkUnitStatus.Waiting)
            .Select(w => w.WorkUnitId)
            .ToList();

        return Task.FromResult(new WorkspaceSummary(
            units,
            [],
            [],
            [],
            []));
    }

    private WorkUnit GetRequired(string workUnitId)
    {
        if (!_workUnits.TryGetValue(workUnitId, out var workUnit))
        {
            throw new KeyNotFoundException($"Work unit '{workUnitId}' was not found.");
        }

        return workUnit;
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioOrchestrator(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryWorkUnitService>();
        services.AddSingleton<IWorkUnitService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IOrchestratorService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IWorkspaceService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        return services;
    }
}
