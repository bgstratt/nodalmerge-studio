namespace NodalMerge.Studio.Contracts.Domain;

public sealed record ExecutionEvent(
    string EventId,
    string SessionId,
    string? WorkUnitId,
    ExecutionEventKind Kind,
    string PayloadJson,
    string? CausedByEventId,
    DateTimeOffset OccurredAt);

public enum ExecutionEventKind
{
    // Session lifecycle
    SessionStarted,
    SessionPaused,
    SessionResumed,
    SessionBranchCreated,

    // Work unit lifecycle
    WorkUnitCreated,
    WorkUnitScheduled,
    WorkUnitStarted,
    WorkUnitCompleted,
    WorkUnitFailed,
    WorkUnitAbandoned,
    WorkUnitStatusChanged,

    // Scheduler internals
    SchedulerLeaseAcquired,
    SchedulerLeaseReleased,
    SchedulerLeaseExpired,

    // Workspace
    WorkspaceCreated,
    WorkspaceBranchCreated,
    WorkspaceArchived,
    WorkspaceDestroyed,

    // Artifact lifecycle
    ArtifactRecorded,
    ArtifactProposed,
    ArtifactStatusChanged,

    // Proposal lifecycle
    ProposalApproved,
    ProposalRejected,
    ProposalSuperseded,
    MergeProposalStatusChanged,

    // Merge lifecycle
    MergeApproved,
    MergeApplied,

    // Orchestration
    OrchestrationDecision,
    ConflictDetected,
}
