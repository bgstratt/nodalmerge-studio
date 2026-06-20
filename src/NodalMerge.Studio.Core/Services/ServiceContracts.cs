namespace NodalMerge.Studio.Core.Services;

// Slice 0a — implemented by every in-memory storage service that owns a node-store-backed
// dictionary. StudioStateRehydrationService calls RehydrateAsync on each registered instance
// once at startup, before the host accepts traffic, to rebuild that dictionary from whatever
// was already durably written via IStudioNodeStore.WriteNodeAsync.
public interface IRehydratable
{
    Task RehydrateAsync(CancellationToken cancellationToken = default);
}

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

    // Slice 15c — shared create-task entry point for MCP/REST/the agent-loop dispatcher.
    // The artifact-lineage record that only McpToolDispatcher previously wrote on task creation
    // now fires once, inside TaskCommandService.CreateAsync, regardless of transport.
    public sealed record TaskCreateCommand(
        string WorkUnitId,
        string Title,
        string Description,
        int Priority = 0);

    public interface ITaskCommandService
    {
        Task<StudioTask> CreateAsync(TaskCreateCommand command, CancellationToken cancellationToken = default);

        Task<StudioTask> UpdateAsync(
            string taskId,
            string? title = null,
            string? description = null,
            string? status = null,
            int? priority = null,
            CancellationToken cancellationToken = default);

        Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken cancellationToken = default);

        Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken cancellationToken = default);
    }

    public interface IMergeService
{
    Task<MergeProposal> ProposeAsync(MergeProposal proposal, CancellationToken cancellationToken = default);

    Task<MergeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default);

    Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default);

    Task<MergeProposal> ReviewAsync(string proposalId, MergeProposalStatus decision, CancellationToken cancellationToken = default);

    /// <summary>
    /// Automated pre-gate review (11d). Approved returns the proposal to ReadyForReview with
    /// verificationResults populated; Rejected terminates before human review.
    /// </summary>
    Task<MergeProposal> AutomatedReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        string verificationResults,
        string? reviewerAgentId = null,
        CancellationToken cancellationToken = default);

    Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default, bool autoApplied = false);

    Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken cancellationToken = default);

    Task<MergeProposal> SupersedeAsync(
            string proposalId,
            string supersededByProposalId,
            CancellationToken cancellationToken = default);
    }

    // Slice 15d — merge command consolidation. ProposeAsync is the heavyweight one: it runs the diff,
    // records artifact lineage, appends execution events, and best-efforts the owning work unit's
    // status transition to Proposed — all of which were previously only executed by the agent-loop
    // dispatcher path. Validate/Review/Apply are near-identical across transports, so wrapping them
    // here keeps every adapter thin.
    public interface IMergeCommandService
    {
        Task<MergeProposal> ProposeAsync(
            string sourceBranch,
            string targetBranch,
            string summary,
            string? goal = null,
            string? changeDescription = null,
            string? workUnitId = null,
            string? agentId = null,
            string? model = null,
            string? provider = null,
            string? sessionId = null,
            string? commandId = null,
            CancellationToken cancellationToken = default);

        Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default);

        Task<MergeProposal> ReviewAsync(
            string proposalId,
            string decision,
            string? verificationResults = null,
            bool automated = false,
            string? reviewerAgentId = null,
            CancellationToken cancellationToken = default);

        Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default, bool autoApplied = false);
    }

    public interface IProposalReviewService
{
    Task<IReadOnlyList<ProposalFileChange>> GetFileChangesAsync(
        string proposalId,
        CancellationToken cancellationToken = default);
}

public interface IMergeReconciliationService
{
    Task<MergeReconciliationResult> TryReconcileAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken cancellationToken = default);
}

public enum MergeReconciliationOutcome
{
    NotApplicable,
    WaitingForChildren,
    AlreadyReconciled,
    Reconciled,
    Conflict,
}

public sealed record MergeReconciliationResult(
    MergeReconciliationOutcome Outcome,
    string? ReconciledProposalId = null,
    IReadOnlyList<string>? ConstituentProposalIds = null,
    string? ConflictReportPath = null);

