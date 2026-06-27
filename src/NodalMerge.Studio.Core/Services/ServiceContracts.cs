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

    // Slice — LLM scan context. Assembles the bounded text sent to the model: the current
    // RunRetrospective stats plus a capped sample of rejection/steering/review-note free text.
    // Lives here (not in Host) because every data source it needs is already a ProjectionManager
    // dependency.
    Task<string> BuildInsightScanContextAsync(CancellationToken cancellationToken = default);
}

// Capability-gap fix — persists ProjectionManager's otherwise-ephemeral AgentWorkspace resolution
// as an immutable, versioned snapshot keyed off WorkUnitId (no parallel ProjectionId/WorkspaceId
// identity scheme). Staleness is read directly from the artifact-invalidation cascade
// (ArtifactLineageService.InvalidateAsync) rather than tracked separately; comparison is a
// symmetric diff for siblings (different work units), distinct from ProjectionDelta's temporal
// same-work-unit diff.
public interface IProjectionSnapshotService
{
    Task<ProjectionSnapshot> CaptureAsync(string workUnitId, CancellationToken ct = default);

    Task<ProjectionSnapshot?> GetAsync(string snapshotId, CancellationToken ct = default);

    Task<IReadOnlyList<ProjectionSnapshot>> ListAsync(string? workUnitId = null, CancellationToken ct = default);

    Task<ProjectionStaleness> CheckStaleAsync(string snapshotId, CancellationToken ct = default);

    Task<ProjectionComparison> CompareAsync(string snapshotIdA, string snapshotIdB, CancellationToken ct = default);

    // Slice 20 — runs a build/test/lint execution against the work unit's branch and immediately
    // captures a snapshot of the result, so callers get one atomic "run it and freeze the result"
    // operation instead of two independent calls that could race against a concurrent execution.
    Task<ProjectionMaterializationResult> MaterializeAsync(
        string workUnitId, WorkspaceExecutionRequest? request = null, CancellationToken ct = default);
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

