namespace NodalMerge.Studio.Core.Services;

public interface IProjectionManager
{
    Task<ProjectionResult> GetAsync(ProjectionRequest request, CancellationToken cancellationToken = default);

    Task<ProjectionResult> CompactAsync(
        ProjectionType type,
        ProjectionLevel targetLevel,
        CancellationToken cancellationToken = default);
}

public interface ITaskService
{
    Task<StudioTask> CreateAsync(StudioTask task, CancellationToken cancellationToken = default);

    Task<StudioTask?> GetAsync(string taskId, CancellationToken cancellationToken = default);

    Task<StudioTask> UpdateAsync(StudioTask task, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken cancellationToken = default);

    Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken cancellationToken = default);
}

public interface IMergeService
{
    Task<MergeProposal> ProposeAsync(MergeProposal proposal, CancellationToken cancellationToken = default);

    Task<MergeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default);

    Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default);

    Task<MergeProposal> ReviewAsync(string proposalId, MergeProposalStatus decision, CancellationToken cancellationToken = default);

    Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken cancellationToken = default);
}

public interface IWorkUnitService
{
    Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default);

    Task<WorkUnit> UpdateStatusAsync(
        string workUnitId,
        WorkUnitStatus status,
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken cancellationToken = default);
}

public interface IOrchestratorService
{
    Task<WorkUnit> CreateWorkUnitAsync(
        string goal,
        string owner,
        string? successCriteria = null,
        string? repositoryPath = null,
        string? parentWorkUnitId = null,
        IReadOnlyList<string>? dependsOn = null,
        IReadOnlyList<string>? fileScope = null,
        string? seedFromBranchId = null,
        string? branchedFromProposalId = null,
        CancellationToken cancellationToken = default);

    Task AssignWorkAsync(string workUnitId, string agentId, CancellationToken cancellationToken = default);
}

public interface IAgentRuntimeService
{
    Task<ExecutionSnapshot> GetSnapshotAsync(string agentId, string workUnitId, CancellationToken cancellationToken = default);

    Task RecordActionAsync(
        string agentId,
        string workUnitId,
        string action,
        CancellationToken cancellationToken = default);
}

public interface IKnownGoodStateService
{
    Task<KnownGoodState> MarkKnownGoodAsync(KnownGoodState state, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<KnownGoodState>> FindKnownGoodAsync(
        string branchId,
        CancellationToken cancellationToken = default);

    Task<KnownGoodState?> CheckoutKnownGoodAsync(string stateId, CancellationToken cancellationToken = default);
}

public interface IBranchService
{
    Task<string> CreateBranchAsync(string name, string? fromBranchId = null, CancellationToken cancellationToken = default);

    Task CheckoutBranchAsync(string branchId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken = default);

    Task<BranchStatus> GetStatusAsync(string branchId, CancellationToken cancellationToken = default);
}

public sealed record BranchStatus(
    string BranchId,
    string Status,
    int PendingChangeCount,
    string? HeadCheckpoint = null);

public interface IReplayService
{
    Task<string> RangeAsync(string branchId, string? fromNode = null, string? toNode = null, CancellationToken cancellationToken = default);

    Task<string> RollbackAsync(string branchId, string knownGoodStateId, CancellationToken cancellationToken = default);

    Task<string> InspectAsync(string branchId, string? nodeId = null, CancellationToken cancellationToken = default);
}

public interface ISnapshotService
{
    Task<ExecutionSnapshot> GetAsync(string agentId, string workUnitId, CancellationToken cancellationToken = default);

    Task<string> CompareAsync(string agentId, string workUnitId, string otherAgentId, CancellationToken cancellationToken = default);
}

public sealed record AgentInfo(string AgentId, string WorkUnitId, string Status);

public interface IAgentControlService
{
    Task<string> SpawnAsync(
        string agentType,
        string workUnitId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? profileId = null,
        CancellationToken cancellationToken = default);

    // Re-enters the orchestrator loop for a work unit whose orchestrator was previously
    // SpawnAsync'd — a no-op if none was registered (e.g. a work unit whose worker was
    // enqueued directly via the scheduler debug endpoint, with no orchestrator behind it).
    Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, CancellationToken cancellationToken = default);

    Task PauseAsync(string agentId, CancellationToken cancellationToken = default);

    Task ResumeAsync(string agentId, CancellationToken cancellationToken = default);

    Task StopAsync(string agentId, CancellationToken cancellationToken = default);

    Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default);
}

public interface IArtifactLineageService
{
    Task<ArtifactRef> RecordAsync(ArtifactRef artifact, CancellationToken ct = default);

    Task<ArtifactRef?> GetAsync(string artifactId, CancellationToken ct = default);

    Task<IReadOnlyList<ArtifactRef>> GetChainAsync(string workUnitId, CancellationToken ct = default);

    Task<IReadOnlyList<ArtifactRef>> GetChildrenAsync(string parentArtifactId, CancellationToken ct = default);

    Task<ArtifactRef> UpdateStatusAsync(string artifactId, ArtifactStatus status, CancellationToken ct = default);
}

public sealed record ConflictWarning(
    IReadOnlyList<string> OverlappingFiles,
    IReadOnlyList<string> ConflictingWorkUnitIds);