public interface IAutomatedReviewGateService
{
    /// <summary>
    /// When auto-review is enabled for a parent work unit, enqueues the reviewer profile
    /// against a ReadyForReview proposal that has not yet been auto-reviewed.
    /// </summary>
    Task<AutomatedReviewGateResult> TryEnqueueReviewerAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// After automated rejection, resets child work units for retry (11d rejection path).
    /// </summary>
    Task<AutomatedRejectionResult> HandleAutomatedRejectionAsync(
        string parentWorkUnitId,
        string proposalId,
        string agentId,
        string? sessionId = null,
        CancellationToken cancellationToken = default);
}

public enum AutomatedRejectionOutcome
{
    RetriedWorkers,
    EscalatedToDeadLetter,
}

public sealed record AutomatedRejectionResult(AutomatedRejectionOutcome Outcome);

public enum AutomatedReviewGateOutcome
{
    NotEnabled,
    NotApplicable,
    AlreadyEnqueued,
    Enqueued,
}

public sealed record AutomatedReviewGateResult(
    AutomatedReviewGateOutcome Outcome,
    string? ProposalId = null);

public interface IDeadLetterService
{
    Task<DeadLetterEntry> RecordFailureAsync(
        string workUnitId,
        string agentId,
        PipelineStage stage,
        string profileId,
        string reason,
        string? taskId = null,
        string? lastProjectionSnapshot = null,
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    Task<DeadLetterEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default);

    Task<DeadLetterEntry?> GetLatestForWorkUnitAsync(
        string workUnitId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<DeadLetterRetryResult> RetryAsync(string entryId, CancellationToken cancellationToken = default);
}

public enum DeadLetterRetryOutcome
{
    Retried,
    NotFound,
    MaxAttemptsReached,
    InvalidState,
}

public sealed record DeadLetterRetryResult(
    DeadLetterRetryOutcome Outcome,
    string? Message = null);

public interface IWorkUnitService
{
    Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default);

    Task<WorkUnit> UpdateStatusAsync(
        string workUnitId,
        WorkUnitStatus status,
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    // Independent of WorkUnitStatus — there's no transition table for PipelineStage, it's an
    // observational field (which stage is currently executing this work unit). Pass null to clear.
    Task<WorkUnit> SetCurrentStageAsync(
        string workUnitId,
        PipelineStage? stage,
        CancellationToken cancellationToken = default);

    // Slice 14b — observational field, independent of WorkUnitStatus, same reasoning as
    // SetCurrentStageAsync above. Pass null to clear once a previously blocked slice enqueues.
    Task<WorkUnit> SetFanOutBlockedReasonAsync(
        string workUnitId,
        string? blockedReason,
        CancellationToken cancellationToken = default);

    Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken cancellationToken = default);
}

// Slice 12c — pushes live pipeline-stage updates to connected extension clients over the
// embedded NodalMerge runtime WebSocket room, so the Artifact Explorer doesn't have to poll to
// see stage badges move. Optional collaborator (resolved via IServiceProvider.GetService, never
// constructor-required) because the room broker only exists in the Studio Host process — unit
// and integration tests that build services directly never register an implementation.
public interface IRuntimeEventBroadcaster
{
    Task BroadcastWorkUnitStageChangedAsync(
        string workUnitId,
        PipelineStage? stage,
        CancellationToken cancellationToken = default);
}

public interface IOrchestratorService
{
    Task<WorkUnit> CreateWorkUnitAsync(
        string goal,
        string owner,
        // Slice 15b — caller-chosen branch name (e.g. "feature/payment-validation" per the MCP
        // contract doc's example). Null keeps today's behavior: a fresh "work-{guid}" branch.
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
        ReviewPolicy? reviewPolicy = null,
        bool bypassPromotionBranch = false,
        CancellationToken cancellationToken = default);

    Task AssignWorkAsync(string workUnitId, string agentId, CancellationToken cancellationToken = default);
}

