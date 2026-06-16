using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Orchestrator;

public sealed class FanOutService : IFanOutService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly IWorkUnitService _workUnits;
    private readonly IOrchestratorService _orchestrator;
    private readonly IArtifactLineageService _artifacts;
    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly IWorkScheduler _scheduler;
    private readonly ITaskService _tasks;
    private readonly IAgentControlService _agentControl;

    public FanOutService(
        IWorkUnitService workUnits,
        IOrchestratorService orchestrator,
        IArtifactLineageService artifacts,
        IFileWorkspaceService fileWorkspace,
        IWorkScheduler scheduler,
        ITaskService tasks,
        IAgentControlService agentControl)
    {
        _workUnits     = workUnits;
        _orchestrator  = orchestrator;
        _artifacts     = artifacts;
        _fileWorkspace = fileWorkspace;
        _scheduler     = scheduler;
        _tasks         = tasks;
        _agentControl  = agentControl;
    }

    public Task<FanOutResult> TryFanOutFromPlanAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken ct = default) =>
        ProcessAsync(parentWorkUnitId, createChildren: true, sessionId, ct);

    public Task<FanOutResult> TryEnqueueReadyDependentsAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken ct = default) =>
        ProcessAsync(parentWorkUnitId, createChildren: false, sessionId, ct);

    private async Task<FanOutResult> ProcessAsync(
        string parentWorkUnitId,
        bool createChildren,
        string? sessionId,
        CancellationToken ct)
    {
        var actions = new List<FanOutAction>();
        var enqueued = new List<string>();

        var parent = await _workUnits.GetAsync(parentWorkUnitId, ct).ConfigureAwait(false);
        if (parent is null)
            return new FanOutResult(actions, enqueued);

        var planContent = await _fileWorkspace
            .ReadAsync(parent.BranchId, PlanDocumentPaths.FileName, ct)
            .ConfigureAwait(false);
        if (planContent is null)
            return new FanOutResult(actions, enqueued);

        PlanDocument? plan;
        try
        {
            plan = JsonSerializer.Deserialize<PlanDocument>(planContent, JsonOpts);
        }
        catch (JsonException)
        {
            return new FanOutResult(actions, enqueued);
        }

        if (plan is null || plan.Slices.Count == 0)
            return new FanOutResult(actions, enqueued);

        if (await EnsurePlanArtifactAsync(parent, planContent, ct).ConfigureAwait(false))
            actions.Add(FanOutAction.PlanRecorded);

        var sliceIdToWorkUnitId = await BuildSliceMapAsync(parent.WorkUnitId, ct).ConfigureAwait(false);

        if (createChildren)
        {
            var created = await EnsureChildWorkUnitsAsync(parent, plan, sliceIdToWorkUnitId, ct).ConfigureAwait(false);
            if (created)
                actions.Add(FanOutAction.ChildrenCreated);
        }

        var creds = _agentControl.GetOrchestratorCredentials(parentWorkUnitId);
        var children = await _workUnits.GetChildrenAsync(parentWorkUnitId, ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            if (!await IsReadyToEnqueueAsync(child, ct).ConfigureAwait(false))
                continue;

            if (await EnqueueChildWorkerAsync(child, creds, sessionId, ct).ConfigureAwait(false))
            {
                actions.Add(FanOutAction.ChildEnqueued);
                enqueued.Add(child.WorkUnitId);
            }
        }

        return new FanOutResult(actions, enqueued);
    }

    private async Task<bool> EnsurePlanArtifactAsync(WorkUnit parent, string planContent, CancellationToken ct)
    {
        var chain = await _artifacts.GetChainAsync(parent.WorkUnitId, ct).ConfigureAwait(false);
        if (chain.Any(a => a.Type == ArtifactType.Plan))
            return false;

        var planId = $"PLAN-{Guid.NewGuid():N}";
        await _artifacts.RecordAsync(new ArtifactRef(
            planId,
            ArtifactType.Plan,
            parent.WorkUnitId,
            ArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            parent.WorkUnitId,
            null,
            "Plan",
            planContent), ct).ConfigureAwait(false);
        return true;
    }

    private async Task<Dictionary<string, string>> BuildSliceMapAsync(string parentWorkUnitId, CancellationToken ct)
    {
        var children = await _workUnits.GetChildrenAsync(parentWorkUnitId, ct).ConfigureAwait(false);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var child in children)
        {
            if (child.Metadata?.TryGetValue(WorkUnitMetadataKeys.SliceId, out var sliceId) == true &&
                !string.IsNullOrEmpty(sliceId))
            {
                map[sliceId] = child.WorkUnitId;
            }
        }

        return map;
    }

    private async Task<bool> EnsureChildWorkUnitsAsync(
        WorkUnit parent,
        PlanDocument plan,
        Dictionary<string, string> sliceIdToWorkUnitId,
        CancellationToken ct)
    {
        var created = false;
        var remaining = new HashSet<string>(plan.Slices.Select(s => s.SliceId), StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var progressed = false;
            foreach (var slice in plan.Slices.Where(s => remaining.Contains(s.SliceId)))
            {
                if (sliceIdToWorkUnitId.ContainsKey(slice.SliceId))
                {
                    remaining.Remove(slice.SliceId);
                    progressed = true;
                    continue;
                }

                if (slice.DependsOn.Any(dep => !sliceIdToWorkUnitId.ContainsKey(dep)))
                    continue;

                var resolvedDeps = slice.DependsOn
                    .Select(dep => sliceIdToWorkUnitId[dep])
                    .ToList();

                var metadata = new Dictionary<string, string>
                {
                    [WorkUnitMetadataKeys.SliceId] = slice.SliceId,
                    [WorkUnitMetadataKeys.SeedFromBranchId] = parent.BranchId,
                };

                var child = await _orchestrator.CreateWorkUnitAsync(
                    slice.Goal,
                    parent.Owner,
                    parentWorkUnitId: parent.WorkUnitId,
                    dependsOn: resolvedDeps,
                    fileScope: slice.FileScope,
                    seedFromBranchId: parent.BranchId,
                    metadata: metadata,
                    cancellationToken: ct).ConfigureAwait(false);

                sliceIdToWorkUnitId[slice.SliceId] = child.WorkUnitId;
                remaining.Remove(slice.SliceId);
                created = true;
                progressed = true;
            }

            if (!progressed)
                break;
        }

        return created;
    }

    private async Task<bool> IsReadyToEnqueueAsync(WorkUnit child, CancellationToken ct)
    {
        if (child.Status is not WorkUnitStatus.Created)
            return false;

        foreach (var depId in child.DependsOn)
        {
            var dep = await _workUnits.GetAsync(depId, ct).ConfigureAwait(false);
            if (dep is null)
                return false;

            if (dep.Status is not WorkUnitStatus.Proposed and not WorkUnitStatus.Merged)
                return false;
        }

        return true;
    }

    private async Task<bool> EnqueueChildWorkerAsync(
        WorkUnit child,
        OrchestratorCredentials? creds,
        string? sessionId,
        CancellationToken ct)
    {
        var existingTasks = await _tasks.ListAsync(child.WorkUnitId, ct).ConfigureAwait(false);
        var task = existingTasks.FirstOrDefault();
        if (task is null)
        {
            task = await _tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"),
                child.WorkUnitId,
                child.Goal,
                $"Execute slice for {child.Goal}",
                StudioTaskStatus.Open,
                null,
                0), ct).ConfigureAwait(false);

            await _artifacts.RecordAsync(new ArtifactRef(
                task.TaskId,
                ArtifactType.Task,
                child.WorkUnitId,
                ArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                child.WorkUnitId,
                null), ct).ConfigureAwait(false);
        }

        await _scheduler.EnqueueAsync(
            child.WorkUnitId,
            "worker",
            task.TaskId,
            creds?.Model,
            creds?.BaseUrl,
            creds?.ApiKey,
            creds?.Provider,
            sessionId,
            ct).ConfigureAwait(false);

        return true;
    }
}
