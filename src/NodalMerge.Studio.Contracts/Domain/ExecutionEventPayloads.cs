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

public sealed record WorkUnitStatusChangedPayload(
    string WorkUnitId,
    WorkUnitStatus PreviousStatus,
    WorkUnitStatus NewStatus);

// Capability-gap fix — records in-place FileScope amendments so a revised assignment stays
// auditable in the event stream rather than being a silent mutation (the alternative today,
// SteeringService, forks a new work unit specifically so its decision log stays immutable; this
// event is what gives the same auditability to an in-place change instead).
public sealed record WorkUnitFileScopeChangedPayload(
    string WorkUnitId,
    IReadOnlyList<string> PreviousScope,
    IReadOnlyList<string> NewScope);

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

// Capability-gap fix — covers every descendant flagged by one InvalidateAsync call in a single
// event, rather than one event per artifact in the cascade.
public sealed record ArtifactInvalidationCascadedPayload(
    string RootArtifactId,
    string Reason,
    IReadOnlyList<string> FlaggedArtifactIds);

// Proposal lifecycle
public sealed record ProposalApprovedPayload(
    string ProposalId,
    string ApprovedBy);

public sealed record ProposalRejectedPayload(
    string ProposalId,
    string RejectedBy,
    string? Reason);

public sealed record MergeProposalStatusChangedPayload(
    string ProposalId,
    MergeProposalStatus PreviousStatus,
    MergeProposalStatus NewStatus);

// Merge
public sealed record MergeApprovedPayload(
    string ProposalId,
    string ApprovedBy,
    DateTimeOffset ApprovedAt);

public sealed record MergeAppliedPayload(
    string ProposalId,
    string TargetBranch,
    string ResultCommitHash,
    // The RepositorySnapshot current after this apply's disk write-back — the point-in-time
    // anchor WorkspacePathways Integration nodes materialize from. Null when the apply didn't
    // touch disk (promotion-branch staging, workspace-only sessions) or the CAS layer isn't
    // configured. See plans/pathways-workspace-history.md verification item 1.
    string? SnapshotId = null);

// Orchestration
public enum OrchestrationAction
{
    SpawnPlanner,
    SpawnWorker,
    Enqueue,
    AwaitReview,
    ApplyMerge,
    Escalate,
    // Slice 14a — a registered IPolicyRule rejected the checkpoint. Distinct from Escalate, which
    // is the 11e dead-letter path for a different kind of "something stopped this."
    PolicyBlocked,
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

// Goal lifecycle
public sealed record GoalPausedPayload(
    string GoalId,
    string WorkUnitId,
    string? Reason,
    string? PausedBy);

public sealed record GoalResumedPayload(
    string GoalId,
    string WorkUnitId,
    string? Steering,
    string? ResumedBy);

public sealed record ClarificationRequestedPayload(
    string RequestId,
    string WorkUnitId,
    string Question,
    string? Context,
    bool Blocking,
    IReadOnlyList<string> Options,
    string? RequestedByAgentId,
    DateTimeOffset RequestedAt,
    int? TimeoutSeconds = null,
    string? TimeoutBehavior = null,
    string? DefaultResponse = null);

public sealed record ClarificationRespondedPayload(
    string RequestId,
    string WorkUnitId,
    string Response,
    string? Note,
    string? RespondedBy,
    DateTimeOffset RespondedAt,
    bool Resumed);

// Phase 14 — workspace usage instrumentation. These feed WorkspaceUsageMetricsService's
// aggregation queries; see plans/phase-14-usage-instrumentation-and-read-many.md.
public sealed record WorkspaceSearchExecutedPayload(
    string Query,
    IReadOnlyList<string> MatchedPaths,
    int MatchCount,
    bool Truncated);

public sealed record WorkspaceReadExecutedPayload(IReadOnlyList<string> Paths);

public sealed record FileLeaseContendedPayload(
    string Path,
    string RequestingWorkUnitId,
    string HolderWorkUnitId);

public sealed record ExternalDocFetchedPayload(
    string ArtifactId,
    string WorkUnitId,
    string Url,
    string ContentHash,
    bool Truncated,
    int SnapshotBytes,
    DateTimeOffset FetchedAt);

// Slice 23 — domain-agent constraint feedback loop. ArtifactSurfaced records that a domain-agent-
// authored artifact (recognizable by its title prefix) was actually returned in a projection an
// agent read, not just written into the DAG. ArtifactConsideredInDecision records that a reviewer
// explicitly cited the artifact when deciding a merge proposal.
public sealed record ArtifactSurfacedPayload(
    string ArtifactId,
    string? SurfacedToAgentId,
    string ProjectionType);

public sealed record ArtifactConsideredInDecisionPayload(
    string ArtifactId,
    string ProposalId,
    MergeProposalStatus Decision);

// Orchestrator reliability — one of these per transient-provider-error retry attempt inside
// LlmClient.SendAsync (see AgentId's caller — OrchestratorAgentLoop/WorkerAgentLoop — for where
// this gets appended). AgentId is who was mid-call when the provider rejected the request.
public sealed record ProviderRetryAttemptedPayload(
    string AgentId,
    string? Provider,
    int? StatusCode,
    int AttemptNumber,
    int MaxAttempts,
    int DelayMs,
    string Reason);

// plans/harness-hosting-architecture.md Phase B.3 — one of these per entry in a ClaudeCodeExecutor
// run's terminal permission_denials array. RawJson carries the denial verbatim (unverified shape
// beyond "empty array" as of 2026-07-12 — see the plan's B2 research) so nothing is lost even if
// the parsed ToolName/Reason fields miss a real field name.
public sealed record HarnessPermissionDeniedPayload(
    string AgentId,
    string? ToolName,
    string? Reason,
    string RawJson);

// plans/phase-d-implementation.md D1.b — one of these per Plan-mode CLI run whose diff (against
// the harvest's target branch) was non-empty outside .workspace/ and got discarded instead of
// proposed. DiffSummary carries IFileWorkspaceService.DiffAsync's own human-readable summary
// verbatim (added/modified/deleted counts + file list) — enough to diagnose without re-running
// the harness.
public sealed record HarnessPlanDiffDiscardedPayload(
    string AgentId,
    string DiffSummary);