public sealed record ScheduledItem(
    string WorkUnitId,
    string ProfileId,
    string? TaskId,
    string? LeasedBy,
    DateTimeOffset? LeasedAt,
    int AttemptCount,
    string? Model,
    string? BaseUrl,
    string? ApiKey,
    string? Provider,
    string? SessionId = null,
    ConflictWarning? Conflict = null);

public interface IWorkScheduler
{
    Task EnqueueAsync(
        string workUnitId,
        string profileId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? sessionId = null,
        CancellationToken ct = default);

    Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default);

    Task ReleaseAsync(string workUnitId, bool success, CancellationToken ct = default);

    Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default);
}

public interface IExecutionSessionService
{
    Task<ExecutionSession> CreateAsync(
        string rootWorkUnitId,
        string modelConfigJson,
        IReadOnlyList<string> profileIds,
        string? parentSessionId = null,
        string? parentEventId = null,
        CancellationToken ct = default);

    Task<ExecutionSession?> GetAsync(string sessionId, CancellationToken ct = default);

    Task<IReadOnlyList<ExecutionSession>> ListAsync(CancellationToken ct = default);

    Task SetStatusAsync(string sessionId, ExecutionSessionStatus status, CancellationToken ct = default);
}

public interface IAgentProfileService
{
    Task<AgentProfile> CreateAsync(AgentProfile profile, CancellationToken cancellationToken = default);

    Task<AgentProfile?> GetAsync(string profileId, CancellationToken cancellationToken = default);

    Task<AgentProfile> UpdateAsync(AgentProfile profile, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AgentProfile>> ListAsync(CancellationToken cancellationToken = default);
}

public interface IExecutionEventStream
{
    Task<ExecutionEvent> AppendAsync<T>(
        string sessionId,
        string? workUnitId,
        ExecutionEventKind kind,
        T payload,
        string? causedByEventId = null,
        string? eventId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ExecutionEvent>> GetSessionEventsAsync(
        string sessionId,
        DateTimeOffset? since = null,
        CancellationToken ct = default);

    Task<ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default);
}

public interface IWorkspaceService
{
    Task<WorkspaceSummary> GetSummaryAsync(string? branchId = null, CancellationToken cancellationToken = default);
}

public interface IAgentWorkspaceService
{
    Task<AgentWorkspace> CreateAsync(
        string workUnitId, string baseBranch, string? sessionId = null, CancellationToken ct = default);

    Task<AgentWorkspace?> GetAsync(string workspaceId, CancellationToken ct = default);

    Task ArchiveAsync(string workspaceId, string? sessionId = null, CancellationToken ct = default);

    Task DestroyAsync(string workspaceId, string? reason = null, string? sessionId = null, CancellationToken ct = default);

    Task<bool> ValidateWriteAsync(
        string workUnitId, string path, IReadOnlyList<string> fileScope, CancellationToken ct = default);
}

public interface IOrchestrationDecisionLogService
{
    Task<OrchestrationEvent> RecordAsync(
        string workUnitId,
        string orchestratorAgentId,
        PipelineStage inputStage,
        string inputProjectionSnapshot,
        OrchestrationAction action,
        IReadOnlyList<string> spawnedIds,
        string? reason,
        string? sessionId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<OrchestrationEvent>> GetEventsAsync(string workUnitId, CancellationToken ct = default);
}

public interface IIntentGraphService
{
    Task RecordIntentAsync(ChangeIntent intent, CancellationToken ct = default);

    Task<IReadOnlyList<ChangeIntent>> QueryIntentsAsync(string workUnitId, CancellationToken ct = default);

    Task<IReadOnlyList<ChangeIntent>> QueryOverlappingAsync(ChangeIntent intent, CancellationToken ct = default);

    Task RemoveIntentAsync(string intentId, CancellationToken ct = default);
}

public interface IStateReconstructionService
{
    Task<SessionStateSnapshot> GetStateAtAsync(
        string sessionId, string upToEventId, CancellationToken ct = default);

    Task<SessionStateSnapshot> GetStateAtTimeAsync(
        string sessionId, DateTimeOffset asOf, CancellationToken ct = default);
}

public sealed record SessionStateSnapshot(
    string SessionId,
    string BoundaryEventId,
    DateTimeOffset BoundaryTime,
    IReadOnlyList<string> ActiveWorkUnitIds,
    IReadOnlyList<string> ActiveWorkspaceIds,
    IReadOnlyList<string> ArtifactIds,
    IReadOnlyList<string> CompletedEventIds);

public interface IFileWorkspaceService
{
    Task InitBranchAsync(string branchId, string? seedFromBranchId = null, CancellationToken ct = default);
    Task<string?> ReadAsync(string branchId, string relativePath, CancellationToken ct = default);
    Task WriteAsync(string branchId, string relativePath, string content, CancellationToken ct = default);
    Task DeleteAsync(string branchId, string relativePath, CancellationToken ct = default);
    Task<bool> ExistsAsync(string branchId, string relativePath, CancellationToken ct = default);
    Task<IReadOnlyList<string>> ListAsync(string branchId, string? subPath = null, CancellationToken ct = default);
    Task<string> DiffAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default);
    Task ApplyBranchAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default);
    Task<string?> GetWorkingDirectoryAsync(string branchId, CancellationToken ct = default);
}

public sealed record WorkspaceSummary(
    IReadOnlyList<string> ActiveWorkUnits,
    IReadOnlyList<string> ActiveAgents,
    IReadOnlyList<string> PendingMerges,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> KnownGoodStates);
