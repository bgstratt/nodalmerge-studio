using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class WorkSchedulerService : IWorkScheduler
{
    private static readonly TimeSpan LeaseTimeout = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, ScheduledItem> _queue = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IExecutionEventStream _events;
    private readonly IAgentWorkspaceService _workspaces;
    private readonly IServiceProvider? _serviceProvider;
    private readonly IArtifactLineageService? _artifacts;
    private readonly IMergeService? _merge;
    private readonly IIntentGraphService? _intents;

    // IWorkUnitService is resolved lazily (via IServiceProvider) rather than constructor-injected:
    // its production implementation depends on IAgentControlService, which depends on IWorkScheduler
    // (this service) — a direct dependency here would be a circular constructor graph. Same pattern
    // as AgentWorkspaceService. IIntentGraphService has no such cycle (depends only on IStudioNodeStore
    // and IArtifactLineageService) so it's constructor-injected directly.
    public WorkSchedulerService(
        IStudioNodeStore nodeStore,
        IExecutionEventStream events,
        IAgentWorkspaceService workspaces,
        IServiceProvider? serviceProvider = null,
        IArtifactLineageService? artifacts = null,
        IMergeService? merge = null,
        IIntentGraphService? intents = null)
    {
        _nodeStore       = nodeStore;
        _events          = events;
        _workspaces      = workspaces;
        _serviceProvider = serviceProvider;
        _artifacts       = artifacts;
        _merge           = merge;
        _intents         = intents;
    }

    public async Task EnqueueAsync(
        string workUnitId,
        string profileId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        // Idempotent: track whether this is a new enqueue to avoid duplicate events.
        bool isNew = !_queue.ContainsKey(workUnitId);

        var conflict = await DetectConflictAsync(workUnitId, ct).ConfigureAwait(false);

        _queue.AddOrUpdate(
            workUnitId,
            _ => new ScheduledItem(workUnitId, profileId, taskId, null, null, 0, model, baseUrl, apiKey, provider, sessionId, conflict),
            (_, existing) => existing.LeasedBy is not null
                ? existing  // keep leased item unchanged
                : existing with { ProfileId = profileId, TaskId = taskId, Model = model, BaseUrl = baseUrl, ApiKey = apiKey, Provider = provider, SessionId = sessionId, Conflict = conflict });

        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.SchedulerV1,
            workUnitId,
            JsonSerializer.Serialize(_queue[workUnitId]),
            ct).ConfigureAwait(false);

        // Only emit events/transition status on the first enqueue, not on retries.
        if (isNew)
        {
            await UpdateWorkUnitStatusAsync(workUnitId, WorkUnitStatus.Queued, sessionId, ct).ConfigureAwait(false);

            if (sessionId is not null)
            {
                var item = _queue[workUnitId];
                await _events.AppendAsync(
                    sessionId,
                    workUnitId,
                    ExecutionEventKind.WorkUnitScheduled,
                    new WorkUnitScheduledPayload(workUnitId, profileId, item.AttemptCount),
                    ct: ct).ConfigureAwait(false);

                if (conflict is not null)
                {
                    await _events.AppendAsync(
                        sessionId,
                        workUnitId,
                        ExecutionEventKind.ConflictDetected,
                        new ConflictDetectedPayload(workUnitId, conflict.OverlappingFiles, conflict.ConflictingWorkUnitIds),
                        ct: ct).ConfigureAwait(false);
                }
            }
        }
    }

    // Pre-detects overlap from two independent sources, merged into one warning:
    //  - 10d (reactive): this unit's FileScope vs. the FilesTouched of MergeProposals siblings
    //    have already raised.
    //  - 10f.5 (proactive): this unit's own declared ChangeIntents vs. anyone else's overlapping
    //    intents — global by TargetPath/RegionDescriptor, not limited to siblings, since two
    //    unrelated work units touching the same file is exactly the case intents exist to catch
    //    before either one writes anything.
    // Advisory only — does not block enqueue; Phase 4's merger/region-locking resolves real conflicts.
    private async Task<ConflictWarning?> DetectConflictAsync(string workUnitId, CancellationToken ct)
    {
        var overlappingFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflictingWorkUnitIds = new HashSet<string>();

        var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
        if (workUnits is not null && _artifacts is not null && _merge is not null)
        {
            var unit = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (unit is not null && unit.FileScope.Count > 0 && unit.ParentWorkUnitId is not null)
            {
                var siblings = await workUnits.GetChildrenAsync(unit.ParentWorkUnitId, ct).ConfigureAwait(false);

                foreach (var sibling in siblings.Where(s => s.WorkUnitId != workUnitId))
                {
                    var chain = await _artifacts.GetChainAsync(sibling.WorkUnitId, ct).ConfigureAwait(false);

                    foreach (var proposalRef in chain.Where(a => a.Type == ArtifactType.MergeProposal))
                    {
                        var proposal = await _merge.GetAsync(proposalRef.ArtifactId, ct).ConfigureAwait(false);
                        if (proposal is null)
                            continue;

                        foreach (var file in proposal.FilesTouched)
                        {
                            if (!unit.FileScope.Any(pattern => AgentWorkspaceService.MatchesGlob(pattern.Replace('\\', '/'), file.Replace('\\', '/'))))
                                continue;

                            overlappingFiles.Add(file);
                            conflictingWorkUnitIds.Add(sibling.WorkUnitId);
                        }
                    }
                }
            }
        }

        if (_intents is not null)
        {
            var myIntents = await _intents.QueryIntentsAsync(workUnitId, ct).ConfigureAwait(false);
            foreach (var intent in myIntents)
            {
                var overlapping = await _intents.QueryOverlappingAsync(intent, ct).ConfigureAwait(false);
                foreach (var other in overlapping)
                {
                    overlappingFiles.Add(intent.TargetPath);
                    conflictingWorkUnitIds.Add(other.WorkUnitId);
                }
            }
        }

        return overlappingFiles.Count == 0
            ? null
            : new ConflictWarning(overlappingFiles.ToList(), conflictingWorkUnitIds.ToList());
    }

    public async Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        // Re-acquire: if this agent already holds a valid lease, return it without re-emitting events.
        var reacquired = _queue.Values.FirstOrDefault(i =>
            i.LeasedBy == agentId &&
            i.LeasedAt.HasValue &&
            now - i.LeasedAt.Value < LeaseTimeout);
        if (reacquired is not null)
            return reacquired;

        foreach (var (key, item) in _queue)
        {
            // Skip items with a valid (non-expired) lease.
            if (item.LeasedBy is not null && item.LeasedAt.HasValue &&
                now - item.LeasedAt.Value < LeaseTimeout)
                continue;

            var acquired = item with
            {
                LeasedBy = agentId,
                LeasedAt = now,
                AttemptCount = item.AttemptCount + 1,
            };

            // Compare-and-swap to avoid double-acquisition.
            if (!_queue.TryUpdate(key, acquired, item))
                continue;

            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.SchedulerV1, key,
                JsonSerializer.Serialize(acquired), ct).ConfigureAwait(false);

            if (acquired.SessionId is not null)
            {
                await _events.AppendAsync(
                    acquired.SessionId,
                    acquired.WorkUnitId,
                    ExecutionEventKind.SchedulerLeaseAcquired,
                    new SchedulerLeaseAcquiredPayload(
                        acquired.WorkUnitId,
                        agentId,
                        now + LeaseTimeout),
                    ct: ct).ConfigureAwait(false);
            }

            await _workspaces.CreateAsync(acquired.WorkUnitId, "main", acquired.SessionId, ct).ConfigureAwait(false);

            await UpdateWorkUnitStatusAsync(acquired.WorkUnitId, WorkUnitStatus.Executing, acquired.SessionId, ct).ConfigureAwait(false);

            return acquired;
        }

        return null;
    }

    public async Task ReleaseAsync(string workUnitId, bool success, CancellationToken ct = default)
    {
        if (success)
        {
            _queue.TryRemove(workUnitId, out var removed);
            var sessionId  = removed?.SessionId;
            var agentId    = removed?.LeasedBy ?? string.Empty;

            await _nodeStore.WriteNodeAsync(
                StudioNodeKind.SchedulerV1, workUnitId,
                "{\"status\":\"completed\"}", ct).ConfigureAwait(false);

            if (sessionId is not null)
            {
                var completedEv = await _events.AppendAsync(
                    sessionId,
                    workUnitId,
                    ExecutionEventKind.WorkUnitCompleted,
                    new WorkUnitCompletedPayload(workUnitId, agentId, null),
                    ct: ct).ConfigureAwait(false);

                await _events.AppendAsync(
                    sessionId,
                    workUnitId,
                    ExecutionEventKind.SchedulerLeaseReleased,
                    new SchedulerLeaseReleasedPayload(workUnitId, agentId, true),
                    causedByEventId: completedEv.EventId,
                    ct: ct).ConfigureAwait(false);
            }

            await _workspaces.ArchiveAsync($"ws-{workUnitId}", sessionId, ct).ConfigureAwait(false);

            await ReinvokeOrchestratorAsync(workUnitId, sessionId, ct).ConfigureAwait(false);
        }
        else
        {
            // Reset lease so the next poll can retry. The workspace is intentionally left alone
            // (not destroyed) here — it's keyed 1:1 to the work unit, not the attempt, and the
            // next retry reuses it. DestroyAsync is for a work unit that is abandoned for good.
            if (_queue.TryGetValue(workUnitId, out var item))
            {
                var sessionId = item.SessionId;
                var agentId   = item.LeasedBy ?? string.Empty;
                var reset     = item with { LeasedBy = null, LeasedAt = null };
                _queue.TryUpdate(workUnitId, reset, item);
                await _nodeStore.WriteNodeAsync(
                    StudioNodeKind.SchedulerV1, workUnitId,
                    JsonSerializer.Serialize(reset), ct).ConfigureAwait(false);

                if (sessionId is not null)
                {
                    var failedEv = await _events.AppendAsync(
                        sessionId,
                        workUnitId,
                        ExecutionEventKind.WorkUnitFailed,
                        new WorkUnitFailedPayload(workUnitId, agentId, "Agent loop exited without success"),
                        ct: ct).ConfigureAwait(false);

                    await _events.AppendAsync(
                        sessionId,
                        workUnitId,
                        ExecutionEventKind.SchedulerLeaseReleased,
                        new SchedulerLeaseReleasedPayload(workUnitId, agentId, false),
                        causedByEventId: failedEv.EventId,
                        ct: ct).ConfigureAwait(false);
                }

                await UpdateWorkUnitStatusAsync(workUnitId, WorkUnitStatus.Retrying, sessionId, ct).ConfigureAwait(false);
                await ReinvokeOrchestratorAsync(workUnitId, sessionId, ct).ConfigureAwait(false);
            }
        }
    }

    public Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default)
    {
        IReadOnlyList<ScheduledItem> items = _queue.Values.OrderBy(i => i.AttemptCount).ToList();
        return Task.FromResult(items);
    }

    // Lazily resolved — IAgentControlService's production impl (InMemoryAgentRuntimeService)
    // depends on IWorkScheduler (this class), so a direct constructor dependency here would be
    // the same two-way cycle already avoided for IWorkUnitService below.
    private async Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId, CancellationToken ct)
    {
        var agentControl = _serviceProvider?.GetService(typeof(IAgentControlService)) as IAgentControlService;
        if (agentControl is not null)
            await agentControl.ReinvokeOrchestratorAsync(workUnitId, sessionId, ct).ConfigureAwait(false);
    }

    // Same lazy-resolution shape as DetectConflictAsync's IWorkUnitService lookup — best-effort,
    // since the work unit's lifecycle status is a secondary signal, not the scheduler's primary job.
    private async Task UpdateWorkUnitStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId, CancellationToken ct)
    {
        var workUnits = _serviceProvider?.GetService(typeof(IWorkUnitService)) as IWorkUnitService;
        if (workUnits is null)
            return;

        try
        {
            await workUnits.UpdateStatusAsync(workUnitId, status, sessionId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Illegal transition from the current status — a best-effort side effect, not worth
            // failing the scheduler operation that triggered it.
        }
        catch (KeyNotFoundException)
        {
            // Work unit not found (e.g. test fakes, or a debug-enqueued item with no real WorkUnit
            // record) — same best-effort reasoning as above.
        }
    }
}
