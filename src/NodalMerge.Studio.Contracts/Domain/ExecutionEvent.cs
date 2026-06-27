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
    WorkUnitFileScopeChanged,

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
    ArtifactInvalidationCascaded,

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
    ClarificationRequested,
    ClarificationResponded,

    // Phase 14 — workspace usage instrumentation
    WorkspaceSearchExecuted,
    WorkspaceReadExecuted,
    FileLeaseContended,

    // Slice 15g — constrained external documentation fetch audit trail
    ExternalDocFetched,

    // Slice 23 — domain-agent constraint feedback loop
    ArtifactSurfaced,
    ArtifactConsideredInDecision,
}
