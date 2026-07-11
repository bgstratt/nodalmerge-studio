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
    private readonly IStudioGraphPromoter? _graphPromoter;
    private readonly IParticipantEventBus? _eventBus;
    private readonly IServiceProvider? _serviceProvider;

    // IStudioGraphPromoter resolves RuntimeGraphPromoter → IRuntimeCommandBridge → FfiBridgeProcessor
    // → HostFfiClient → native P/Invoke. Defer until first use to avoid blocking startup.
    private IStudioGraphPromoter? GraphPromoter =>
        _graphPromoter ?? _serviceProvider?.GetService<IStudioGraphPromoter>();

    public InMemoryWorkUnitService(
        IBranchService branchService,
        IMergeService mergeService,
        IKnownGoodStateService knownGoodStateService,
        IAgentControlService agentControl,
        IStudioNodeStore nodeStore,
        IArtifactLineageService artifactLineage,
        WorkspaceOptions workspaceOptions,
        IExecutionEventStream events,
        IRuntimeEventBroadcaster? broadcaster = null,
        IStudioGraphPromoter? graphPromoter = null,
        IParticipantEventBus? eventBus = null,
        IServiceProvider? serviceProvider = null)
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
        _graphPromoter         = graphPromoter;
        _eventBus              = eventBus;
        _serviceProvider       = serviceProvider;
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

        if (status is WorkUnitStatus.Completed or WorkUnitStatus.Merged)
        {
            _ = GraphPromoter?.TryPromoteStudioCheckpointAsync();
        }

        _eventBus?.Publish(new WorkUnitStatusChangedEvent(workUnitId, previousStatus, status, DateTimeOffset.UtcNow));

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

    public async Task<WorkUnit> IncrementReviewRejectionCountAsync(
        string workUnitId,
        bool automated,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        var executionInfo = workUnit.ExecutionInfo ?? new WorkUnitExecutionInfo(0, 0);
        executionInfo = automated
            ? executionInfo with { AutomatedReviewRejectionCount = executionInfo.AutomatedReviewRejectionCount + 1 }
            : executionInfo with { HumanReviewRejectionCount = executionInfo.HumanReviewRejectionCount + 1 };

        var updated = workUnit with { ExecutionInfo = executionInfo, UpdatedAt = DateTimeOffset.UtcNow };
        _workUnits[workUnitId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnitId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async Task<WorkUnit> IncrementFailureAttemptCountAsync(
        string workUnitId,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        var executionInfo = (workUnit.ExecutionInfo ?? new WorkUnitExecutionInfo(0, 0)) with
        {
            FailureAttemptCount = (workUnit.ExecutionInfo?.FailureAttemptCount ?? 0) + 1,
        };

        var updated = workUnit with { ExecutionInfo = executionInfo, UpdatedAt = DateTimeOffset.UtcNow };
        _workUnits[workUnitId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnitId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async Task<WorkUnit> AddDependencyAsync(
        string workUnitId,
        string dependsOnWorkUnitId,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        if (workUnit.DependsOn.Contains(dependsOnWorkUnitId))
            return workUnit;

        var updated = workUnit with
        {
            DependsOn = [.. workUnit.DependsOn, dependsOnWorkUnitId],
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _workUnits[workUnitId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnitId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        return updated;
    }

    public async Task<WorkUnit> AmendGoalForSteeredRetryAsync(
        string workUnitId,
        string amendedGoal,
        string steeringContext,
        string deadLetterEntryId,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        var amendedMetadata = new Dictionary<string, string>(workUnit.Metadata ?? new Dictionary<string, string>())
        {
            ["lastSteeringContext"] = steeringContext,
            ["steeredFromDeadLetterEntryId"] = deadLetterEntryId,
        };

        var updated = workUnit with
        {
            Goal = amendedGoal,
            Metadata = amendedMetadata,
            ExecutionInfo = (workUnit.ExecutionInfo ?? new WorkUnitExecutionInfo(0, 0)) with { FailureAttemptCount = 0 },
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        _workUnits[workUnitId] = updated;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.WorkUnitV1,
            workUnitId,
            JsonSerializer.Serialize(updated),
            cancellationToken).ConfigureAwait(false);

        return updated;
    }

    private static readonly HashSet<WorkUnitStatus> TerminalStatuses = new()
    {
        WorkUnitStatus.Completed, WorkUnitStatus.Merged, WorkUnitStatus.Cancelled,
    };

    public async Task<WorkUnit> SetFileScopeAsync(
        string workUnitId,
        IReadOnlyList<string> fileScope,
        string? sessionId = null,
        CancellationToken cancellationToken = default)
    {
        var workUnit = GetRequired(workUnitId);
        if (TerminalStatuses.Contains(workUnit.Status))
        {
            throw new InvalidOperationException(
                $"Cannot amend FileScope on work unit '{workUnitId}': already {workUnit.Status}.");
        }

        var previousScope = workUnit.FileScope;
        var updated = workUnit with { FileScope = fileScope, UpdatedAt = DateTimeOffset.UtcNow };
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
                ExecutionEventKind.WorkUnitFileScopeChanged,
                new WorkUnitFileScopeChangedPayload(workUnitId, previousScope, fileScope),
                ct: cancellationToken).ConfigureAwait(false);
        }

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
        HypothesisForkType? forkType = null,
        ReviewPolicy? taskReviewPolicy = null,
        ReviewPolicy? workspaceReviewPolicy = null,
        int? taskReviewHybridTimeoutMinutes = null,
        int? workspaceReviewHybridTimeoutMinutes = null,
        bool bypassPromotionBranch = false,
        WorkUnitExpectedOutputKind expectedOutputKind = WorkUnitExpectedOutputKind.FileChange,
        string? repositoryId = null,
        IReadOnlyList<FileReferenceV1>? referenceFiles = null,
        string? workspaceId = null,
        IReadOnlyList<string>? reconciliationSourceProposalIds = null,
        IReadOnlyList<string>? reconciliationTargetPaths = null,
        string? reconciliationSourceRef = null,
        CancellationToken cancellationToken = default)
    {
        // repositoryPath (the extension's auto-detected/override nodalmerge.repositoryPath, sent on
        // every goal-creation call) used to be silently dropped here — accepted as a parameter but
        // never turned into a RepositoryId, so PromoteAsync's ResolveWriteBackPathAsync (which only
        // ever consults WorkUnit.RepositoryId) had nowhere to write back to, and a goal's changes
        // would land on the candidate/main branch inside NodalMerge's own storage but never reach
        // the real on-disk repo. RegisterAsync is idempotent by normalized path, so this is safe to
        // call on every goal creation even if the path's already registered from an earlier goal.
        var resolvedRepositoryId = repositoryId;
        if (resolvedRepositoryId is null && repositoryPath is not null)
        {
            var repositories = _serviceProvider?.GetService<IRepositoryRegistryService>();
            if (repositories is not null)
            {
                var repository = await repositories.RegisterAsync(repositoryPath, label: null, cancellationToken)
                    .ConfigureAwait(false);
                resolvedRepositoryId = repository.RepositoryId;
            }
        }

        var resolvedBranchId = await _branchService
            .CreateBranchAsync(branchId ?? $"work-{Guid.NewGuid():N}", seedFromBranchId, fileScope, cancellationToken)
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
            BranchedFromProposalId: branchedFromProposalId,
            ForkType: forkType,
            TaskReviewPolicy: taskReviewPolicy ?? ReviewPolicy.HumanRequired,
            WorkspaceReviewPolicy: workspaceReviewPolicy ?? ReviewPolicy.HumanRequired,
            TaskReviewHybridTimeoutMinutes: taskReviewHybridTimeoutMinutes,
            WorkspaceReviewHybridTimeoutMinutes: workspaceReviewHybridTimeoutMinutes,
            BypassPromotionBranch: bypassPromotionBranch,
            ExpectedOutputKind: expectedOutputKind,
            RepositoryId: resolvedRepositoryId,
            ReferenceFiles: referenceFiles,
            WorkspaceId: workspaceId ?? "workspace-default",
            ReconciliationSourceProposalIds: reconciliationSourceProposalIds,
            ReconciliationTargetPaths: reconciliationTargetPaths,
            ReconciliationSourceRef: reconciliationSourceRef);

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
            knownGoodStates,
            _workspaceOptions.RootPath,
            _workspaceOptions.SeedRepositoryPath);
    }

    public async Task<WorkspaceStatus> GetStatusAsync(
        string? branchId = null,
        string? workUnitId = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        if (limit <= 0)
            throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be greater than zero.");
        if (offset < 0)
            throw new ArgumentOutOfRangeException(nameof(offset), "Offset cannot be negative.");

        WorkUnit? currentWorkUnit = null;
        var resolvedBranchId = branchId;
        if (workUnitId is not null)
        {
            currentWorkUnit = GetRequired(workUnitId);
            resolvedBranchId ??= currentWorkUnit.BranchId;
        }

        var proposalSnapshots = new List<(DateTimeOffset SortKey, WorkspaceStatusProposalSummary Summary, IReadOnlyList<WorkspaceStatusFileChange> ChangedFiles)>();

        if (workUnitId is not null)
        {
            var chain = await _artifactLineage.GetChainAsync(workUnitId, cancellationToken).ConfigureAwait(false);
            foreach (var proposalRef in chain.Where(a => a.Type == ArtifactType.MergeProposal).OrderBy(a => a.CreatedAt))
            {
                var proposal = await _mergeService.GetAsync(proposalRef.ArtifactId, cancellationToken).ConfigureAwait(false);
                if (proposal is null)
                    continue;

                var snapshot = BuildProposalSnapshot(proposal, proposalRef.CreatedAt);
                proposalSnapshots.Add(snapshot);
            }
        }
        else
        {
            var proposals = await _mergeService.ListAsync(resolvedBranchId, cancellationToken).ConfigureAwait(false);
            foreach (var proposal in proposals.OrderBy(p => p.DiffGeneratedAt ?? DateTimeOffset.MinValue))
            {
                proposalSnapshots.Add(BuildProposalSnapshot(proposal, proposal.DiffGeneratedAt ?? DateTimeOffset.MinValue));
            }
        }

        var mergedFiles = new Dictionary<string, WorkspaceStatusFileChange>(StringComparer.OrdinalIgnoreCase);
        int addedFiles = 0;
        int modifiedFiles = 0;
        int deletedFiles = 0;
        int? addedLines = 0;
        int? removedLines = null;

        foreach (var snapshot in proposalSnapshots)
        {
            foreach (var fileChange in snapshot.ChangedFiles)
            {
                mergedFiles[fileChange.Path] = fileChange;
            }

            addedFiles += snapshot.Summary.AddedFiles;
            modifiedFiles += snapshot.Summary.ModifiedFiles;
            deletedFiles += snapshot.Summary.DeletedFiles;
            if (snapshot.Summary.AddedLines is not null)
                addedLines += snapshot.Summary.AddedLines;
            if (snapshot.Summary.RemovedLines is null)
                removedLines = null;
            else
                removedLines = (removedLines ?? 0) + snapshot.Summary.RemovedLines.Value;
        }

        var orderedFiles = mergedFiles.Values
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.ChangeKind)
            .ToList();

        var pagedFiles = orderedFiles.Skip(offset).Take(limit).ToList();
        var nextOffset = Math.Min(offset + pagedFiles.Count, orderedFiles.Count);
        var truncated = orderedFiles.Count > nextOffset;

        var orderedProposals = proposalSnapshots
            .OrderByDescending(p => p.SortKey)
            .ThenByDescending(p => p.Summary.ProposalId, StringComparer.Ordinal)
            .Select(p => p.Summary)
            .ToList();

        return new WorkspaceStatus(
            resolvedBranchId,
            workUnitId,
            currentWorkUnit?.Status,
            pagedFiles,
            orderedProposals,
            orderedFiles.Count == 0 && addedFiles == 0 && modifiedFiles == 0 && deletedFiles == 0
                ? null
                : new WorkspaceStatusDiffStats(addedFiles, modifiedFiles, deletedFiles, addedLines, removedLines),
            truncated,
            limit,
            offset,
            nextOffset,
            DateTimeOffset.UtcNow);
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

    private static (DateTimeOffset SortKey, WorkspaceStatusProposalSummary Summary, IReadOnlyList<WorkspaceStatusFileChange> ChangedFiles) BuildProposalSnapshot(MergeProposal proposal, DateTimeOffset sortKey)
    {
        var (changedFiles, addedLines, removedLines) = ParseChangedFiles(proposal.ProposalId, proposal.WorkspaceChanges, proposal.FilesTouched);
        var addedFiles = changedFiles.Count(f => f.ChangeKind == WorkspaceChangeKind.Added);
        var modifiedFiles = changedFiles.Count(f => f.ChangeKind == WorkspaceChangeKind.Modified);
        var deletedFiles = changedFiles.Count(f => f.ChangeKind == WorkspaceChangeKind.Deleted);

        return (
            sortKey,
            new WorkspaceStatusProposalSummary(
                proposal.ProposalId,
                proposal.Status,
                proposal.FilesTouched,
                addedFiles,
                modifiedFiles,
                deletedFiles,
                addedLines,
                removedLines,
                proposal.Summary,
                proposal.DiffGeneratedAt),
            changedFiles);
    }

    private static (IReadOnlyList<WorkspaceStatusFileChange> ChangedFiles, int? AddedLines, int? RemovedLines) ParseChangedFiles(
        string proposalId,
        string? workspaceChanges,
        IReadOnlyList<string> fallbackFilesTouched)
    {
        if (string.IsNullOrWhiteSpace(workspaceChanges))
        {
            var fallbackChanges = fallbackFilesTouched
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new WorkspaceStatusFileChange(path, WorkspaceChangeKind.Changed, proposalId))
                .ToList();
            return (fallbackChanges, null, null);
        }

        var changes = new List<WorkspaceStatusFileChange>();
        WorkspaceChangeKind? currentKind = null;
        int addedLines = 0;

        foreach (var rawLine in workspaceChanges.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("+++ ADDED: ", StringComparison.Ordinal))
            {
                currentKind = WorkspaceChangeKind.Added;
                changes.Add(new WorkspaceStatusFileChange(line["+++ ADDED: ".Length..], WorkspaceChangeKind.Added, proposalId));
                continue;
            }

            if (line.StartsWith("~~~ MODIFIED: ", StringComparison.Ordinal))
            {
                currentKind = WorkspaceChangeKind.Modified;
                changes.Add(new WorkspaceStatusFileChange(line["~~~ MODIFIED: ".Length..], WorkspaceChangeKind.Modified, proposalId));
                continue;
            }

            if (line.StartsWith("--- DELETED: ", StringComparison.Ordinal))
            {
                currentKind = WorkspaceChangeKind.Deleted;
                changes.Add(new WorkspaceStatusFileChange(line["--- DELETED: ".Length..], WorkspaceChangeKind.Deleted, proposalId));
                continue;
            }

            if (currentKind is WorkspaceChangeKind.Added or WorkspaceChangeKind.Modified && line.StartsWith("+ ", StringComparison.Ordinal))
                addedLines++;
        }

        if (changes.Count == 0)
        {
            changes.AddRange(fallbackFilesTouched
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(path => new WorkspaceStatusFileChange(path, WorkspaceChangeKind.Changed, proposalId)));
        }

        return (changes, addedLines, null);
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
            sp.GetService<IRuntimeEventBroadcaster>(),
            graphPromoter: null,       // deferred — IStudioGraphPromoter chains to native FFI
            eventBus: sp.GetService<IParticipantEventBus>(),
            serviceProvider: sp));
        services.AddSingleton<IWorkUnitService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IOrchestratorService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IWorkspaceService>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IRehydratable>(sp => sp.GetRequiredService<InMemoryWorkUnitService>());
        services.AddSingleton<IFanOutService, FanOutService>();
        services.AddSingleton<IWorkUnitCommandService, WorkUnitCommandService>();
        services.AddSingleton<IExperimentService, ExperimentService>();
        services.AddSingleton<ISteeringService, SteeringService>();
        return services;
    }
}