    Task<MergeProposal> ReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        string? notes = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Automated pre-gate review (11d). Approved returns the proposal to ReadyForReview with
    /// verificationResults populated; Rejected terminates before human review.
    /// </summary>
    Task<MergeProposal> AutomatedReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        string verificationResults,
        string? reviewerAgentId = null,
        // Slice 23 — Constraint/Research artifact IDs the reviewer explicitly says it considered.
        IReadOnlyList<string>? consideredArtifactIds = null,
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
            string? noFileChangesJustification = null,
            CancellationToken cancellationToken = default);

        Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default);

        Task<MergeProposal> ReviewAsync(
            string proposalId,
            string decision,
            string? verificationResults = null,
            bool automated = false,
            string? reviewerAgentId = null,
            string? notes = null,
            // Slice 23 — Constraint/Research artifact IDs the automated reviewer explicitly says
            // it considered. Only meaningful when automated=true.
            IReadOnlyList<string>? consideredArtifactIds = null,
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

// Slice 21/22 — domain/intelligence-plane agents. Unlike the structural agents above
// (Orchestrator/Worker/Reviewer), domain agents (Security, Architecture, ...) own no lifecycle
// and are never assigned a task: each reacts to a Research/Decision/Constraint artifact being
// recorded and, if it judges the artifact relevant to its own definition, proposes a Constraint
// of its own back into the same lineage. Disabled by default per-agent (see
// WorkspaceOptions.EnabledDomainAgents / IAgentControlService.GetEnabledDomainAgents) since each
// is an opt-in reactive LLM call, not a free side effect of recording an artifact.
public interface IDomainAgentTriggerService
{
    /// <summary>
    /// Fire-and-forget reactive entry point called after an artifact is recorded. No-ops for any
    /// domain agent that isn't enabled for this work unit, isn't relevant to the artifact, or
    /// would be reacting to another domain agent's own prior output. Never throws — a failing
    /// domain agent must never affect the artifact-record call path that triggered it.
    /// </summary>
    Task NotifyArtifactRecordedAsync(ArtifactRef artifact, CancellationToken ct = default);
}

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

    /// <summary>
    /// After a human rejects a proposal from the review panel (optionally with steering notes),
    /// resets the rejected work unit (or its children, for a reconciled fan-out proposal) for
    /// retry — same retry/dead-letter budget shape as HandleAutomatedRejectionAsync, tracked
    /// under its own counter so human and automated rejection cycles don't share a budget.
    /// </summary>
    Task<AutomatedRejectionResult> HandleHumanRejectionAsync(
        string proposalId,
        string? reviewNotes,
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
        // Whatever credentials the failed run actually used — captured on the entry so retry can
        // use them directly instead of depending on the live (and ephemeral) orchestrator registry.
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        CancellationToken cancellationToken = default);

    Task<DeadLetterEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default);

    Task<DeadLetterEntry?> GetLatestForWorkUnitAsync(
        string workUnitId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken cancellationToken = default);

    Task<DeadLetterRetryResult> RetryAsync(string entryId, CancellationToken cancellationToken = default);

    // When credential overrides are supplied, ResolveRetryCredentials prefers them over the
    // dead-letter entry's captured credentials — this lets a human retry with a different
    // model/profile (e.g. switching from vscode-lm to deepseek) without spawning a new work unit.
    Task<DeadLetterRetryResult> RetryWithCredentialOverrideAsync(
        string entryId,
        string? overrideModel,
        string? overrideBaseUrl,
        string? overrideApiKey,
        string? overrideProvider,
        string? overrideProfileId,
        CancellationToken cancellationToken = default);

    // Human-steered retry: appends corrective context to the work unit's Goal so the agent
    // actually sees it (projections only ever surface Goal/SuccessCriteria, not Metadata), and
    // resets FailureAttemptCount since the correction addresses a different root cause than the
    // one that produced the prior failures. Bypasses the MaxFailureAttempts cap for the same
    // reason — that cap exists to stop retrying the *same* mistake, not a corrected one.
    Task<DeadLetterRetryResult> RetryWithContextAsync(
        string entryId,
        string steeringContext,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        CancellationToken cancellationToken = default);
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

    // Capability-gap fix — amends a work unit's FileScope in place. Unlike SteeringService (which
    // always forks a sibling so the original's decision log stays immutable), this mutates the
    // original directly for the common case where an agent's findings warrant a narrower/wider
    // scope and a full fork would be overkill. Throws InvalidOperationException for a work unit
    // already in a terminal state (Completed/Merged/Cancelled) — amending finished work is
    // meaningless. Appends WorkUnitFileScopeChanged when sessionId is given, so the change stays
    // auditable in the event stream rather than being a silent mutation.
    Task<WorkUnit> SetFileScopeAsync(
        string workUnitId,
        IReadOnlyList<string> fileScope,
        string? sessionId = null,
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

    // Slice 18 — lets live UI clients react to an invalidation cascade (e.g. flip a projection
    // snapshot's staleness badge) instead of polling. Fired unconditionally, not gated on a
    // session existing — a watching UI client has no session of its own.
    Task BroadcastArtifactInvalidatedAsync(
        string? workUnitId,
        string artifactId,
        IReadOnlyList<string> flaggedArtifactIds,
        string reason,
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
        WorkUnitExpectedOutputKind expectedOutputKind = WorkUnitExpectedOutputKind.FileChange,
        string? repositoryId = null,
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
    string? SeedFromBranchId = null,
    WorkUnitExpectedOutputKind? ExpectedOutputKind = null,
    // Slice 19 — references an already-registered IRepositoryRegistryService entry by id.
    // Resolved to a path by WorkUnitCommandService.CreateAsync, which takes priority over
    // RepositoryPath when both are given.
    string? RepositoryId = null);

public interface IWorkUnitCommandService
{
    Task<WorkUnit> CreateAsync(WorkUnitCreateCommand command, CancellationToken cancellationToken = default);

    // Stop controls — cancels one goal's whole subtree (the work unit plus every descendant
    // spawned via fan-out), stopping their agents and pending review timers. Work units already
    // Completed/Merged are left untouched (WorkUnitTransitions forbids cancelling out of those
    // states), so already-committed work survives a cancel.
    Task<IReadOnlyList<WorkUnit>> CancelAsync(string workUnitId, CancellationToken cancellationToken = default);

    // Stop-all — runs CancelAsync against every non-terminal root work unit (no parent), across
    // every session, for a single "stop everything" control.
    Task<IReadOnlyList<WorkUnit>> CancelAllActiveAsync(CancellationToken cancellationToken = default);
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
        IReadOnlyDictionary<PipelineStage, OrchestratorCredentials>? stageCredentials = null,
        IReadOnlyList<string>? enabledDomainAgents = null,
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
    /// Per-stage credential override captured at orchestrator spawn time (e.g. a different model
    /// for Plan vs Execute vs Review), or null if no override was configured for that stage —
    /// callers fall back to <see cref="GetOrchestratorCredentials"/> in that case.
    /// </summary>
    OrchestratorCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage);

    /// <summary>
    /// Profile ID for the automated reviewer pre-gate, captured at orchestrator spawn time.
    /// </summary>
    string? GetAutoReviewProfileId(string workUnitId);

    /// <summary>
    /// Per-work-unit override of which domain agents (by name, e.g. "Security"/"Architecture")
    /// may react to artifacts recorded under this work unit, captured at orchestrator spawn time.
    /// Null means "no override" — callers fall back to the global
    /// <c>WorkspaceOptions.EnabledDomainAgents</c> default in that case.
    /// </summary>
    IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId);

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

    // Slice — Knowledge Promotion. Constraint artifacts with no owning work unit are durable,
    // workspace-wide guidance (promoted Findings), distinct from the per-work-unit Constraint
    // artifacts an agent records via nm_v1_artifact_record. GetChainAsync intentionally excludes
    // these since it only indexes work-unit-owned artifacts.
    Task<IReadOnlyList<ArtifactRef>> GetGlobalConstraintsAsync(CancellationToken ct = default);

    // Capability-gap fix — marks a Research/Decision/Constraint artifact Invalidated and flags
    // every artifact in its descendant subtree (via ParentArtifactId/GetChildrenAsync) with
    // InvalidatedByArtifactId, without touching the descendants' own Status. Throws
    // ArgumentException if the target's Type isn't Research/Decision/Constraint, KeyNotFoundException
    // if the artifact doesn't exist.
    Task<ArtifactRef> InvalidateAsync(
        string artifactId, string reason, string? sessionId = null, CancellationToken ct = default);
}

