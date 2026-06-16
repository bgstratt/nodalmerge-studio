namespace NodalMerge.Studio.Contracts.Domain;

// Session
public sealed record SessionStartedPayload(
    string SessionId,
    IReadOnlyList<string> ProfileIds,
    string ModelConfigSnapshotJson);

public sealed record SessionPausedPayload(string SessionId);

public sealed record SessionResumedPayload(string SessionId);

public sealed record SessionBranchCreatedPayload(
    string ChildSessionId,
    string ParentSessionId,
    string ParentEventId);

// Work unit
public sealed record WorkUnitScheduledPayload(
    string WorkUnitId,
    string ProfileId,
    int AttemptCount);

public sealed record WorkUnitStartedPayload(
    string WorkUnitId,
    string AgentId);

public sealed record WorkUnitCompletedPayload(
    string WorkUnitId,
    string AgentId,
    string? ProducedProposalId);

public sealed record WorkUnitFailedPayload(
    string WorkUnitId,
    string AgentId,
    string FailureReason);

// Scheduler
public sealed record SchedulerLeaseAcquiredPayload(
    string WorkUnitId,
    string AgentId,
    DateTimeOffset ExpiresAt);

public sealed record SchedulerLeaseReleasedPayload(
    string WorkUnitId,
    string AgentId,
    bool Success);

public sealed record SchedulerLeaseExpiredPayload(
    string WorkUnitId,
    string PriorAgentId);

// Workspace
public sealed record WorkspaceCreatedPayload(
    string WorkspaceId,
    string WorkUnitId,
    string BranchName,
    string BaseBranch);

public sealed record WorkspaceBranchCreatedPayload(
    string NewWorkspaceId,
    string SourceWorkspaceId,
    string SourceEventId);

public sealed record WorkspaceArchivedPayload(
    string WorkspaceId,
    string ExecutionBranch);

public sealed record WorkspaceDestroyedPayload(
    string WorkspaceId,
    string Reason);

// Artifacts
public sealed record ArtifactProposedPayload(
    string ArtifactId,
    string WorkUnitId,
    IReadOnlyList<string> FilesTouched);

public sealed record ArtifactStatusChangedPayload(
    string ArtifactId,
    ArtifactStatus PreviousStatus,
    ArtifactStatus NewStatus);

// Proposal lifecycle
public sealed record ProposalApprovedPayload(
    string ProposalId,
    string ApprovedBy);

public sealed record ProposalRejectedPayload(
    string ProposalId,
    string RejectedBy,
    string? Reason);

// Merge
public sealed record MergeApprovedPayload(
    string ProposalId,
    string ApprovedBy,
    DateTimeOffset ApprovedAt);

public sealed record MergeAppliedPayload(
    string ProposalId,
    string TargetBranch,
    string ResultCommitHash);

// Orchestration
public enum OrchestrationAction
{
    SpawnPlanner,
    SpawnWorker,
    Enqueue,
    AwaitReview,
    ApplyMerge,
    Escalate,
    NoOp,
}

public sealed record OrchestrationDecisionPayload(
    string OrchestratorAgentId,
    OrchestrationAction Action,
    IReadOnlyList<string> SpawnedIds,
    string? Reason);

public sealed record ConflictDetectedPayload(
    string WorkUnitId,
    IReadOnlyList<string> OverlappingFiles,
    IReadOnlyList<string> ConflictingWorkUnitIds);
