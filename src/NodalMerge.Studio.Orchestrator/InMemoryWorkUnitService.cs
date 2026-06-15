using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Orchestrator;

public sealed class InMemoryWorkUnitService : IWorkUnitService, IOrchestratorService, IWorkspaceService
{
    private readonly ConcurrentDictionary<string, WorkUnit> _workUnits = new();
    private readonly IBranchService _branchService;
    private readonly IMergeService _mergeService;
    private readonly IKnownGoodStateService _knownGoodStateService;
    private readonly IAgentControlService _agentControl;
    private readonly IStudioNodeStore _nodeStore;

    public InMemoryWorkUnitService(
        IBranchService branchService,
        IMergeService mergeService,
        IKnownGoodStateService knownGoodStateService,
        IAgentControlService agentControl,
        IStudioNodeStore nodeStore)
    {
        _branchService         = branchService;
        _mergeService          = mergeService;
        _knownGoodStateService = knownGoodStateService;
        _agentControl          = agentControl;
        _nodeStore             = nodeStore;
    }

    public async Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default)
    {
        _workUnits[workUnit.WorkUnitId] = workUnit;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnit.WorkUnitId,
            JsonSerializer.Serialize(workUnit),
            cancellationToken).ConfigureAwait(false);
        return workUnit;
    }

    public async Task<WorkUnit> UpdateStatusAsync(
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
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnitId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);
        return updated;
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

    public async Task AssignWorkAsync(string workUnitId, string agentId, CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        var updated = workUnit with
        {
            AssignedAgent = agentId,
            Status        = WorkUnitStatus.Active,
            UpdatedAt     = DateTimeOffset.UtcNow,
        };
        _workUnits[workUnitId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnitId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<WorkspaceSummary> GetSummaryAsync(string? branchId = null, CancellationToken cancellationToken = default)
    {
        var activeUnits = _workUnits.Values
            .Where(w => branchId is null || w.BranchId == branchId)
            .Where(w => w.Status is WorkUnitStatus.Created or WorkUnitStatus.Active or WorkUnitStatus.Waiting)
            .Select(w => w.WorkUnitId)
            .ToList();

        var failures = _workUnits.Values
            .Where(w => branchId is null || w.BranchId == branchId)
            .Where(w => w.Status is WorkUnitStatus.Failed)
            .Select(w => w.WorkUnitId)
            .ToList();

        var allProposals = await _mergeService.ListAsync(branchId, cancellationToken).ConfigureAwait(false);
        var pendingMerges = allProposals
            .Where(p => p.Status is MergeProposalStatus.Draft or MergeProposalStatus.ReadyForReview)
            .Select(p => p.ProposalId)
            .ToList();

        IReadOnlyList<string> knownGoodStates = branchId is not null
            ? (await _knownGoodStateService.FindKnownGoodAsync(branchId, cancellationToken).ConfigureAwait(false))
                .Select(k => k.StateId).ToList()
            : [];

        var allAgents = await _agentControl.ListActiveAsync(cancellationToken).ConfigureAwait(false);
        var activeAgents = branchId is null
            ? allAgents.Select(a => a.AgentId).ToList()
            : allAgents
                .Where(a => _workUnits.TryGetValue(a.WorkUnitId, out var wu) && wu.BranchId == branchId)
                .Select(a => a.AgentId)
                .ToList();

        return new WorkspaceSummary(
            activeUnits,
            activeAgents,
            pendingMerges,
            failures,
            knownGoodStates);
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