// Slice — Knowledge Promotion. Review lifecycle for Findings detected by either the deterministic
// or LLM scan (Insights tab). Modeled on IMergeService's Propose/Review shape, simplified since
// there's no build/apply step — promotion's durable effect happens inside ReviewAsync itself.
public interface IFindingService
{
    Task<Finding> ProposeAsync(Finding finding, CancellationToken ct = default);

    Task<Finding?> GetAsync(string findingId, CancellationToken ct = default);

    Task<IReadOnlyList<Finding>> ListAsync(CancellationToken ct = default);

    /// <summary>decision must be Promoted, Dismissed, or Investigating — Open is the initial state
    /// only, never a review outcome.</summary>
    Task<Finding> ReviewAsync(
        string findingId, FindingStatus decision, string? notes = null, CancellationToken ct = default);

    /// <summary>Promoted PromptImprovement findings targeting this pipeline stage — read directly
    /// by that stage's agent loop(s) when building their outgoing prompt context, the same way
    /// promoted KnowledgeGuideline findings reach every loop via InheritedConstraints, just
    /// stage-scoped instead of universal.</summary>
    Task<IReadOnlyList<Finding>> ListPromotedPromptGuidanceAsync(PipelineStage stage, CancellationToken ct = default);
}

// Slice — LLM scan. A second, independent Finding detector (alongside FindingDetectorService's
// deterministic rules) that calls a real model with the user's own credentials. Lives behind this
// interface because the concrete LLM-calling machinery (LlmClient) is internal to AgentRuntime —
// same split as every other cross-project service here (public contract in Core, implementation in
// the owning project).
public sealed record InsightLlmScanRequest(string Provider, string Model, string BaseUrl, string ApiKey, string ContextText);

// TargetStage is meaningful only when Kind is PromptImprovement — the analyzer parses it
// defensively (bad/missing values default to KnowledgeGuideline/null) so a malformed model
// response degrades to "no actionable suggestion" rather than crashing the scan.
public sealed record LlmFindingSuggestion(string Title, string Summary, FindingKind Kind = FindingKind.KnowledgeGuideline, PipelineStage? TargetStage = null);