// Slice 15b — shared create-work-unit entry point for MCP/REST/the agent-loop dispatcher, so the
// three transports can't drift on which optional params (branchId/parentWorkUnitId/dependsOn/
// fileScope) they happen to support. See phase-6.5-command-surface-hardening.md.
public sealed record WorkUnitCreateCommand(
    string Goal,
    string Owner,
    string? BranchId = null,
    string? SuccessCriteria = null,
    string? RepositoryPath = null,
    string? ParentWorkUnitId = null,
    IReadOnlyList<string>? DependsOn = null,
    IReadOnlyList<string>? FileScope = null,
    HypothesisForkType? ForkType = null,
    ReviewPolicy? ReviewPolicy = null,
    bool BypassPromotionBranch = false,
    string? SeedFromBranchId = null);

public interface IWorkUnitCommandService
{
    Task<WorkUnit> CreateAsync(WorkUnitCreateCommand command, CancellationToken cancellationToken = default);
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

    // Non-mutating lookup by id — unlike CheckoutKnownGoodAsync, does not restore the branch.
    Task<KnownGoodState?> GetAsync(string stateId, CancellationToken cancellationToken = default);
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

public sealed record AgentInfo(string AgentId, string WorkUnitId, string Status, string? CurrentActivity = null);

public sealed record OrchestratorCredentials(
    string Provider,
    string Model,
    string BaseUrl,
    string ApiKey,
    string? ProfileId);

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
        string? autoReviewProfileId = null,
        CancellationToken cancellationToken = default);

    // Re-enters the orchestrator loop for a work unit whose orchestrator was previously
    // SpawnAsync'd — a no-op if none was registered (e.g. a work unit whose worker was
    // enqueued directly via the scheduler debug endpoint, with no orchestrator behind it).
    Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns LLM credentials captured when an orchestrator was first spawned for a work unit.
    /// Used by fan-out to enqueue child workers with the same credentials.
    /// </summary>
    OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId);

    /// <summary>
    /// Profile ID for the automated reviewer pre-gate, captured at orchestrator spawn time.
    /// </summary>
    string? GetAutoReviewProfileId(string workUnitId);

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

    Task<ArtifactRef> ReparentAsync(string artifactId, string newParentArtifactId, CancellationToken ct = default);
}

public interface IFanOutService
{
    /// <summary>
    /// Reads plan.json on the parent branch, records a Plan artifact, creates child work units,
    /// and enqueues slices whose dependencies are satisfied.
    /// </summary>
    Task<FanOutResult> TryFanOutFromPlanAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken ct = default);

    /// <summary>
    /// After a child completes, enqueues any dependent siblings that are now unblocked.
    /// </summary>
    Task<FanOutResult> TryEnqueueReadyDependentsAsync(
        string parentWorkUnitId,
        string? sessionId = null,
        CancellationToken ct = default);
}

public enum FanOutAction
{
    None,
    PlanRecorded,
    ChildrenCreated,
    ChildEnqueued,
}

public sealed record FanOutResult(
    IReadOnlyList<FanOutAction> Actions,
    IReadOnlyList<string> EnqueuedWorkUnitIds);

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
    ConflictWarning? Conflict = null,
    // Phase 8c — set on rehydrate for any item that held a lease when the Host died (i.e. a
    // worker was actively executing it, not just sitting queued). TryAcquireAsync skips these
    // until a human explicitly approves via ApproveResumeAsync/ApproveResumeAllAsync, mirroring
    // the orchestrator-level Interrupted+manual-Resume pattern instead of silently auto-resuming.
    bool AwaitingResume = false);

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

    // Phase 8c — items flagged AwaitingResume on rehydrate (see ScheduledItem.AwaitingResume).
    Task<IReadOnlyList<ScheduledItem>> ListAwaitingResumeAsync(CancellationToken ct = default);

    Task ApproveResumeAsync(string workUnitId, CancellationToken ct = default);

    Task<int> ApproveResumeAllAsync(CancellationToken ct = default);
}

// Slice 15f — shared enqueue entry point for MCP/REST/the agent-loop dispatcher, so the
// three transports can't drift on which params (model/baseUrl/apiKey/provider) they support.
public interface ISchedulerCommandService
{
    Task<ScheduledItem> EnqueueAsync(
        string workUnitId,
        string profileId,
        string? taskId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        string? sessionId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default);
}

