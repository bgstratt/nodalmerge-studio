using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Orchestrator;

public sealed class InMemoryWorkUnitService : IWorkUnitService, IOrchestratorService, IWorkspaceService, IRehydratable
{
    private readonly ConcurrentDictionary<string, WorkUnit> _workUnits = new();
    private readonly IBranchService _branchService;
    private readonly IMergeService _mergeService;
    private readonly IKnownGoodStateService _knownGoodStateService;
    private readonly IAgentControlService _agentControl;
    private readonly IStudioNodeStore _nodeStore;
    private readonly IArtifactLineageService _artifactLineage;
    private readonly WorkspaceOptions _workspaceOptions;
    private readonly IExecutionEventStream _events;
    private readonly IRuntimeEventBroadcaster? _broadcaster;

    public InMemoryWorkUnitService(
        IBranchService branchService,
        IMergeService mergeService,
        IKnownGoodStateService knownGoodStateService,
        IAgentControlService agentControl,
        IStudioNodeStore nodeStore,
        IArtifactLineageService artifactLineage,
        WorkspaceOptions workspaceOptions,
        IExecutionEventStream events,
        IRuntimeEventBroadcaster? broadcaster = null)
    {
        _branchService         = branchService;
        _mergeService          = mergeService;
        _knownGoodStateService = knownGoodStateService;
        _agentControl          = agentControl;
        _nodeStore             = nodeStore;
        _artifactLineage       = artifactLineage;
        _workspaceOptions      = workspaceOptions;
        _events                = events;
        _broadcaster           = broadcaster;
    }