public interface IInsightLlmAnalyzerService
{
    Task<IReadOnlyList<LlmFindingSuggestion>> AnalyzeAsync(InsightLlmScanRequest request, CancellationToken ct = default);
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

public sealed record ClarificationRequest(
    string RequestId,
    string WorkUnitId,
    string Question,
    string? Context,
    bool Blocking,
    IReadOnlyList<string> Options,
    string? RequestedByAgentId,
    DateTimeOffset RequestedAt,
    string? Response = null,
    string? ResponseNote = null,
    string? RespondedBy = null,
    DateTimeOffset? RespondedAt = null);

public sealed record ClarificationRequestResult(
    string RequestId,
    string WorkUnitId,
    bool Blocking,
    bool ParkedAwaitingResponse,
    string Status);

public sealed record ClarificationResponseResult(
    string RequestId,
    string WorkUnitId,
    bool Resumed,
    string Status);

public sealed record ClarificationInboxItem(
    string RequestId,
    string? SessionId,
    string WorkUnitId,
    string Goal,
    string Question,
    string? Context,
    bool Blocking,
    IReadOnlyList<string> Options,
    string? RequestedByAgentId,
    DateTimeOffset RequestedAt,
    string Status,
    string? Response,
    string? ResponseNote,
    string? RespondedBy,
    DateTimeOffset? RespondedAt,
    bool AwaitingResume);

public sealed record ClarificationGoalMetric(
    string WorkUnitId,
    string Goal,
    int Requests,
    int Answered,
    int Abandoned);

public sealed record ClarificationMetrics(
    int Requests,
    int Answered,
    int Abandoned,
    IReadOnlyList<ClarificationGoalMetric> PerGoal);

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
    bool AwaitingResume = false,
    // Phase 12 — set when the agent loop exits with AgentLoopCompletion.AwaitingFileLease (a
    // write hit a file another active sibling currently holds). Mirrors AwaitingResume's "park,
    // don't remove or dead-letter" shape: TryAcquireAsync skips these until IFileLeaseService's
    // release-and-advance hook (on the holder's actual merge) clears the flag, at which point the
    // item — never removed from the queue — is simply eligible for re-acquisition again with
    // AttemptCount > 0, so the resumed WorkerAgentLoop gets isResume: true automatically.
    bool AwaitingFileLease = false);

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

    // Phase 15d — park a queued item in AwaitingResume without dropping it. Used by the
    // clarification workflow to pause execution until a human response resumes it.
    Task MarkAwaitingResumeAsync(string workUnitId, CancellationToken ct = default);

    // Phase 12 — called instead of ReleaseAsync when a worker's loop exits with
    // AgentLoopCompletion.AwaitingFileLease: parks the item (kept in the queue, lease cleared so
    // it's no longer "actively running") rather than removing or dead-lettering it. Mirrors the
    // AwaitingResume flag's "skip until cleared" shape in TryAcquireAsync.
    Task MarkAwaitingFileLeaseAsync(string workUnitId, CancellationToken ct = default);

    // Phase 12 — called by IFileLeaseService's release-and-advance hook (on the holder's actual
    // merge) for the WorkUnitId it just promoted to holder. Clears the flag in place; the item was
    // never removed from the queue, so it's immediately eligible for TryAcquireAsync again with
    // AttemptCount > 0, which is what gives the resumed WorkerAgentLoop isResume: true.
    Task ClearAwaitingFileLeaseAsync(string workUnitId, CancellationToken ct = default);

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