// Slice 15f — shared artifact command entry point for MCP/REST/the agent-loop dispatcher.
// Moves the CollectChainWithAncestorsAsync walk (copy-pasted verbatim between ArtifactTools.cs
// and McpToolDispatcher.cs) into a single implementation.
public interface IArtifactCommandService
{
    Task<ArtifactRef> RecordAsync(
        string workUnitId,
        string type,
        string title,
        string body,
        string? parentArtifactId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ArtifactRef>> QueryAsync(
        string workUnitId,
        string? type = null,
        string? keywords = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ArtifactRef>> ListAsync(
        string workUnitId,
        bool includeAncestors = true,
        CancellationToken ct = default);
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

// Slice 12d — LLM-driven profile selection. FanOutService (Orchestrator project) needs this to
// pick a profile for each child work unit it enqueues; the LLM-calling implementation lives in
// AgentRuntime (where LlmClient lives), so this interface is the seam that lets Orchestrator
// depend on the capability without depending on AgentRuntime directly.
public sealed record ProfileSelectionResult(string ProfileId, string Reason, bool UsedLlm);

public interface IProfileSelectionService
{
    /// <summary>
    /// Picks the agent profile for a child work unit. Returns the heuristic default ("worker")
    /// immediately when LLM selection is disabled, credentials are unavailable, or the LLM call
    /// fails/times out/returns an unknown profile id.
    /// </summary>
    Task<ProfileSelectionResult> SelectProfileAsync(
        WorkUnit childUnit,
        OrchestratorCredentials? credentials,
        CancellationToken ct = default);
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

// Slice 14a — pluggable validation seam checked at defined pipeline checkpoints. Ships with zero
// registered IPolicyRule implementations; PolicyGateService aggregates whatever rules DI resolves
// for the requested checkpoint, so adding a new rule later is "register one more class," not
// "edit the gate." context is a loose bag rather than a typed-per-checkpoint payload because
// different checkpoints have genuinely different available data.
public enum PolicyCheckpoint
{
    BeforeEnqueue,
    ProposalCreated,
    BeforeMerge,
}

public sealed record PolicyViolation(string RuleId, string Message);

public sealed record PolicyResult(bool Allowed, IReadOnlyList<PolicyViolation> Violations);

public interface IPolicyRule
{
    string RuleId { get; }

    PolicyCheckpoint Checkpoint { get; }

    Task<PolicyResult> EvaluateAsync(IReadOnlyDictionary<string, object?> context, CancellationToken ct = default);
}

public interface IPolicyGateService
{
    Task<PolicyResult> EvaluateAsync(PolicyCheckpoint checkpoint, IReadOnlyDictionary<string, object?> context, CancellationToken ct = default);

    Task<IReadOnlyList<string>> ListRuleIdsAsync(CancellationToken ct = default);
}

// Slice 20b — runs a ReviewerAgentLoop inline (synchronously awaited) for AgentApproval/Hybrid
// policies. Defined here so AutoReviewRule in Merge can depend on it without referencing AgentRuntime
// (which references Merge — adding the reverse reference would be circular).
public sealed record InlineReviewResult(bool Approved, string? Notes);

public interface IInlineReviewerService
{
    Task<InlineReviewResult> ReviewAsync(
        string workUnitId,
        string proposalId,
        CancellationToken ct = default);
}

// Slice 20c — Hybrid review policy: agent approves, timer starts; auto-merges at expiry or human
// overrides first.
public sealed record ReviewTimer(
    string TimerId,
    string ProposalId,
    string WorkUnitId,
    DateTimeOffset ExpiresAt,
    bool Cancelled = false);

public interface IReviewTimerService
{
    Task ScheduleAsync(string proposalId, string workUnitId, TimeSpan delay, CancellationToken ct = default);
    Task TryCancelAsync(string proposalId, CancellationToken ct = default);
    Task ProcessExpiredAsync(CancellationToken ct = default);
    Task<ReviewTimer?> GetAsync(string proposalId, CancellationToken ct = default);
}

// Slice 14b — shape of the "activeSiblings" key FanOutService populates in BeforeEnqueue's
// context bag. Built by FanOutService (it already depends on IWorkUnitService) so IPolicyRule
// implementations living in Storage don't need their own IWorkUnitService dependency — that
// would risk the same circular constructor graph IWorkScheduler's lazy IWorkUnitService
// resolution (see WorkSchedulerService) already exists to avoid.
public sealed record FileScopeSibling(
    string WorkUnitId,
    string? SliceId,
    WorkUnitStatus Status,
    IReadOnlyList<string> FileScope);

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
    Task CopyFilesAsync(
        string sourceBranchId,
        string targetBranchId,
        IReadOnlyList<string> relativePaths,
        CancellationToken ct = default);
    Task<string?> GetWorkingDirectoryAsync(string branchId, CancellationToken ct = default);
}

public sealed record WorkspaceSummary(
    IReadOnlyList<string> ActiveWorkUnits,
    IReadOnlyList<string> ActiveAgents,
    IReadOnlyList<string> PendingMerges,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> KnownGoodStates);

// Slice 16b — executes build, test, and lint commands inside a branch's working directory.
// Language-agnostic — runs whatever command string it receives via Process.Start.
public interface IWorkspaceExecutionService
{
    /// <summary>
    /// Execute build/test/lint on a single branch.
    /// </summary>
    Task<BranchExecutionResult> ExecuteAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default);

    /// <summary>
    /// Merge files from multiple source branches into a temporary composite branch,
    /// then execute build/test/lint on the composite. Cleans up the temp branch afterward.
    /// </summary>
    Task<BranchExecutionResult> ExecuteCompositeAsync(
        IReadOnlyList<string> sourceBranchIds,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default);
}

// Slice 16c — shared entry point for workspace execution commands — called by both MCP tools
// (WorkspaceTools) and REST endpoints (StudioRestEndpoints) so they cannot drift.
public interface IWorkspaceExecutionCommandService
{
    Task<BranchExecutionResult> BuildAsync(
        string branchId,
        string? buildCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default);

    Task<BranchExecutionResult> TestAsync(
        string branchId,
        string? testCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default);

    Task<BranchExecutionResult> ExecAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default);

    Task<BranchExecutionResult?> GetLatestAsync(
        string branchId,
        CancellationToken ct = default);

    Task<BuildResult> RunAsync(
        string branchId,
        string? runCommand = null,
        int timeoutSeconds = 120,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default);

    Task<string?> GetBranchPathAsync(
        string branchId,
        CancellationToken ct = default);

    /// <summary>
    /// Returns the cached (truncated) stdout+stderr for a previously persisted execution result.
    /// The resultId is the ExecutionResultV1 node entity ID (e.g. "exec/{branchId}/20260618120000").
    /// </summary>
    Task<ExecutionOutput?> GetOutputAsync(
        string branchId,
        string resultId,
        CancellationToken ct = default);
}

// ── Slice 22a — Experiment runner ─────────────────────────────────────────

public sealed record ExperimentForkSpec(
    string? ProfileId,
    string? ConstraintText = null);

public sealed record ExperimentSpec(
    string Goal,
    string Owner,
    HypothesisForkType ForkType,
    IReadOnlyList<ExperimentForkSpec> Forks,
    string? ComparisonMetricHint = null,
    ReviewPolicy? ReviewPolicy = null,
    string? SessionId = null);

public sealed record ExperimentResult(
    string ExperimentId,
    string ParentWorkUnitId,
    IReadOnlyList<string> ForkWorkUnitIds);

public sealed record ExperimentNode(
    string ExperimentId,
    string ParentWorkUnitId,
    HypothesisForkType ForkType,
    IReadOnlyList<string> ForkWorkUnitIds,
    string? ComparisonMetricHint,
    DateTimeOffset CreatedAt,
    string? SessionId = null);

public interface IExperimentService
{
    Task<ExperimentResult> CreateAsync(ExperimentSpec spec, CancellationToken ct = default);
    Task<ExperimentNode?> GetAsync(string experimentId, CancellationToken ct = default);
    Task<IReadOnlyList<ExperimentNode>> ListAsync(CancellationToken ct = default);
}