    public async Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default)
    {
        if (workUnit.ParentWorkUnitId is not null && !_workUnits.ContainsKey(workUnit.ParentWorkUnitId))
            throw new KeyNotFoundException($"Parent work unit '{workUnit.ParentWorkUnitId}' was not found.");

        _workUnits[workUnit.WorkUnitId] = workUnit;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnit.WorkUnitId,
            JsonSerializer.Serialize(workUnit),
            cancellationToken).ConfigureAwait(false);

        // The work unit's own ID doubles as its Goal artifact's ID — every other artifact in
        // its chain (Task, MergeProposal, ...) traces back to this root. A child work unit's
        // Goal is parented to the parent work unit's own Goal (same ID), so the artifact graph
        // and the work-unit DAG agree — this is also how 10f's branch-from-proposal lineage
        // becomes traversable via GetChildrenAsync without a separate artifact type.
        await _artifactLineage.RecordAsync(new ArtifactRef(
            workUnit.WorkUnitId,
            ArtifactType.Goal,
            workUnit.ParentWorkUnitId,
            ArtifactStatus.Active,
            workUnit.CreatedAt,
            workUnit.WorkUnitId,
            null), cancellationToken).ConfigureAwait(false);

        return workUnit;
    }

    public async Task<WorkUnit> UpdateStatusAsync(
        string workUnitId,
        WorkUnitStatus status,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        if (!WorkUnitTransitions.CanTransition(workUnit.Status, status))
        {
            throw new InvalidOperationException($"Cannot transition work unit from {workUnit.Status} to {status}.");
        }

        var previousStatus = workUnit.Status;
        var updated = workUnit with { Status = status, UpdatedAt = DateTimeOffset.UtcNow };
        _workUnits[workUnitId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnitId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (sessionId is not null)
        {
            await _events.AppendAsync(
                sessionId,
                workUnitId,
                ExecutionEventKind.WorkUnitStatusChanged,
                new WorkUnitStatusChangedPayload(workUnitId, previousStatus, status),
                ct: cancellationToken).ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<WorkUnit> SetCurrentStageAsync(
        string workUnitId,
        PipelineStage? stage,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        var updated = workUnit with { CurrentStage = stage, UpdatedAt = DateTimeOffset.UtcNow };
        _workUnits[workUnitId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnitId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        if (_broadcaster is not null)
        {
            await _broadcaster.BroadcastWorkUnitStageChangedAsync(workUnitId, stage, cancellationToken)
                .ConfigureAwait(false);
        }

        return updated;
    }

    public async Task<WorkUnit> SetFanOutBlockedReasonAsync(
        string workUnitId,
        string? blockedReason,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        var fanOutInfo = workUnit.FanOutInfo is null
            ? (blockedReason is null ? null : new WorkUnitFanOutInfo(null, null, blockedReason))
            : workUnit.FanOutInfo with { BlockedReason = blockedReason };

        var updated = workUnit with { FanOutInfo = fanOutInfo, UpdatedAt = DateTimeOffset.UtcNow };
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
        string? branchId = null,
        string? successCriteria = null,
        string? repositoryPath = null,
        string? parentWorkUnitId = null,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? fileScope = null,
        string? seedFromBranchId = null,
        string? branchedFromProposalId = null,
        string? sliceId = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default)
    {
        // First work unit with a repositoryPath seeds the main branch for this session.
        if (!string.IsNullOrWhiteSpace(repositoryPath) &&
            string.IsNullOrWhiteSpace(_workspaceOptions.SeedRepositoryPath))
        {
            _workspaceOptions.SeedRepositoryPath = repositoryPath;
        }

        // Slice 15b — branchId used to be patched onto the WorkUnit record *after* this method
        // returned (by the MCP tool and the agent-loop dispatcher, independently), which left the
        // real generated "work-{guid}" branch (and any seedFromBranchId content copied into it)
        // orphaned, and the caller-chosen name never registered with IBranchService at all
        // (nm_v1_branch_status on it would report "unknown" even though the work unit was "on"
        // it). Resolving it here means it actually gets created/seeded like any other branch.
        var resolvedBranchId = await _branchService
            .CreateBranchAsync(branchId ?? $"work-{Guid.NewGuid():N}", seedFromBranchId, cancellationToken)
            .ConfigureAwait(false);

        var fanOutInfo = sliceId is not null || seedFromBranchId is not null
            ? new WorkUnitFanOutInfo(sliceId, seedFromBranchId)
            : null;

        var now = DateTimeOffset.UtcNow;
        var workUnit = new WorkUnit(
            WorkUnitId: Guid.NewGuid().ToString("N"),
            Goal: goal,
            BranchId: resolvedBranchId,
            Status: WorkUnitStatus.Created,
            CreatedAt: now,
            UpdatedAt: now,
            Owner: owner,
            AssignedAgent: null,
            SuccessCriteria: successCriteria,
            Metadata: metadata,
            ParentWorkUnitId: parentWorkUnitId,
            DependsOn: dependsOn ?? [],
            FileScope: fileScope ?? [],
            FanOutInfo: fanOutInfo,
            BranchedFromProposalId: branchedFromProposalId);

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
            .Where(w => w.Status is WorkUnitStatus.Failed or WorkUnitStatus.DeadLettered)
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

    public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken cancellationToken = default)
    {
        var children = _workUnits.Values
            .Where(w => w.ParentWorkUnitId == parentId)
            .OrderBy(w => w.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<WorkUnit>>(children);
    }

    public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        var dependents = _workUnits.Values
            .Where(w => w.DependsOn.Contains(workUnitId))
            .OrderBy(w => w.CreatedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<WorkUnit>>(dependents);
    }

    // Slice 0a — bypasses CreateAsync's parent-existence check (children can be loaded before
    // their parents) and never re-emits artifacts/events; just repopulates the dictionary from
    // what was already durably written.
    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.WorkUnitV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var workUnit = JsonSerializer.Deserialize<WorkUnit>(payloadJson);
            if (workUnit is not null)
                _workUnits[entityId] = workUnit;
        }
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
        services.AddSingleton<InMemoryWorkUnitService>(sp => new InMemoryWorkUnitService(
            sp.GetRequiredService<IBranchService>(),
            sp.GetRequiredService<IMergeService>(),
            sp.GetRequiredService<IKnownGoodStateService>(),
            sp.GetRequiredService<IAgentControlService>(),
            sp.GetRequiredService<IStudioNodeStore>(),
            sp.GetRequiredService<IArtifactLineageService>(),
            sp.GetService<WorkspaceOptions>() ?? new WorkspaceOptions(),
            sp.GetRequiredService<IExecutionEventStream>(),
            sp.GetService<IRuntimeEventBroadcaster>()));
        services.AddSingleton<IWorkUnitService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IOrchestratorService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IWorkspaceService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IFanOutService, FanOutService>();
        services.AddSingleton<IWorkUnitCommandService, WorkUnitCommandService>();
        return services;
    }
}