public interface IClarificationCommandService
{
    Task<ClarificationRequestResult> RequestAsync(
        string workUnitId,
        string question,
        string? context = null,
        bool blocking = true,
        IReadOnlyList<string>? options = null,
        string? requestedByAgentId = null,
        string? sessionId = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScheduledItem>> ListAwaitingAsync(CancellationToken ct = default);

    Task<IReadOnlyList<ClarificationInboxItem>> ListActiveRequestsAsync(CancellationToken ct = default);

    Task<ClarificationMetrics> GetMetricsAsync(CancellationToken ct = default);

    Task<ClarificationResponseResult> RespondAsync(
        string workUnitId,
        string response,
        string? note = null,
        string? respondedBy = null,
        string? requestId = null,
        bool resume = true,
        string? sessionId = null,
        CancellationToken ct = default);
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

    /// <summary>
    /// Records a Plan artifact in the DAG, attached to the work unit's artifact lineage.
    /// Replaces the planner's old nm_v1_workspace_write-to-plan.json pattern — the plan
    /// now lives exclusively in the DAG, not as a physical file on the branch.
    /// </summary>
    Task<ArtifactRef> RecordPlanAsync(string workUnitId, string planContent, CancellationToken ct = default);

    Task<IReadOnlyList<ArtifactRef>> QueryAsync(
        string workUnitId,
        string? type = null,
        string? keywords = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ArtifactRef>> ListAsync(
        string workUnitId,
        bool includeAncestors = true,
        CancellationToken ct = default);

    Task<ArtifactRef> InvalidateAsync(
        string artifactId, string reason, string? sessionId = null, CancellationToken ct = default);
}

public sealed record ExternalDocFetchContent(
    string ContentType,
    string Snapshot,
    bool Truncated,
    int SnapshotBytes);

public interface IExternalDocFetcher
{
    Task<ExternalDocFetchContent> FetchAsync(
        Uri normalizedUrl,
        int maxBytes,
        TimeSpan timeout,
        CancellationToken ct = default);
}

public sealed record DocFetchResult(
    string ArtifactId,
    string WorkUnitId,
    string RequestedUrl,
    string NormalizedUrl,
    string Reason,
    DateTimeOffset FetchedAt,
    string ContentHash,
    string HashAlgorithm,
    string ContentType,
    string Snapshot,
    bool Truncated,
    int SnapshotBytes,
    string? Summary);

public interface IDocFetchCommandService
{
    Task<DocFetchResult> FetchAsync(
        string url,
        string reason,
        string workUnitId,
        string? sessionId = null,
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

    // Phase 14 — cross-session lookup for usage-instrumentation aggregation (WorkspaceUsageMetricsService).
    // GetSessionEventsAsync is scoped to one session; metrics like "top hit files across all workspaces"
    // need to scan by kind regardless of session.
    Task<IReadOnlyList<ExecutionEvent>> GetEventsByKindAsync(
        IReadOnlyList<ExecutionEventKind> kinds,
        DateTimeOffset? since = null,
        CancellationToken ct = default);
}

// Phase 14 — derived/on-demand usage metrics computed from the execution event log, used to decide
// (with evidence instead of speculation) whether any of phase-12-file-ownership-leasing.md's deferred
// coordination features are actually worth building. No persistence of its own.
public interface IWorkspaceUsageMetricsService
{
    Task<IReadOnlyList<FileHitCount>> GetTopFileHitsAsync(
        int topN = 20, DateTimeOffset? since = null, CancellationToken ct = default);

    Task<IReadOnlyList<LeaseContentionHotSpot>> GetLeaseContentionHotSpotsAsync(
        int topN = 20, DateTimeOffset? since = null, CancellationToken ct = default);

    Task<SearchUsageSummary> GetSearchUsageAsync(
        string? workUnitId = null, DateTimeOffset? since = null, CancellationToken ct = default);
}

public sealed record FileHitCount(string Path, int Hits);

public sealed record LeaseContentionHotSpot(
    string Path, int ContentionCount, IReadOnlyList<string> ContendingWorkUnitIds);

public sealed record SearchUsageSummary(int SearchCount, int TotalMatches, int TruncatedCount);

public interface IWorkspaceService
{
    Task<WorkspaceSummary> GetSummaryAsync(string? branchId = null, CancellationToken cancellationToken = default);

    Task<WorkspaceStatus> GetStatusAsync(
        string? branchId = null,
        string? workUnitId = null,
        int limit = 50,
        int offset = 0,
        CancellationToken cancellationToken = default);
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

/// <summary>
/// Durable, append-only log of each agent-loop cycle's LLM exchange — what the OrchestrationEvent/
/// DecisionNode records can't show, since those only capture a decision's outcome, not the
/// reasoning path that produced it. One entry per cycle, never updated after being recorded.
/// </summary>
public interface IConversationLogService
{
    Task<ConversationLogEntry> RecordAsync(ConversationLogEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<ConversationLogEntry>> GetEntriesAsync(string workUnitId, CancellationToken ct = default);
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

    // Stop controls — lets a work-unit/global cancel routine find every still-pending timer it
    // needs to cancel without having to know proposal IDs up front.
    Task<IReadOnlyList<ReviewTimer>> ListPendingAsync(string? workUnitId = null, CancellationToken ct = default);
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

// Phase 12 — replaces the old hard, static FileScope write-time block. A file's lease is held by
// whichever active sibling WorkUnit first successfully writes it, and is held until that holder's
// MergeProposal touching the file is actually merged. Anyone else who wants the same path while
// it's held is queued (FIFO) instead of rejected outright — see FileLeaseService for the
// merge-gated release/resume mechanics.
public interface IFileLeaseService
{
    Task<(bool Granted, string? HolderWorkUnitId)> TryAcquireOrEnqueueAsync(
        string workUnitId, string path, CancellationToken ct = default);

    // Clears the current holder for path and promotes the next FIFO waiter (if any) to holder,
    // returning its WorkUnitId so the caller can copy the merged file into its branch and resume it.
    Task<string?> ReleaseAndAdvanceAsync(string path, CancellationToken ct = default);

    // Failure/dead-letter/manual-stop path: a holder that will never merge must not strand its
    // queue(s) forever. No content is forwarded here — nothing was ever merged. Returns every
    // WorkUnitId promoted to holder as a result (one per path released with a non-empty queue) —
    // the caller must clear each one's IWorkScheduler.AwaitingFileLease flag (this service can't
    // do it itself: IWorkScheduler's own production implementation optionally depends on
    // IMergeService, which optionally depends back on this interface — a direct dependency here
    // would be circular), or a promoted waiter would sit parked forever despite already holding
    // the lease it was waiting on.
    Task<IReadOnlyList<string>> ForceReleaseAllForWorkUnitAsync(string workUnitId, CancellationToken ct = default);

    // Admin/dashboard visibility — every path currently held or queued. Lets a human spot a
    // stuck lease (no live agent to StopAsync, no pending proposal to reject) before reaching for
    // the matching manual-release endpoint below.
    Task<IReadOnlyList<FileLeaseInfo>> ListAsync(CancellationToken ct = default);
}

// Admin-facing read model for IFileLeaseService.ListAsync — deliberately separate from
// FileLeaseService's own internal FileLeaseState record (that one's persisted/serialized
// verbatim and includes the "removed" tombstone shape ReleaseAndAdvanceAsync writes; this one is
// just the current, real state callers actually want to look at).
public sealed record FileLeaseInfo(string Path, string? HolderWorkUnitId, IReadOnlyList<string> WaitQueue);

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
    // pattern: optional case-insensitive filter against each result's relative path, supporting
    // * (any run of characters) and ? (any single character) wildcards — a plain filename like
    // "WeatherForecastController.cs" matches as a substring, so callers can find a specific file by
    // name across the whole branch (subPath omitted) without already knowing its directory.
    Task<IReadOnlyList<string>> ListAsync(string branchId, string? subPath = null, string? pattern = null, CancellationToken ct = default);

    // Content search (grep), as opposed to ListAsync's filename-only matching. query is matched
    // literally unless regex=true; caseSensitive defaults to false. filePattern reuses ListAsync's
    // wildcard syntax to scope which files get scanned. contextLines (clamped to [0,20]) controls
    // how many surrounding lines accompany each match, so callers don't need an immediate follow-up
    // ReadAsync just to see what's around a hit. Binary files (null-byte heuristic on the first
    // chunk) and files over MaxReadBytes are skipped silently, same as ListAsync skips hidden paths.
    // Returns Truncated=true once maxResults (clamped to [1,1000]) matches have been found.
    Task<(IReadOnlyList<WorkspaceSearchMatch> Matches, bool Truncated)> SearchAsync(
        string branchId, string query, string? subPath = null, string? filePattern = null,
        bool regex = false, bool caseSensitive = false, int contextLines = 3, int maxResults = 200,
        CancellationToken ct = default);

    // Targeted edit, as opposed to WriteAsync's full-file replacement. expectedMatches defaults to
    // 1 (uniqueness required, mirroring Claude Code's Edit tool): throws if oldText's literal
    // occurrence count in the file isn't exactly expectedMatches, naming the actual count in the
    // message so the caller can adjust (add context if too many, fix the assumption if zero). Pass
    // expectedMatches=N explicitly to replace N legitimate occurrences at once.
    Task<WorkspaceReplaceResult> ReplaceAsync(
        string branchId, string relativePath, string oldText, string newText, int expectedMatches = 1,
        CancellationToken ct = default);

    // Batched ReadAsync — collapses the search-then-read-several-hits pattern into one round trip.
    // A missing path comes back as Found=false/Content=null in its own slot rather than failing the
    // whole call, matching ReadAsync's existing "null means not found" convention. Callers should
    // clamp paths to a sane count (e.g. [1,50]); this method itself does not reject an oversized list.
    Task<IReadOnlyList<WorkspaceFileRead>> ReadManyAsync(
        string branchId, IReadOnlyList<string> paths, CancellationToken ct = default);

    Task<string> DiffAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default);
    Task ApplyBranchAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default);
    Task CopyFilesAsync(
        string sourceBranchId,
        string targetBranchId,
        IReadOnlyList<string> relativePaths,
        CancellationToken ct = default);
    Task<string?> GetWorkingDirectoryAsync(string branchId, CancellationToken ct = default);

    // Compares branchId's working directory against an arbitrary absolute directory outside
    // RootPath (e.g. the live repository on disk) rather than another branch id — used to detect
    // drift between "main" and the real repo. Read-only; see ApplyExternalPathAsync to mirror it in.
    Task<WorkspaceDiff> DiffExternalPathAsync(string branchId, string externalPath, CancellationToken ct = default);

    // Always a full destructive mirror: files present in branchId but absent from externalPath are
    // deleted, same "delete what's absent from source" semantics as ApplyBranchAsync today. This is
    // not specific to a repository switch — ordinary drift (a file removed on disk) needs exactly
    // the same deletion behavior to stay correct. Do not special-case or soften this for either caller.
    Task ApplyExternalPathAsync(string branchId, string externalPath, CancellationToken ct = default);
}

// Carries a structural fingerprint of the external path alongside the diff so callers never need a
// second tree walk just to get one (see RepositorySyncService).
public sealed record WorkspaceDiff(
    IReadOnlyList<string> Added, IReadOnlyList<string> Modified, IReadOnlyList<string> Deleted,
    // Diagnostic signal only (relative path + size + last-write time, no content reads) — NOT a
    // content hash. Two repositories could theoretically collide; never used to decide sync
    // behavior (path-string equality still drives RepositoryDrift vs. RepositorySwitch).
    string ExternalFingerprint)
{
    public bool IsEmpty => Added.Count == 0 && Modified.Count == 0 && Deleted.Count == 0;
}

// Line is 1-based and points at the matching line; StartLine/EndLine bound the context window
// (inclusive, also 1-based) that Snippet's joined text spans.
public sealed record WorkspaceSearchMatch(
    string Path, int Line, int StartLine, int EndLine, string Snippet);

// OldLength/NewLength are character counts of the file content before/after the replacement.
// Diff is a short "@@ line {n} @@ / - old / + new" block per replaced occurrence, not a full unified
// diff — enough to confirm the edit landed correctly without a follow-up full-file read.
public sealed record WorkspaceReplaceResult(
    int Matches, long OldLength, long NewLength, string Diff);

// One slot of a ReadManyAsync batch. Found=false/Content=null when the path doesn't exist in the
// branch — mirrors ReadAsync's null-means-not-found convention rather than throwing per-path.
public sealed record WorkspaceFileRead(string Path, string? Content, bool Found);

// Read-only semantic query input. Symbol can be omitted when path+line(+column) are provided,
// in which case the implementation resolves the symbol at that source location.
public sealed record WorkspaceSymbolQuery(
    string? Symbol = null,
    string? Path = null,
    int? Line = null,
    int? Column = null,
    int MaxResults = 200);

// Symbol location in branch-relative coordinates (1-based line/column).
public sealed record WorkspaceSymbolLocation(
    string Path,
    int Line,
    int Column,
    string SymbolName,
    string? ContainingSymbol = null,
    string? Kind = null);

// Phase 15a — compiler-backed semantic navigation for branch workspaces.
// Read-only by design: definition/reference/implementation lookup only.
public interface IWorkspaceSemanticNavigationService
{
    Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> FindDefinitionsAsync(
        string branchId,
        WorkspaceSymbolQuery query,
        CancellationToken ct = default);

    Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> FindReferencesAsync(
        string branchId,
        WorkspaceSymbolQuery query,
        CancellationToken ct = default);

    Task<(IReadOnlyList<WorkspaceSymbolLocation> Locations, bool Truncated)> FindImplementationsAsync(
        string branchId,
        WorkspaceSymbolQuery query,
        CancellationToken ct = default);
}

public interface IRepositorySyncService
{
    Task<PendingExternalSync?> SyncBranchFromRepositoryAsync(
        string branchId, string repositoryPath, SyncTrigger trigger, CancellationToken ct = default);

    // Non-mutating lookup — lets a caller find the chain's current tail via
    // LatestExternalChangesetId, mirrors IKnownGoodStateService.GetAsync's role.
    Task<RepositorySyncState?> GetStateAsync(string branchId, CancellationToken ct = default);
}

public sealed record WorkspaceSummary(
    IReadOnlyList<string> ActiveWorkUnits,
    IReadOnlyList<string> ActiveAgents,
    IReadOnlyList<string> PendingMerges,
    IReadOnlyList<string> Failures,
    IReadOnlyList<string> KnownGoodStates,
    // Capability-gap fix: lets an agent learn the boundary of what it's actually managing — Studio
    // has exactly one repository/root per instance — instead of inferring it (or guessing wrong)
    // from the files it happens to see in its own branch.
    string? RootPath = null,
    string? SeedRepositoryPath = null);

public enum WorkspaceChangeKind
{
    Added,
    Modified,
    Deleted,
    Changed,
}

public sealed record WorkspaceStatusFileChange(
    string Path,
    WorkspaceChangeKind ChangeKind,
    string? ProposalId = null);

public sealed record WorkspaceStatusProposalSummary(
    string ProposalId,
    MergeProposalStatus Status,
    IReadOnlyList<string> FilesTouched,
    int AddedFiles,
    int ModifiedFiles,
    int DeletedFiles,
    int? AddedLines,
    int? RemovedLines,
    string? Summary,
    DateTimeOffset? DiffGeneratedAt);

public sealed record WorkspaceStatusDiffStats(
    int AddedFiles,
    int ModifiedFiles,
    int DeletedFiles,
    int? AddedLines = null,
    int? RemovedLines = null);

public sealed record WorkspaceStatus(
    string? BranchId,
    string? WorkUnitId,
    WorkUnitStatus? CurrentWorkUnitStatus,
    IReadOnlyList<WorkspaceStatusFileChange> ChangedFiles,
    IReadOnlyList<WorkspaceStatusProposalSummary> ProposalSummaries,
    WorkspaceStatusDiffStats? DiffStats,
    bool Truncated,
    int Limit,
    int Offset,
    int NextOffset,
    DateTimeOffset GeneratedAt);

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

// Phase 9a — detects sub-project roots inside a branch's working directory (one per directory
// containing a recognized build-system marker file), each with its own resolved build/test/run
// commands. Replaces the single-flat-detection assumption in IWorkspaceExecutionService for
// multi-project repos.
public interface IWorkspaceProfileService
{
    /// <summary>Returns the cached profile for this branch, detecting it on first access.</summary>
    Task<WorkspaceProfile> GetOrDetectAsync(string branchId, CancellationToken ct = default);

    /// <summary>Forces re-detection, bypassing (and refreshing) the cache.</summary>
    Task<WorkspaceProfile> RescanAsync(string branchId, CancellationToken ct = default);

    /// <summary>
    /// Drops the cached profile for a branch with no recompute. For ephemeral branches (e.g. the
    /// temp composite branches ExecuteCompositeAsync creates and deletes per call) — without this
    /// the cache would grow forever, keyed by GUID branch ids that no longer exist on disk.
    /// </summary>
    void Invalidate(string branchId);
}

// Slice 16c — shared entry point for workspace execution commands — called by both MCP tools
// (WorkspaceTools) and REST endpoints (StudioRestEndpoints) so they cannot drift.
public interface IWorkspaceExecutionCommandService
{
    Task<BranchExecutionResult> BuildAsync(
        string branchId,
        string? buildCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default,
        string? rootPath = null);

    Task<BranchExecutionResult> TestAsync(
        string branchId,
        string? testCommand = null,
        int timeoutSeconds = 300,
        CancellationToken ct = default,
        string? rootPath = null);

    Task<BranchExecutionResult> ExecAsync(
        string branchId,
        WorkspaceExecutionRequest request,
        CancellationToken ct = default);

    Task<BranchExecutionResult?> GetLatestAsync(
        string branchId,
        CancellationToken ct = default);

    /// <summary>
    /// Runs the application in a branch. With no <paramref name="rootPath"/>/<paramref name="runCommand"/>,
    /// runs every WorkspaceProfile root with a resolved RunCommand (Phase 9c) — long-running roots
    /// (dev servers) are started detached and tracked (see <see cref="StopAsync"/>); one-shot roots
    /// block up to <paramref name="timeoutSeconds"/> exactly like Build/Test. An explicit
    /// <paramref name="runCommand"/> always runs once, since long-running-ness can't be inferred for
    /// an arbitrary caller-supplied command.
    /// </summary>
    Task<IReadOnlyList<BuildResult>> RunAsync(
        string branchId,
        string? rootPath = null,
        string? runCommand = null,
        int timeoutSeconds = 120,
        Dictionary<string, string>? environmentVariables = null,
        CancellationToken ct = default);

    /// <summary>Stops tracked long-running processes for a branch (see <see cref="RunAsync"/>). Narrows by pid and/or rootPath when given. Returns the count stopped.</summary>
    Task<int> StopAsync(
        string branchId,
        int? pid = null,
        string? rootPath = null,
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
