using System.Text.Json.Serialization;
using NodalMerge.Studio.Contracts.Domain;

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

    /// <summary>reviewedBy: who made this decision — defaults to "user" (this is the human review
    /// path; automated review goes through AutomatedReviewAsync, which records the reviewer agent
    /// id instead). Persisted on MergeProposal.ReviewedBy and carried in the
    /// ProposalApproved/Rejected event payloads.</summary>
    Task<MergeProposal> ReviewAsync(
        string proposalId,
        MergeProposalStatus decision,
        string? notes = null,
        string? reviewedBy = null,
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

    /// <summary>
    /// The human-triggered counterpart to ApplyAsync's deferred write-back when
    /// WorkspaceOptions.UsePromotionBranch is on: writes CandidateBranchId's current (already
    /// additively composed) content to every distinct real repo path any Merged-but-not-yet-
    /// promoted proposal resolves to, then marks each PromotedToDisk. A no-op (Succeeded: true,
    /// empty Promoted list) when nothing is pending. If RequireBuildBeforeProposal/
    /// RequireTestBeforeProposal is on, runs that gate against the composed candidate branch first
    /// and promotes nothing on failure.
    /// </summary>
    Task<PromoteResult> PromoteAsync(CancellationToken cancellationToken = default);
    }

    public sealed record PromoteResult(
        bool Succeeded,
        IReadOnlyList<MergeProposal> Promoted,
        string? FailureReason = null);

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

// Record-keeper for candidate-branch cross-goal conflicts (see CandidateConflictRecord's own doc
// comment for why this is separate from IConflictService/RepositoryConflict). Detection happens in
// InMemoryMergeService.TryApplyAdditivelyAsync; this is purely the persisted-record surface the
// promote UI queries.
public interface ICandidateConflictService
{
    Task<CandidateConflictRecord> RecordAsync(CandidateConflictRecord conflict, CancellationToken ct = default);

    Task<CandidateConflictRecord?> GetAsync(string conflictId, CancellationToken ct = default);

    Task<IReadOnlyList<CandidateConflictRecord>> GetOpenAsync(CancellationToken ct = default);

    // Atomically transitions Open -> Reconciling; returns null if the conflict doesn't exist or
    // isn't currently Open (re-entrancy guard — two near-simultaneous triggers for the same
    // conflict must not both create a reconciliation work unit).
    Task<CandidateConflictRecord?> TryStartReconcilingAsync(string conflictId, CancellationToken ct = default);

    // Reconciling -> Open, for when the reconciliation attempt that claimed this conflict died
    // (agent dead-lettered/cancelled) without ever reaching MarkResolvedAsync. Without this, a
    // failed reconciliation left the conflict stuck Reconciling forever — un-re-triggerable and
    // un-resolvable, a dead end for the human. Returns null if not found or not Reconciling.
    Task<CandidateConflictRecord?> TryReopenAsync(string conflictId, CancellationToken ct = default);

    Task<CandidateConflictRecord?> MarkResolvedAsync(string conflictId, CancellationToken ct = default);
}

// Thin candidate-branch-specific adapter over IReconciliationAgentService — translates a
// CandidateConflictRecord into a ReconciliationRequest and guards re-entrancy via
// ICandidateConflictService.TryStartReconcilingAsync. The only place in the codebase that
// interprets the "candidate-conflict:{id}" ReconciliationSourceRef convention. Also owns the
// manual-resolution path (TryResolveManuallyAsync) — a human directly supplies the combined
// content instead of spinning up an agent, for the common case where the correct combination
// (e.g. "keep both, in this order") is already obvious and not worth a full agent turn.
public interface ICandidateReconciliationTrigger
{
    // Returns null if the conflict doesn't exist or isn't currently Open (already reconciling/
    // resolved) — a no-op re-entrancy guard rather than a thrown error, since both the human button
    // and the auto-trigger hook may race to call this for the same conflict. credentials, when
    // supplied, is passed straight through to ReconciliationRequest.Credentials — see that field's
    // own comment for why this takes priority over best-effort source-goal credential inheritance.
    Task<WorkUnit?> TryTriggerAsync(
        string conflictId, string? steeringNotes = null, GoalDefaultCredentials? credentials = null, CancellationToken ct = default);

    // Writes resolvedContent directly onto the candidate branch for each conflicting path, records
    // a synthetic Merged MergeProposal representing the human's resolution (so it flows through the
    // same promote/write-back pipeline as any agent-produced one), supersedes the original
    // conflicting proposals, and marks the conflict Resolved. resolvedContent must cover every path
    // in the conflict's ConflictingPaths. Returns null under the same re-entrancy guard as
    // TryTriggerAsync; throws InvalidOperationException if resolvedContent is missing a path.
    Task<MergeProposal?> TryResolveManuallyAsync(
        string conflictId, IReadOnlyDictionary<string, string> resolvedContent, CancellationToken ct = default);
}

// Record-keeper for fan-out-sibling task-level conflicts (see TaskConflictRecord's own doc
// comment). Mirrors ICandidateConflictService's shape exactly — detection happens in the same
// InMemoryMergeService.TryApplyAdditivelyAsync block, this is purely the persisted-record surface.
public interface ITaskConflictService
{
    Task<TaskConflictRecord> RecordAsync(TaskConflictRecord conflict, CancellationToken ct = default);

    Task<TaskConflictRecord?> GetAsync(string conflictId, CancellationToken ct = default);

    // parentWorkUnitId filters to one goal's own conflicts — unlike candidate (a single session-wide
    // branch), task conflicts are naturally scoped per fan-out parent, and the review panel only
    // ever wants the conflicts for the goal it's showing.
    Task<IReadOnlyList<TaskConflictRecord>> GetOpenAsync(string? parentWorkUnitId = null, CancellationToken ct = default);

    Task<TaskConflictRecord?> TryStartReconcilingAsync(string conflictId, CancellationToken ct = default);

    // Reconciling -> Open; see ICandidateConflictService.TryReopenAsync — same failed-reconciliation
    // recovery hatch, same semantics.
    Task<TaskConflictRecord?> TryReopenAsync(string conflictId, CancellationToken ct = default);

    Task<TaskConflictRecord?> MarkResolvedAsync(string conflictId, CancellationToken ct = default);
}

// Thin task-level adapter over IReconciliationAgentService, mirroring ICandidateReconciliationTrigger
// exactly — the only place that interprets the "task-conflict:{id}" ReconciliationSourceRef
// convention. TryResolveManuallyAsync writes to a dedicated scratch branch
// (task-resolution/{conflictId}), not merge/{ParentWorkUnitId} directly — that branch gets
// destructively rebuilt from scratch by MergeReconciliationService.TryReconcileAsync's own
// ApplyBranchAsync reset every time it runs, so a manual resolution needs its own durable branch a
// synthetic proposal's SourceBranch can point to, same as any other child's own branch would.
public interface ITaskReconciliationTrigger
{
    Task<WorkUnit?> TryTriggerAsync(
        string conflictId, string? steeringNotes = null, GoalDefaultCredentials? credentials = null, CancellationToken ct = default);

    Task<MergeProposal?> TryResolveManuallyAsync(
        string conflictId, IReadOnlyDictionary<string, string> resolvedContent, CancellationToken ct = default);
}

// Attempts automated per-file resolution for a candidate-branch conflict by calling the same
// IMergeStrategy implementations ConflictResolutionService orchestrates for the unrelated
// RepositoryConflict/CAS subsystem — but directly, bypassing that subsystem entirely (see
// CandidateConflictRecord's doc comment). Storage-agnostic: just base/A/B content strings in,
// merged content or failure out.
public interface ICandidateConflictResolutionService
{
    Task<CandidateConflictResolution> TryResolveAsync(
        string path,
        string? baseContent,
        string? candidateContent,
        string? proposalContent,
        CancellationToken ct = default);
}

public sealed record CandidateConflictResolution(
    bool Resolved,
    string? MergedContent,
    string? StrategyName = null,
    string? FailureReason = null);

// Source-agnostic request to reconcile N proposals whose owning goals both had legitimate intent
// but diverged on the same file(s) — the multi-turn-agent alternative to blocking with a "restart
// from scratch" report. Deliberately doesn't know which subsystem detected the conflict (candidate-
// branch cross-goal collisions today; fan-out sibling/task-level conflicts as a future adapter) —
// see IReconciliationAgentService's own doc comment.
public sealed record ReconciliationRequest(
    string SeedBranchId,
    IReadOnlyList<string> ProposalIds,
    IReadOnlyList<string> ConflictingPaths,
    // Opaque to the core — round-tripped onto WorkUnit.ReconciliationSourceRef so the triggering
    // adapter can later find its own record from InMemoryMergeService.ApplyAsync's completion hook.
    string SourceRef,
    ReviewPolicy WorkspaceReviewPolicy = ReviewPolicy.AgentApproval,
    // Free-text human guidance on HOW to combine the conflicting sides — e.g. "keep both, Brad's
    // section first then Jake's" rather than picking a winner. Deliberately optional and separate
    // from each proposal's own goal text: no amount of re-reading two independent goals tells the
    // agent which combination the human actually wants when the "right" answer is genuinely
    // ambiguous (both entirely valid, mutually exclusive outcomes). Surfaced first and most
    // prominently in the synthetic goal when present.
    string? SteeringNotes = null,
    // Set only by the task-level adapter: makes the created work unit a proper fan-out child
    // (gated by TaskReviewPolicy, its proposal auto-redirected to merge/{ParentWorkUnitId} by
    // MergeCommandService.ProposeAsync's existing fan-out-child override) instead of the default
    // fresh top-level goal the candidate-branch adapter uses. WorkspaceReviewPolicy above is passed
    // as both WorkspaceReviewPolicy and TaskReviewPolicy on the created work unit — whichever one
    // WorkspaceReviewScope.AppliesToRealRepo actually consults for it depends on this field.
    string? ParentWorkUnitId = null,
    // Explicit credentials for the reconciliation orchestrator itself (e.g. a dedicated
    // "Reconciler" slot on the caller's Agent Topology template), resolved client-side the same
    // way Multi-Model Comparison resolves its own orchestrator credentials before spawning.
    // Takes priority over ReconciliationAgentService's own best-effort inheritance from a source
    // goal's in-memory orchestrator registration — that fallback only works if the exact source
    // goal happens to still have a live registration in this process (lost on host restart, and
    // never present at all for a fan-out task's own work unit, only its top-level parent), so it
    // silently no-ops far too often for something advertised as "one-click."
    GoalDefaultCredentials? Credentials = null);

// Creates an ordinary top-level WorkUnit whose goal carries full reconciliation context (every
// source proposal's owning goal text + the conflicting paths' diverged content) directly in the
// goal text, seeded from the conflict's own target branch — reusing the existing worker-agent loop,
// McpToolDispatcher tool surface (including build/test), and propose/review pipeline wholesale
// rather than inventing a new agent loop. Distinct from:
//  - LlmAssistedMergeStrategy: a stateless one-shot "three text blobs in, one merged blob out" call
//    with no goal awareness, no multi-file visibility, and no ability to run anything.
//  - MergeReconciliationService: a purely mechanical fold of a fan-out's own already-approved
//    children (CopyFilesAsync only) — no LLM/agent involved at all.
// Intentionally source-agnostic: knows nothing about CandidateConflictRecord or any other specific
// conflict-tracking record. A thin, subsystem-specific adapter (e.g. the candidate-branch adapter)
// translates its own record into a ReconciliationRequest and, on completion, uses
// WorkUnit.ReconciliationSourceRef to mark its own record resolved.
public interface IReconciliationAgentService
{
    Task<WorkUnit> TriggerAsync(ReconciliationRequest request, CancellationToken ct = default);
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
    string? ConflictReportPath = null,
    // Human-readable explanation of WHY this outcome happened (which child is blocking, what a
    // conflict is on) — surfaced in the orchestrator's decision log so a reinvoke that decides
    // "nothing to do" says what it saw instead of silently no-op'ing.
    string? Detail = null);

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
    /// mode selects whether the retry target's branch is reverted to its pre-attempt snapshot
    /// (RestartMode.Revert) or left as-is with a compacted RevisionContext artifact attached
    /// (RestartMode.Revise, the default).
    /// </summary>
    Task<AutomatedRejectionResult> HandleHumanRejectionAsync(
        string proposalId,
        string? reviewNotes,
        RestartMode mode = RestartMode.Revise,
        string? sessionId = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// A fanned-out task child's own inline reviewer (TaskReviewPolicy.AgentApproval/Hybrid) just
    /// rejected that child's proposal — unlike HandleAutomatedRejectionAsync (which retries every
    /// child of a parent whose *reconciled batch* proposal was rejected), this retries only the one
    /// work unit that owns proposalId, same as HandleHumanRejectionAsync does for a non-fan-out
    /// proposal. Tracked under its own per-work-unit AutomatedReviewRejectionCount budget so it
    /// can't be starved by, or starve, a sibling's own retry cycle. Always attaches a compacted
    /// RevisionContext (RestartMode.Revise) — there's no human present to choose Revert.
    /// </summary>
    Task<AutomatedRejectionResult> HandleAutomatedTaskRejectionAsync(
        string proposalId,
        string? reviewerAgentId = null,
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
        FailureKind kind = FailureKind.Exception,
        string? credentialRef = null,
        CancellationToken cancellationToken = default);

    Task<DeadLetterEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default);

    Task<DeadLetterEntry?> GetLatestForWorkUnitAsync(
        string workUnitId,
        CancellationToken cancellationToken = default);

    // Every dead-letter entry recorded for this work unit, oldest first — the full failure story
    // (e.g. "max iterations" -> manually steered retry -> "transient 529") in one call, instead of
    // a human following steeredFromDeadLetterEntryId across separate GetAsync calls by hand.
    Task<IReadOnlyList<DeadLetterEntry>> GetHistoryForWorkUnitAsync(
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
        string? overrideCredentialRef = null,
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
        string? overrideCredentialRef = null,
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

// Re-plan-the-slice: the mechanism both recovery tracks from
// plans/orchestrator-reliability-and-observability.md Phase 1.4 use to safely retry a failed
// slice without touching sibling work — never retries the failed work unit itself, always spawns
// fresh, independently-budgeted children and marks the original terminal (Cancelled). Distinct
// from IDeadLetterService.RetryAsync/RetryWithContextAsync, which resume the *same* work unit.
public interface IReplanService
{
    Task<ReplanResult> ReplanFailedSliceAsync(string entryId, CancellationToken cancellationToken = default);
}

public enum ReplanOutcome
{
    Replanned,
    NotFound,
    // The failed work unit has no parent to attach new sibling slices to (it's a top-level goal,
    // not a fanned-out slice) — this mechanism only applies to slices, not whole goals.
    NotApplicable,
    // The bounded PlannerAgentLoop ran but didn't end naturally (hit its own iteration cap or was
    // cancelled) — nothing is changed; the original dead-letter entry and work unit are untouched.
    PlanningFailed,
    // The planner claimed success but recorded a Plan artifact that produced no new child work
    // units (e.g. empty slice list) — again, nothing is changed.
    NoNewSlicesProduced,
}

public sealed record ReplanResult(
    ReplanOutcome Outcome,
    string? Message = null,
    IReadOnlyList<string>? NewWorkUnitIds = null,
    // plans/phase-d-implementation.md D3 — the manual replan triggers (REST/MCP) surface the
    // current staleness signal state on the parent (plan-owning) work unit alongside the replan
    // outcome itself, so a human deciding whether to replan (or having just replanned) can see
    // "this plan looks stale" in the same response. Null when no parent work unit was resolved
    // (NotFound/NotApplicable outcomes) or IPlanStalenessService isn't registered.
    PlanStalenessState? StalenessSignal = null);

// plans/phase-d-implementation.md D3 — staleness *signals* only, never auto-replan (see
// ExecutionEventKind.PlanStalenessSignalRaised's own doc comment for why automatic replan stays
// deferred). Evaluated at the two cheapest existing checkpoints where the underlying data already
// changes — a superseding Decision artifact recorded (IArtifactCommandService.RecordAsync) and a
// slice transitioning to DeadLettered (IDeadLetterService.RecordFailureAsync) — never a polling
// timer. Both thresholds are WorkspaceOptions knobs (PlanStalenessSupersedingDecisionThreshold /
// PlanStalenessDeadLetteredSliceThreshold).
public interface IPlanStalenessService
{
    // Called after a Decision artifact with a non-empty Supersedes list is recorded. Walks the
    // WorkUnit ancestor chain from decision.OwnedByWorkUnitId for the nearest self-owned Plan
    // artifact; if found, counts qualifying superseding decisions recorded since that plan across
    // the plan owner and its immediate fanned-out children, and raises the event when the count
    // reaches the configured threshold. No-op if decision isn't a superseding Decision or has no
    // owning work unit.
    Task NotifySupersedingDecisionRecordedAsync(ArtifactRef decision, CancellationToken ct = default);

    // Called after workUnitId transitions to DeadLettered. Counts sibling slices (children of
    // workUnitId's immediate parent) currently DeadLettered and raises the event when the count
    // reaches the configured threshold. No-op if workUnitId has no parent.
    Task NotifySliceDeadLetteredAsync(string workUnitId, CancellationToken ct = default);

    // On-demand read of the current signal state for planOwnerWorkUnitId — used by the manual
    // replan triggers to attach staleness state to their response regardless of whether a
    // Notify* call most recently raised the event (an operator may check well after the
    // triggering decision/dead-letter, or before either has happened again).
    Task<PlanStalenessState> GetStateAsync(string planOwnerWorkUnitId, CancellationToken ct = default);
}

public sealed record PlanStalenessState(
    bool IsStale,
    int SupersedingDecisionCount,
    int SupersedingDecisionThreshold,
    int DeadLetteredSliceCount,
    int DeadLetteredSliceThreshold,
    string? PlanArtifactId);

// Continue-track (Phase 1.4 two-track failure/recovery design): reconstructs a dead-lettered
// work unit's own prior conversation from ConversationLogEntry rows and resumes the SAME work
// unit with a fresh iteration budget, instead of spawning fresh siblings (that's ReplanService's
// job, and the other Continue-track option). Only meaningful for MaxIterationsExceeded — nothing
// about hitting the iteration ceiling implies the approach was wrong, only that the budget was
// too small, so continuing the same conversation (not restarting it) is the right recovery.
public interface IContinueService
{
    Task<ContinueResult> ContinueWithPriorContextAsync(
        string entryId,
        // Optional resupply, same shape/priority as IDeadLetterService.RetryWithCredentialOverrideAsync
        // — lets a caller (e.g. the VS Code extension re-reading its own SecretStorage) hand back
        // live credentials when the entry's own captured ApiKey and the shared
        // IRuntimeCredentialCache are both cold (e.g. right after a Host restart).
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideCredentialRef = null,
        CancellationToken cancellationToken = default);
}

public enum ContinueOutcome
{
    Continued,
    NotFound,
    // Continue only applies to MaxIterationsExceeded — for any other FailureKind, the approach
    // itself was the problem, so resuming the same conversation isn't the right recovery (that's
    // Retry-track's job: steer-and-retry, or re-plan from scratch).
    NotApplicable,
    // No LLM credentials resolvable, or the reconstructed conversation loop didn't complete
    // successfully — treated as MaxIterationsExceeded again (a new dead-letter entry is recorded
    // with the same Kind so Continue can be reached for again, or the human can switch tracks).
    NotCompleted,
    // The resumed run hit a file lease held by another active sibling, or requested a human
    // clarification — neither is the agent's own fault, so unlike NotCompleted this does NOT
    // record a fresh dead-letter entry (that would burn one of the work unit's limited
    // MaxFailureAttempts on pure infrastructure contention). Instead the work unit is handed to
    // the normal scheduler queue and parked exactly the way a scheduler-driven run already is —
    // it resumes automatically once the lease clears or a human answers the clarification.
    Parked,
}

public sealed record ContinueResult(
    ContinueOutcome Outcome,
    string? Message = null,
    AgentLoopCompletion? Completion = null);

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

    // plans/harness-hosting-architecture.md Phase B3 — external-harness resume identity
    // (ClaudeCodeExecutor's own CLI session id) needs a durable home; Metadata is the existing
    // ad hoc/future-use grab-bag (see WorkUnit.cs), already given read-merge-write treatment by
    // AmendGoalForSteeredRetryAsync below. Generic single-key setter (null value removes the key)
    // rather than a harness-specific field, so future ad hoc uses don't need their own setter.
    // Default body (GetAsync + CreateAsync upsert) exists so every existing IWorkUnitService test
    // fake keeps compiling without a mechanical edit to all of them; InMemoryWorkUnitService
    // overrides it with the same direct-dictionary read-merge-write every other setter here uses,
    // avoiding the upsert race IncrementReviewRejectionCountAsync's own comment already warns about.
    async Task<WorkUnit> SetMetadataAsync(
        string workUnitId,
        string key,
        string? value,
        CancellationToken cancellationToken = default)
    {
        var workUnit = await GetAsync(workUnitId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Work unit '{workUnitId}' was not found.");
        var metadata = new Dictionary<string, string>(workUnit.Metadata ?? new Dictionary<string, string>());
        if (value is null)
            metadata.Remove(key);
        else
            metadata[key] = value;

        return await CreateAsync(workUnit with { Metadata = metadata, UpdatedAt = DateTimeOffset.UtcNow }, cancellationToken)
            .ConfigureAwait(false);
    }

    // Race-safety fix — increments one of the two ExecutionInfo rejection counters via a fresh
    // internal read-merge-write, the same convention every other setter here uses. Replaces the
    // old AutomatedReviewGateService pattern of reading the whole WorkUnit, computing a new
    // ExecutionInfo, and calling CreateAsync(parent with {...}) — CreateAsync is an unconditional
    // upsert, so that pattern silently reverted any Status/CurrentStage/etc. change a concurrent
    // writer (e.g. OrchestratorAgentLoop, WorkUnitCommandService.CancelAsync) made to the same
    // WorkUnit in the gap between the read and the write.
    Task<WorkUnit> IncrementReviewRejectionCountAsync(
        string workUnitId,
        bool automated,
        CancellationToken cancellationToken = default);

    // Race-safety fix — same convention as IncrementReviewRejectionCountAsync above. Replaces
    // InMemoryDeadLetterService.RecordFailureAsync's old pattern of reading the whole WorkUnit,
    // computing a new ExecutionInfo, and calling CreateAsync(unit with {...}), which silently
    // reverted any concurrent writer's Status/CurrentStage/etc. change made in the read/write gap.
    Task<WorkUnit> IncrementFailureAttemptCountAsync(
        string workUnitId,
        CancellationToken cancellationToken = default);

    // Race-safety fix — same convention. Replaces InMemoryDeadLetterService.RetryWithContextAsync's
    // old CreateAsync(unit with { Goal, Metadata, ExecutionInfo }) upsert. Folds the steering
    // correction into Goal (projections only ever surface Goal/SuccessCriteria to agents, never
    // Metadata), records the steering metadata keys, and resets FailureAttemptCount to 0 so the
    // freshly-steered retry starts with a clean failure budget.
    Task<WorkUnit> AmendGoalForSteeredRetryAsync(
        string workUnitId,
        string amendedGoal,
        string steeringContext,
        string deadLetterEntryId,
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

    // Fan-out collision avoidance — when two sibling slices declare overlapping fileScope with no
    // dependsOn between them (a planning gap, not a runtime one), FanOutService inserts this edge
    // instead of letting both start and fight over a file lease. Idempotent (a repeat call is a
    // no-op) so a fan-out pass that runs again before the edge takes effect doesn't double-add it.
    // The caller is responsible for cycle-safety (walking the target's own DependsOn chain before
    // calling) — this method itself does not check, so a caller elsewhere with a different
    // invariant isn't forced into this one's specific cycle policy.
    Task<WorkUnit> AddDependencyAsync(
        string workUnitId,
        string dependsOnWorkUnitId,
        CancellationToken cancellationToken = default);
}

// Slice 12c — pushes live pipeline-stage updates to connected extension clients over the
// embedded NodalMerge runtime WebSocket room, so the Artifact Explorer doesn't have to poll to
// Optional collaborator — materializes the current canonical checkpoint into the Studio CRDT
// sync graph after a meaningful state-change boundary (WorkUnit completed / merged). Optional
// because the underlying runtime bridge only exists in the Studio Host process; unit tests and
// integration tests that build services directly never register an implementation.
public interface IStudioGraphPromoter
{
    Task TryPromoteStudioCheckpointAsync();
}

public sealed record CausalParentsResult(string[] ParentIdsHex, bool NodeFound);
public sealed record CanonicalResolutionEntry(string Key, string ValueBytesB64);
public sealed record CanonicalResolutionResult(IReadOnlyList<CanonicalResolutionEntry> Entries);
public sealed record SyncDiffResult(string[] OnlyInServer, string[] OnlyInPeer);

// Exposes read-only causal/CRDT graph queries for the studio room. Backed by the real
// StateGraph that PromoteCheckpointToGraph populates. Optional because the runtime bridge
// only exists in the Studio Host process.
public interface IStudioCausalGraphService
{
    Task<string[]> GetFrontierAsync(CancellationToken cancellationToken = default);
    Task<CausalParentsResult> GetCausalParentsAsync(string nodeIdHex, CancellationToken cancellationToken = default);
    Task<CanonicalResolutionResult> GetCanonicalResolutionAsync(CancellationToken cancellationToken = default);
    Task<SyncDiffResult> ComputeSyncDiffAsync(string[] peerNodeIdsHex, CancellationToken cancellationToken = default);
}

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
        ReviewPolicy? taskReviewPolicy = null,
        ReviewPolicy? workspaceReviewPolicy = null,
        int? taskReviewHybridTimeoutMinutes = null,
        int? workspaceReviewHybridTimeoutMinutes = null,
        bool bypassPromotionBranch = false,
        WorkUnitExpectedOutputKind expectedOutputKind = WorkUnitExpectedOutputKind.FileChange,
        string? repositoryId = null,
        IReadOnlyList<FileReferenceV1>? referenceFiles = null,
        // Phase 16 — resolved by WorkUnitCommandService via IWorkspaceRegistryService, never
        // caller-supplied. Null keeps today's default ("workspace-default") for callers that
        // bypass WorkUnitCommandService (fan-out, steering, fork-from-node).
        string? workspaceId = null,
        // See WorkUnit's own fields of the same name — set only by IReconciliationAgentService.
        IReadOnlyList<string>? reconciliationSourceProposalIds = null,
        IReadOnlyList<string>? reconciliationTargetPaths = null,
        string? reconciliationSourceRef = null,
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
    ReviewPolicy? TaskReviewPolicy = null,
    ReviewPolicy? WorkspaceReviewPolicy = null,
    int? TaskReviewHybridTimeoutMinutes = null,
    int? WorkspaceReviewHybridTimeoutMinutes = null,
    bool BypassPromotionBranch = false,
    string? SeedFromBranchId = null,
    WorkUnitExpectedOutputKind? ExpectedOutputKind = null,
    // Slice 19 — references an already-registered IRepositoryRegistryService entry by id.
    // Resolved to a path by WorkUnitCommandService.CreateAsync, which takes priority over
    // RepositoryPath when both are given.
    string? RepositoryId = null,
    // Cross-repo file reference — see WorkUnit.ReferenceFiles. Each entry's RepositoryId is
    // validated against IRepositoryRegistryService by WorkUnitCommandService.CreateAsync.
    IReadOnlyList<FileReferenceV1>? ReferenceFiles = null,
    // See WorkUnit's own fields of the same name — set only by IReconciliationAgentService.
    IReadOnlyList<string>? ReconciliationSourceProposalIds = null,
    IReadOnlyList<string>? ReconciliationTargetPaths = null,
    string? ReconciliationSourceRef = null);

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

    // Un-cancel — the inverse of CancelAsync. Walks the same subtree shape (root + every
    // descendant), but only acts on members still Cancelled (a sibling that finished before the
    // cancel stays Merged/Completed and is left alone, same as CancelAsync already does). Leaf
    // members re-open their tasks and re-enqueue a worker; fan-out parents re-attempt
    // reconciliation via IMergeReconciliationService, reusing whatever TaskConflictRecord/child
    // state already exists instead of re-deriving anything. Throws InvalidOperationException if
    // workUnitId itself isn't Cancelled — nothing to requeue.
    //
    // The override* params are best-effort credential resupply (IAgentControlService.
    // ResupplyCredentialsAsync) for workUnitId itself before anything else runs — a cancel/requeue
    // cycle commonly spans a Host restart (that's often *why* the goal needed a human to look at
    // it), which wipes the in-memory IRuntimeCredentialCache the inline reviewer and re-enqueued
    // workers both depend on. Does not spawn a new orchestrator loop — see
    // ResupplyCredentialsAsync's own doc comment for why. Silently no-ops (same as today) if
    // nothing is resolvable and no overrides are supplied.
    Task<IReadOnlyList<WorkUnit>> RequeueAsync(
        string workUnitId,
        string? notes = null,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        string? overrideCredentialRef = null,
        CancellationToken cancellationToken = default);
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
    Task<string> CreateBranchAsync(string name, string? fromBranchId = null,
        IReadOnlyList<string>? fileScope = null, CancellationToken cancellationToken = default);

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

/// <summary>
/// Unified view of a runtime participant — either an in-process agent loop ("agent") or a
/// connected WebSocket peer ("peer"). Fields not applicable to a kind are null.
/// </summary>
public sealed record ParticipantDto(
    string Id,
    string Kind,
    string Status,
    string? WorkUnitId = null,
    string? CurrentActivity = null,
    string? PeerType = null);

public interface IStudioParticipantService
{
    Task<IReadOnlyList<ParticipantDto>> ListAsync(CancellationToken ct = default);
    Task StopAsync(string id, CancellationToken ct = default);
}

// ─── Track 7 — Projection Materialization ────────────────────────────────────

/// <summary>
/// Result of writing a work unit's branch files to a local filesystem target.
/// </summary>
public sealed record MaterializationResult(
    string WorkUnitId,
    string SnapshotId,
    string TargetKind,
    string TargetPath,
    int FileCount,
    long DurationMs,
    bool Succeeded,
    string? Error = null);

/// <summary>
/// A single file entry in a branch-level file diff.
/// Status: "Added" | "Removed" | "Modified" | "Unchanged"
/// </summary>
public sealed record FileDiffEntry(string RelativePath, string Status);

/// <summary>
/// Result of comparing files between two KnownGoodState snapshot branches.
/// </summary>
public sealed record KnownGoodDiffResult(
    string StateIdA,
    string StateIdB,
    IReadOnlyList<FileDiffEntry> Differences,
    int AddedCount,
    int RemovedCount,
    int ModifiedCount);

/// <summary>
/// Writes the current state of a work unit's branch to a named materialization target
/// (LocalFilesystem — the single canonical output; CI/CD/deploy happen outside Studio).
/// After writing, captures a ProjectionSnapshot and publishes ProjectionMaterializedEvent.
/// </summary>
public interface IProjectionMaterializer
{
    /// <summary>
    /// Materialize the branch files of <paramref name="workUnitId"/> to <paramref name="targetPath"/>
    /// (defaults to the configured SeedRepositoryPath when null).
    /// </summary>
    Task<MaterializationResult> MaterializeAsync(
        string workUnitId,
        string? targetPath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Materialize the filesystem from a KnownGoodState's immutable snapshot branch — no live
    /// branch state is changed, unlike CheckoutKnownGoodAsync which restores in-memory branch state.
    /// </summary>
    Task<MaterializationResult> MaterializeFromKnownGoodAsync(
        string stateId,
        string? targetPath = null,
        CancellationToken ct = default);

    /// <summary>
    /// Compare the files of two KnownGoodState snapshot branches, returning a file-level diff.
    /// Distinct from ProjectionComparison.Compute which is artifact-metadata-level.
    /// </summary>
    Task<KnownGoodDiffResult> DiffKnownGoodStatesAsync(
        string stateIdA,
        string stateIdB,
        CancellationToken ct = default);
}

/// <summary>
/// In-process pub/sub bus for domain events. Handlers are fire-and-forget from the
/// publisher's perspective; the bus catches and swallows handler exceptions individually
/// so one bad subscriber never blocks event delivery to others.
/// </summary>
public interface IParticipantEventBus
{
    void Publish(IDomainEvent domainEvent);
    /// <summary>Subscribe to events of a specific type. Dispose the returned token to unsubscribe.</summary>
    IDisposable Subscribe(string eventType, Func<IDomainEvent, Task> handler);
    /// <summary>Returns the most recent events (globally, all types), newest-last.</summary>
    IReadOnlyList<IDomainEvent> GetRecentEvents(int limit = 50);
    /// <summary>Returns all known event type names emitted by built-in participants.</summary>
    IReadOnlyList<string> GetRegisteredEventTypes();
}

public sealed record GoalDefaultCredentials(
    string Provider,
    string Model,
    string BaseUrl,
    // Never persisted — see GoalRoutingConfig, the safe subset of this shape that actually
    // gets written to IStudioNodeStore. ApiKey only ever lives in-memory (the live orchestrator
    // registry) or in IRuntimeCredentialCache, keyed by CredentialRef.
    string ApiKey,
    string? ProfileId,
    string? CredentialRef = null);

// The safe-to-persist projection of an orchestrator's registration — everything
// InMemoryAgentRuntimeService's in-memory-only _goalCredentialRegistrations needs to survive a Host
// restart, minus every ApiKey. Written to IStudioNodeStore at SpawnAsync("orchestrator", ...) time
// and rehydrated on startup, so GetAutoReviewProfileId/GetEnabledDomainAgents work immediately after
// a restart with no credential resupply needed at all; GetGoalDefaultCredentials/
// GetCredentialsForStage additionally need IRuntimeCredentialCache to have CredentialRef's entry.
public sealed record GoalRoutingConfig(
    string WorkUnitId,
    string Provider,
    string Model,
    string BaseUrl,
    string? ProfileId,
    string? AutoReviewProfileId,
    string? CredentialRef,
    IReadOnlyDictionary<PipelineStage, GoalDefaultCredentials>? StageCredentials = null,
    IReadOnlyList<string>? EnabledDomainAgents = null);

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
        IReadOnlyDictionary<PipelineStage, GoalDefaultCredentials>? stageCredentials = null,
        IReadOnlyList<string>? enabledDomainAgents = null,
        string? credentialRef = null,
        CancellationToken cancellationToken = default);

    // Runs one deterministic IGoalCoordinator convergence sweep for a goal/orchestrator-type work
    // unit — called automatically whenever a child work unit finishes (WorkSchedulerService.
    // ReleaseAsync's success path) so the goal advances. Since plans/orchestrator-pure-service.md
    // M2 this needs no credentials (there is no LLM loop to restart); the override* params only
    // (re)persist credentials into the Default-profile registry for later planner/child enqueues.
    // ensurePlanner additionally enqueues the planner when the goal has no plan, no children, and
    // no queue item — pass true only from goal start / manual recovery, never from automatic
    // sweeps (see IGoalCoordinator.ConvergeAsync).
    Task ReinvokeOrchestratorAsync(
        string workUnitId,
        string? sessionId = null,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        string? overrideCredentialRef = null,
        bool ensurePlanner = false,
        CancellationToken cancellationToken = default);

    // Requeue Goal's credential half — same resolve-and-persist logic ReinvokeOrchestratorAsync
    // uses (registration hot path, then rehydrated routing + IRuntimeCredentialCache, then the
    // override* params), but without spawning a new orchestrator loop. A requeued goal whose
    // in-flight work is already done (just needs one more reconcile/review pass) doesn't need an
    // ongoing planning loop — it just needs GetGoalDefaultCredentials/GetCredentialsForStage to
    // resolve again for whatever one-shot call (inline reviewer, a re-enqueued worker) needs them
    // next. Returns true if credentials are resolvable (and now persisted) after this call, false
    // if nothing was resolvable (same "no-op, caller can supply overrides" contract as reinvoke).
    Task<bool> ResupplyCredentialsAsync(
        string workUnitId,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        string? overrideCredentialRef = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns LLM credentials captured when an orchestrator was first spawned for a work unit.
    /// Used by fan-out to enqueue child workers with the same credentials.
    /// </summary>
    GoalDefaultCredentials? GetGoalDefaultCredentials(string workUnitId);

    /// <summary>
    /// Per-stage credential override captured at orchestrator spawn time (e.g. a different model
    /// for Plan vs Execute vs Review), or null if no override was configured for that stage —
    /// callers fall back to <see cref="GetGoalDefaultCredentials"/> in that case.
    /// </summary>
    GoalDefaultCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage);

    /// <summary>
    /// Profile ID for the automated reviewer pre-gate, captured at orchestrator spawn time.
    /// </summary>
    string? GetAutoReviewProfileId(string workUnitId);

    /// <summary>
    /// The orchestrator's own dispatch profile ID, captured at spawn time — routing data, not a
    /// credential, so unlike <see cref="GetGoalDefaultCredentials"/> this resolves purely from the
    /// rehydrated routing config and survives a restart with zero credential resupply needed. Lets
    /// a caller (e.g. a manual "Reinvoke Orchestrator" action) know which profile to resolve fresh
    /// credentials for without first needing a live ApiKey.
    /// </summary>
    string? GetGoalDefaultProfileId(string workUnitId);

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

    /// <summary>
    /// Registers a synchronous, non-scheduled agent run (e.g. InlineReviewerService's BeforeMerge-
    /// gate reviewer, which is awaited directly by its caller rather than dispatched through
    /// IWorkScheduler) into the same visibility registry SpawnAsync-driven agents use, for the
    /// duration of <paramref name="run"/>. The entry is registered "active" before <paramref
    /// name="run"/> starts and is guaranteed to be marked "stopped" (or "failed:...", rethrowing)
    /// before this method returns or throws — callers do not need their own try/finally. <paramref
    /// name="run"/> receives an activity-reporting callback equivalent to the one SpawnAsync's own
    /// loops use, for CurrentActivity visibility while it runs.
    /// </summary>
    Task<TResult> TrackInlineAgentAsync<TResult>(
        string agentId,
        string workUnitId,
        string? taskId,
        Func<Action<string?>, Task<TResult>> run,
        CancellationToken cancellationToken = default);
}

// plans/orchestrator-pure-service.md M2 — the deterministic coordinator that replaced the
// orchestrator LLM loop. Goal-level coordination (enqueue the planner, fan out from plans, run
// reconciliation/review sweeps, complete the goal) is code, not an agent: it holds no
// conversation, needs no profile of its own, and its convergence sweep needs no credentials at
// all — child enqueues resolve their own via IAgentControlService's Default-profile registry.
public interface IGoalCoordinator
{
    /// <summary>
    /// Kicks off a freshly registered goal (or an orchestrator-type work unit, e.g. a
    /// reconciliation unit): enqueues the planner with the goal's Plan-stage/Default-profile
    /// credentials, then runs one convergence sweep. Idempotent — a goal that already has a plan,
    /// children, or a pending scheduler item gets the sweep only.
    /// </summary>
    Task StartGoalAsync(string workUnitId, string? sessionId = null, CancellationToken ct = default);

    /// <summary>
    /// One idempotent convergence sweep: fan out from a recorded plan (the unit's own and any
    /// still-open orchestrator-type children's), enqueue ready dependents, attempt merge
    /// reconciliation, enqueue the automated reviewer, and complete the work unit once its
    /// reconciled proposal is approved/merged. <paramref name="ensurePlanner"/> additionally
    /// re-enqueues the planner when nothing exists yet (no plan, no children, no queue item) —
    /// only goal start and *manual* recovery pass true, so an automatic sweep after a planner
    /// that legitimately produced no plan can never re-enqueue planners in a loop.
    /// </summary>
    Task ConvergeAsync(
        string workUnitId, string? sessionId = null, bool ensurePlanner = false,
        CancellationToken ct = default);
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

    // WorkspacePathways (plans/pathways-workspace-history.md) — every artifact of one type,
    // workspace-wide, regardless of owning work unit. The query surface GetChainAsync
    // (per-work-unit) can't provide for OwnedByWorkUnitId:null artifacts like ExternalChangeset;
    // backed by the service's in-memory index so callers don't rescan and re-deserialize the
    // whole node store per request (the projection previously did exactly that, per poll).
    Task<IReadOnlyList<ArtifactRef>> GetByTypeAsync(ArtifactType type, CancellationToken ct = default);

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
    bool AwaitingResume,
    int? TimeoutSeconds = null,
    DateTimeOffset? TimeoutAt = null,
    string? TimeoutBehavior = null);

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
    // Never persisted (see [JsonIgnore]) — the live in-memory value is real for the lifetime of
    // this process (so same-process re-acquire/retry, e.g. FileLease park/resume, is unaffected),
    // but a Host restart's rehydrate deserializes this back as null. Dispatch resolves it from
    // IRuntimeCredentialCache via CredentialRef instead; if that also misses, the item parks as
    // AwaitingCredentials rather than silently persisting the secret to disk to survive restarts.
    [property: JsonIgnore] string? ApiKey,
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
    bool AwaitingFileLease = false,
    // Opaque cache key the client derived (e.g. its VS Code SecretStorage apiKeyRef) — never a
    // secret itself. Lets dispatch re-resolve ApiKey from IRuntimeCredentialCache after a restart.
    string? CredentialRef = null,
    // Set when dispatch needed real credentials (ApiKey null post-rehydrate) but
    // IRuntimeCredentialCache had no entry for CredentialRef yet. Cleared by SupplyCredentialsAsync
    // once a human/the extension resupplies them via the Resume flow.
    bool AwaitingCredentials = false);

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
        string? credentialRef = null,
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

    // Called by RunScheduledWorkerAsync when dispatch needs a real ApiKey (rehydrated item has
    // none) and IRuntimeCredentialCache also has nothing for the item's CredentialRef. Mirrors
    // MarkAwaitingFileLeaseAsync's "park in place" shape.
    Task MarkAwaitingCredentialsAsync(string workUnitId, CancellationToken ct = default);

    // Called from the Resume flow when the caller (typically the VS Code extension, re-reading its
    // own SecretStorage) resupplies live connection details. Populates IRuntimeCredentialCache under
    // the item's own CredentialRef (so any other parked item/orchestrator lookup sharing that ref
    // also unblocks), updates this item's in-memory connection fields, and clears AwaitingCredentials.
    Task SupplyCredentialsAsync(
        string workUnitId,
        string? provider,
        string? model,
        string? baseUrl,
        string? apiKey,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default);

    // Phase 8c — items flagged AwaitingResume on rehydrate (see ScheduledItem.AwaitingResume).
    Task<IReadOnlyList<ScheduledItem>> ListAwaitingResumeAsync(CancellationToken ct = default);

    Task ApproveResumeAsync(string workUnitId, CancellationToken ct = default);

    Task<int> ApproveResumeAllAsync(CancellationToken ct = default);

    // Unconditionally clears every park flag (AwaitingResume, AwaitingFileLease,
    // AwaitingCredentials) for one item, regardless of whether IFileLeaseService/
    // IRuntimeCredentialCache actually think it's still blocked. Exists because the two normal
    // "clear" paths (ClearAwaitingFileLeaseAsync via the lease release-and-advance hook,
    // SupplyCredentialsAsync via resupply) both require the underlying system to agree the block
    // is really gone — but the two are tracked independently, so a scoping change, a force-release
    // elsewhere, or any other drift between them can leave a scheduler item flagged parked with
    // nothing actually blocking it anymore (an "orphaned" park). Self-healing: if a real conflict
    // still exists, the resumed worker just re-attempts the write, gets denied, and re-parks
    // itself cleanly — this is never less safe than leaving a possibly-orphaned flag stuck forever.
    Task ForceResumeAsync(string workUnitId, CancellationToken ct = default);
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
        string? credentialRef = null,
        CancellationToken ct = default);

    Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default);
}

// Pure in-memory cache mapping an opaque client-supplied CredentialRef (e.g. a VS Code
// SecretStorage key reference) to the live LLM connection tuple it names. Never backed by
// IStudioNodeStore or any other durable store — that's the whole point: a raw ApiKey must never
// survive a Host restart on disk. Capture() is safe to call unconditionally at every ingress point
// that receives connection fields; it's a no-op unless both a ref and real values are present.
public sealed record LlmConnectionInfo(string Provider, string Model, string BaseUrl, string ApiKey);

public interface IRuntimeCredentialCache
{
    void Capture(string? credentialRef, string? provider, string? model, string? baseUrl, string? apiKey);

    LlmConnectionInfo? TryGet(string? credentialRef);
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
        int? timeoutSeconds = null,
        string? timeoutBehavior = null,
        string? defaultResponse = null,
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

/// <summary>
/// Goal-level pause/resume for external callers (MCP, REST). Coordinates stopping active agents,
/// updating session status, and re-enqueueing on resume. Does not affect the internal nm_v1_*
/// agent tool surface or McpToolDispatcher.
/// </summary>
public interface IGoalControlService
{
    Task<GoalNode> PauseAsync(string goalId, string? reason = null, string? pausedBy = null, CancellationToken ct = default);
    Task<GoalNode> ResumeAsync(string goalId, string? steering = null, string? resumedBy = null, string? profileId = null, CancellationToken ct = default);
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
        // Phase A — artifact IDs this record supersedes. Required (non-empty) when type is
        // Supersession; optional on Decision/Constraint/Research when the new record also
        // explicitly retires an ancestor.
        IReadOnlyList<string>? supersedes = null,
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
// plans/phase-d-implementation.md D2 — Provider is additive and optional: null (every existing
// producer of this record — FanOutService's deterministic FileScope tier, LlmProfileSelectionService's
// heuristic/LLM tiers) means "no executor-routing opinion, use whatever credentials the caller
// already resolved," so extending the record here changes nothing about their behavior. Only
// IPlannerSelectionService (below) ever sets it, carrying the CLI executor's ProviderKey (e.g.
// "claude-cli") the same way every other executor-routing decision already rides the provider
// channel (see IHarnessExecutorResolver.ResolveForProvider's doc comment) — null still means
// "no override" there too (native, or selection disabled/heuristic).
public sealed record ProfileSelectionResult(string ProfileId, string Reason, bool UsedLlm, string? Provider = null);

public interface IProfileSelectionService
{
    /// <summary>
    /// Picks the agent profile for a child work unit. Returns the heuristic default ("worker")
    /// immediately when LLM selection is disabled, credentials are unavailable, or the LLM call
    /// fails/times out/returns an unknown profile id.
    /// </summary>
    Task<ProfileSelectionResult> SelectProfileAsync(
        WorkUnit childUnit,
        GoalDefaultCredentials? credentials,
        CancellationToken ct = default);
}

// plans/phase-d-implementation.md D2 — "who plans this goal" executor routing. Parallel to
// IProfileSelectionService but for the Plan stage: picks which profile (and, via
// ProfileSelectionResult.Provider, which executor) authors a goal's decomposition. Callers must
// only consult this when the role's Agent Topology assignment for PipelineStage.Plan is
// auto/unset (see OrchestratorAgentLoop.InjectSpawnCredentialsAsync) — an explicit per-stage
// Model Profile assignment is the override and must never be second-guessed by this service.
public interface IPlannerSelectionService
{
    /// <summary>
    /// Picks the agent profile (and optionally the executor provider) that should plan/decompose
    /// <paramref name="goalUnit"/>. Returns the heuristic default ("planner", no provider
    /// override) when selection is disabled (WorkspaceOptions.UsePlannerExecutorSelection),
    /// no Plan-stage candidates are registered, credentials are unavailable, or the LLM tier
    /// fails/times out/returns an unknown profile id.
    /// </summary>
    Task<ProfileSelectionResult> SelectPlannerAsync(
        WorkUnit goalUnit,
        GoalDefaultCredentials? credentials,
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

// Phase 2 item 1 (plans/orchestrator-reliability-and-observability.md) — a soft, alert-only cap
// on a goal's total token usage/wall-clock duration. Deliberately never stops or interrupts
// in-flight work itself — auto-stopping active agent work is a real, hard-to-reverse action a
// human should decide on, not something this triggers automatically. Computed on demand (no
// background poller, no persisted "already alerted" state to go stale) by summing
// ConversationLogEntry token counts across a goal's entire work-unit subtree.
public interface IGoalGuardrailService
{
    Task<GoalGuardrailStatus?> GetStatusAsync(string goalWorkUnitId, CancellationToken cancellationToken = default);

    // Every non-terminal top-level goal (ParentWorkUnitId is null; Status not in
    // {Completed, Merged, Cancelled, Failed}) — what the VS Code dashboard polls to badge any
    // goal that's crossed its cap, without needing to know which goals exist up front.
    Task<IReadOnlyList<GoalGuardrailStatus>> GetActiveGoalStatusesAsync(CancellationToken cancellationToken = default);
}

public sealed record GoalGuardrailStatus(
    string WorkUnitId,
    long TotalTokens,
    long? MaxGoalTokens,
    bool TokensExceeded,
    double ElapsedMinutes,
    int? MaxGoalDurationMinutes,
    bool DurationExceeded);

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
    // Leases are scoped per root goal (the top-level WorkUnit with no parent), resolved internally
    // from workUnitId — an unrelated goal touching the same relative file path in the shared
    // repository must never block this one; that's what the merge/reconciliation flow is for, not
    // proactive cross-goal locking. Two work units only ever contend here if they share a root.
    Task<(bool Granted, string? HolderWorkUnitId)> TryAcquireOrEnqueueAsync(
        string workUnitId, string path, CancellationToken ct = default);

    // Clears the current holder for path and promotes the next FIFO waiter (if any) to holder,
    // returning its WorkUnitId so the caller can copy the merged file into its branch and resume
    // it. workUnitId is the unit whose merge just landed — used only to resolve which root goal's
    // scoped lease on path to release (see TryAcquireOrEnqueueAsync's own doc comment).
    Task<string?> ReleaseAndAdvanceAsync(string workUnitId, string path, CancellationToken ct = default);

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
    // Phase 11: fileScope narrows materialization to matching paths only (+ project structure files).
    // Null/empty = full materialization (existing behavior).
    Task InitBranchAsync(string branchId, string? seedFromBranchId = null,
        IReadOnlyList<string>? fileScope = null, CancellationToken ct = default);
    Task<string?> ReadAsync(string branchId, string relativePath, CancellationToken ct = default);
    Task WriteAsync(string branchId, string relativePath, string content, CancellationToken ct = default);
    Task DeleteAsync(string branchId, string relativePath, CancellationToken ct = default);
    Task<bool> ExistsAsync(string branchId, string relativePath, CancellationToken ct = default);
    // pattern: optional case-insensitive filter against each result's relative path, supporting
    // * (any run of characters) and ? (any single character) wildcards — a plain filename like
    // "WeatherForecastController.cs" matches as a substring, so callers can find a specific file by
    // name across the whole branch (subPath omitted) without already knowing its directory.
    Task<IReadOnlyList<string>> ListAsync(string branchId, string? subPath = null, string? pattern = null, CancellationToken ct = default);

    // plans/harness-hosting-architecture.md Phase A.5 — ListAsync's dot-hidden rule (any
    // dot-prefixed path segment is treated as hidden) is exactly what keeps `.workspace/` out of
    // generic content browsing/diff, but it also means ListAsync can never see inside
    // `.workspace/` itself. This is the read-back counterpart: lists files under subPath
    // ignoring the dot-hidden rule (only WorkspacePathFilter.IgnoredDirNames' genuine junk dirs —
    // node_modules/bin/obj/.git/… — are excluded), the same dotfile-inclusive semantics
    // FileSystemWorkspaceService already uses for branch seeding. Used by
    // WorkspaceContractService to read back harness-written `.workspace/decisions` and
    // `.workspace/inbox` entries.
    Task<IReadOnlyList<string>> ListIncludingDotfilesAsync(
        string branchId, string subPath, CancellationToken ct = default);

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

    // Phase 11 on-demand fetch: pulls a single path from the latest repository snapshot into the
    // branch directory. Returns true if the path was found in the snapshot and materialized,
    // false if the path does not exist in the snapshot (genuinely new/absent file).
    // No-ops gracefully (returns false) when CAS or snapshot is unavailable.
    Task<bool> MaterializeFileAsync(string branchId, string path, CancellationToken ct = default);

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

// Repository virtualization — Phase 3.
// Immutable op log for file transitions. EmitAsync persists each op durably and indexes
// it for fast path queries. Snapshot validation (OldBlobId consistency) is deferred to
// Phase 9 once snapshots exist.
public interface IRepositoryOpService
{
    Task EmitAsync(RepositoryOperation op, CancellationToken ct = default);

    Task<IReadOnlyList<RepositoryOperation>> GetRecentOpsForPathsAsync(
        string repositoryId, IReadOnlyList<string> paths, int limit = 5, CancellationToken ct = default);

    // Phase 6 — all ops for a repository after the given timestamp, chronologically ordered.
    // Used by snapshot compaction to replay ops onto a base snapshot's TreeEntries.
    Task<IReadOnlyList<RepositoryOperation>> GetOpsSinceAsync(
        string repositoryId, DateTimeOffset since, CancellationToken ct = default);
}

// Phase 7 — reconstructs workspace directories from a snapshot's TreeEntries + CAS.
// Enables safe eviction of any workspace directory: the materializer can always rebuild it
// from the node store + blob store without touching the seed repository.
public interface IMaterializationEngine
{
    // Reconstruct targetPath from snapshot.TreeEntries + CAS. Files already on disk whose
    // Blake3 hash matches the expected blobId are skipped (no CAS fetch). Files not in the
    // snapshot (within fileScope) are deleted. Returns count of files written.
    // Returns 0 without error when snapshot.TreeEntries is null (pre-Phase-2 node).
    Task<int> MaterializeAsync(
        RepositorySnapshot snapshot,
        string targetPath,
        IReadOnlyList<string>? fileScope = null,
        CancellationToken ct = default);

    // Incremental update: diff two snapshots and only touch changed/added/removed paths.
    // Used by Phase 8 cache manager to refresh a workspace without full reconstruction.
    Task<int> RematerializeAsync(
        RepositorySnapshot snapshot,
        RepositorySnapshot previousSnapshot,
        string targetPath,
        IReadOnlyList<string>? fileScope = null,
        CancellationToken ct = default);
}

// Phase 2 — snapshot checkpoint service for the repository op log. One snapshot per goal cycle
// (created by between-run sync) serves as the replay base; replaying 10–30 ops from the latest
// snapshot is intentionally fast. SnapshotOnWorkUnitCompletion (Phase 7) and compaction (Phase 6)
// build on this foundation.
public interface IRepositorySnapshotService
{
    // Latest snapshot for this repository, or null if none exists yet.
    Task<RepositorySnapshot?> GetLatestAsync(string repositoryId, CancellationToken ct = default);

    // Phase 14 — look up a specific snapshot by ID. Returns null if not found.
    Task<RepositorySnapshot?> GetAsync(string snapshotId, CancellationToken ct = default);

    // Create a new snapshot. Increments Generation, computes TreeHash, writes to node store.
    Task<RepositorySnapshot> CreateAsync(
        string repositoryId,
        IReadOnlyDictionary<string, string> treeEntries,
        string? baseSnapshotId = null,
        string? workUnitId = null,
        string? gitCommit = null,
        string? source = null,
        CancellationToken ct = default);

    // Phase 6 — mid-goal compaction: if ops accumulated since the last snapshot meet or exceed
    // the threshold, replay them onto the snapshot's TreeEntries and write a new "Compaction"
    // snapshot. Returns the new snapshot if one was created, null otherwise (including when
    // threshold is null or the op count is below it).
    Task<RepositorySnapshot?> ConsiderCompactionAsync(
        string repositoryId, int? threshold, CancellationToken ct = default);
}

// Phase 5 — one-time CAS bootstrap for a repository. Walk every importable file in the repo root,
// write blobs to CAS, emit Import RepositoryOps, and record a Generation-0 RepositorySnapshot node.
// No-op if the bootstrap snapshot already exists (checked via node store on first call, then cached).
// No-op if blobStore or repoOpService is unavailable (e.g. in-memory test environments without CAS).
public interface IRepositoryImportService
{
    // Bootstrap-or-skip: only ever runs the CAS walk/diff once per repositoryId per process
    // (gated by an in-memory "already bootstrapped" set) — right for triggers where a one-time
    // seed is all that's needed (GoalCreation, StartupRecovery).
    Task EnsureBootstrappedAsync(string repositoryId, string repositoryPath, CancellationToken ct = default);

    // Always re-runs the Case 1/Case 2 diff+snapshot logic, regardless of whether this
    // repository was already bootstrapped — for triggers whose whole point is "check again
    // right now" (PostMergeWriteBack, ManualRefresh). Without this, EnsureBootstrappedAsync's
    // one-time gate means RepositorySnapshot never advances again after a repo's first goal
    // creation, no matter how many merges land or how many times a resync is requested.
    Task ForceSyncAsync(string repositoryId, string repositoryPath, CancellationToken ct = default);
}

// Phase 9 — structural conflict detection. A conflict is two RepositoryOps that share the same
// OldBlobId (started from the same parent blob) but produced different NewBlobIds — a DAG fork.
// Detection happens at op-emit time; the op service calls RecordAsync when a fork is found.
// Resolution (Phase 10) calls MarkResolvedAsync after producing a ConflictResolutionOp.
public interface IConflictService
{
    // Persist a detected conflict. Idempotent on ConflictId.
    Task<RepositoryConflict> RecordAsync(RepositoryConflict conflict, CancellationToken ct = default);

    // All open (unresolved, un-dismissed) conflicts for a repository.
    Task<IReadOnlyList<RepositoryConflict>> GetActiveAsync(string repositoryId, CancellationToken ct = default);

    Task<RepositoryConflict?> GetAsync(string conflictId, CancellationToken ct = default);

    // Human or UI dismissal — removes from active list without resolution.
    Task<RepositoryConflict?> DismissAsync(string conflictId, CancellationToken ct = default);

    // Phase 10 hook — called by the merge resolution path once a ConflictResolutionOp lands.
    Task<RepositoryConflict?> MarkResolvedAsync(string conflictId, string resolutionOpId, CancellationToken ct = default);
}

// Phase 8 — workspace cache management. Branch workspace directories are ephemeral cache entries;
// any evicted directory can be reconstructed from the latest repository snapshot + CAS.
// Safe eviction invariant: a non-cancelled work unit's branch dir may only be deleted if a
// repository snapshot with TreeEntries exists (guarantees the materializer can rebuild it).
public interface IWorkspaceCacheManager
{
    // Reconstruct a work unit's branch directory from the repository's latest snapshot + CAS.
    // Returns false if no snapshot with TreeEntries exists or the work unit is not found.
    Task<bool> MaterializeAsync(string workUnitId, CancellationToken ct = default);

    // Delete a work unit's branch workspace directory.
    // For Cancelled work units: always succeeds (files were never merged, no recovery needed).
    // For Completed/Merged work units: only evicts when snapshot.CreatedAt > wu.UpdatedAt,
    // ensuring the between-run sync captured those changes before the directory is removed.
    // Returns false when the invariant is violated (would lose work).
    Task<bool> EvictAsync(string workUnitId, CancellationToken ct = default);

    // Scan all work units and evict branch directories for terminal work units that satisfy
    // the safe eviction invariant. Failed/DeadLettered dirs are preserved for inspection.
    // Returns count of directories evicted.
    Task<int> EvictOrphanedAsync(CancellationToken ct = default);

    // Returns all blob hashes currently referenced by any repository snapshot or pending op.
    // Used by the host-layer blob GC coordinator (FileBlobGcCoordinator) to determine which
    // blobs in the CAS store are safe to tombstone/delete.
    Task<IReadOnlySet<string>> GetLiveBlobHashesAsync(CancellationToken ct = default);
}

// Phase 10 — pluggable merge strategy. Strategies are tried in registration order by
// IConflictResolutionService; each returns Success=false to hand off to the next.
public interface IMergeStrategy
{
    string Name { get; }
    Task<MergeStrategyResult> MergeAsync(MergeContext context, CancellationToken ct = default);
}

// Phase 10 — LLM-backed merge content generation. Defined in Core so Merge project can
// depend on it; implemented in AgentRuntime where LlmClient lives.
public interface ILlmMergeProvider
{
    Task<string?> MergeAsync(MergeContext context, CancellationToken ct = default);
}

// Phase 10 — syntax validation for the merged output. Defined in Core; implemented in
// Storage (which already has Roslyn) so the AstMergeStrategy in Merge can inject it.
public interface ISourceValidator
{
    bool IsValidSyntax(string content, string path);
}

// Phase 10 — orchestrates the strategy chain. Reads blob content, tries strategies in
// order, emits a RepositoryOperation for the merged result, marks the conflict Resolved.
public interface IConflictResolutionService
{
    // preferredStrategy: null = auto (try all in order), or strategy Name to try only that one.
    // llmCredentials: required only when the LLM strategy will run (auto or preferredStrategy="llm").
    Task<ConflictResolutionResult> ResolveAsync(
        string conflictId, string? preferredStrategy = null,
        LlmMergeCredentials? llmCredentials = null, CancellationToken ct = default);
}

// Phase 11.5 — co-modification frequency analysis over the RepositoryOp log.
public interface ICoModService
{
    // Recompute pairwise co-modification frequencies for all work units in the repository,
    // persist results as CoModPatternV1 nodes, and return the full pattern set.
    Task<IReadOnlyList<CoModificationPattern>> ComputeAsync(string repositoryId, CancellationToken ct = default);

    // Return the last-computed pattern set without recomputing.
    Task<IReadOnlyList<CoModificationPattern>> GetAsync(string repositoryId, CancellationToken ct = default);

    // Return patterns where PathA or PathB matches any of the provided prefix-expanded paths
    // at or above minConfidence. Paths here are exact file paths (callers must expand globs).
    Task<IReadOnlyList<CoModificationPattern>> GetForPathsAsync(
        string repositoryId, IReadOnlyList<string> paths,
        double minConfidence = 0.6, CancellationToken ct = default);
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

// plans/harness-hosting-architecture.md Phase A.2.2 — assembly is a service, not a projection.
// Consumes the EngineeringState projection plus work-unit/review-policy state and emits the
// Workspace Contract (docs/contracts/workspace-contract-v1.md); does not itself compute
// projections. RenderEngineeringStateMarkdownAsync is its own method (not folded into
// MaterializeAsync) because the native loop's kickoff injection (Phase A.5) reuses exactly this
// rendering — single source of markdown, never hand-duplicated.
public interface IWorkspaceContractService
{
    Task<WorkspaceContractBundle> AssembleAsync(string workUnitId, CancellationToken ct = default);

    /// <summary>Writes `.workspace/*.json` + derived `.md` siblings into the work unit's branch
    /// workdir via IFileWorkspaceService. Deterministic — the same runtime state materializes
    /// byte-identical content (contract principle WC-2).</summary>
    Task MaterializeAsync(string workUnitId, CancellationToken ct = default);

    Task<string> RenderEngineeringStateMarkdownAsync(string workUnitId, CancellationToken ct = default);

    /// <summary>
    /// Parses `.workspace/decisions/*` (JSON or markdown-with-frontmatter, normalized to
    /// WorkspaceContractDecisionEntry) and records each as an artifact via IArtifactLineageService
    /// directly (bypassing IArtifactCommandService, which always mints a fresh ArtifactId).
    /// Idempotent: each entry's ArtifactId is deterministically derived from its file number, so
    /// IArtifactLineageService.RecordAsync's existing "second record with the same ArtifactId is a
    /// no-op" behavior makes re-harvesting after a crash or retry safe without new dedup logic.
    /// </summary>
    Task<IReadOnlyList<ArtifactRef>> HarvestDecisionsAsync(string workUnitId, CancellationToken ct = default);

    /// <summary>
    /// Parses `.workspace/inbox/*` (one blocking question per numbered file) — the harness→runtime
    /// half of the pause-and-wait flow (plan's resolved "pause-and-wait semantics per executor"
    /// decision: external executor v1 pauses at run granularity, detected at harvest). Returns the
    /// parsed entries; the caller (Phase B.3 harvest) is responsible for turning each into an
    /// IClarificationCommandService.RequestAsync call — this method only reads and parses.
    /// </summary>
    Task<IReadOnlyList<WorkspaceContractInboxEntry>> HarvestInboxAsync(string workUnitId, CancellationToken ct = default);
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

    Task<string?> GetRunOutputAsync(string branchId, string? rootPath = null, CancellationToken ct = default);
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
    // Split from a single ReviewPolicy field. The experiment's parent container work unit has no
    // ParentWorkUnitId (it's never enqueued/executed, but is structurally "top-level"), so it
    // carries WorkspaceReviewPolicy; the fork children are true children, so they carry
    // TaskReviewPolicy — same split as a fresh top-level goal (WorkUnitCreateCommand).
    ReviewPolicy? TaskReviewPolicy = null,
    ReviewPolicy? WorkspaceReviewPolicy = null,
    int? TaskReviewHybridTimeoutMinutes = null,
    int? WorkspaceReviewHybridTimeoutMinutes = null,
    string? SessionId = null,
    // Without one of these, forks never get their own RepositoryId, and
    // WorkspaceReviewScope.AppliesToRealRepo (NodalMerge.Studio.Merge) only allows disk
    // write-back for a top-level goal or a work unit explicitly linked to its own repo — a fork
    // (always has a ParentWorkUnitId) needs the latter to ever apply into the real repo.
    string? RepositoryPath = null,
    string? RepositoryId = null);

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

public sealed record ConvergenceResult(
    string ParentWorkUnitId,
    string WinnerWorkUnitId,
    IReadOnlyList<string> RejectedWorkUnitIds,
    string? Rationale);

public interface IExperimentService
{
    Task<ExperimentResult> CreateAsync(ExperimentSpec spec, CancellationToken ct = default);
    Task<ExperimentNode?> GetAsync(string experimentId, CancellationToken ct = default);
    Task<IReadOnlyList<ExperimentNode>> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Converges an experiment: approves the winner's latest proposal, rejects every other
    /// sibling's non-terminal latest proposal, writes a DecisionNode per sibling (Accepted/
    /// Rejected), and transitions each sibling's HypothesisNode status (Converged/Rejected).
    /// </summary>
    Task<ConvergenceResult> ConvergeAsync(
        string parentWorkUnitId, string winnerWorkUnitId, string? rationale, CancellationToken ct = default);
}

// Phase 14 — Git as Import/Export Adapter.
// Import: walks the git tree at a specific commit (or HEAD), writes blobs to CAS, emits
//   Import RepositoryOps, and returns the SnapshotId of the resulting RepositorySnapshot.
// Export: materializes a RepositorySnapshot to the target working tree. Whether an actual git
//   commit is created depends on WorkspaceOptions.AllowAgentGitCommits (default: false).
public interface IGitAdapter
{
    // gitRepoPath: the local path to the .git directory or working tree root.
    // commitSha: the commit to import; null = HEAD.
    // repositoryId: the studio repository ID to associate ops and the snapshot with.
    // Returns the SnapshotId created from the import.
    Task<string> ImportAsync(
        string gitRepoPath, string? commitSha, string repositoryId, CancellationToken ct = default);

    // Materializes a snapshot to targetGitRepoPath. When AllowAgentGitCommits = false (default),
    // files are written to disk but no git commit is created (CommitSha = null on result).
    // When true, a commit is created on branchName and CommitSha is set.
    // When AllowAgentGitPush is also true, shells out `git push origin {branchName}`.
    Task<GitExportResult> ExportAsync(
        string repositoryId, string? snapshotId, string targetGitRepoPath,
        string branchName, CancellationToken ct = default);

    // Create a git branch in an existing local repository. fromRef defaults to HEAD.
    // Returns the full SHA of the commit the new branch points to.
    Task<string> CreateGitBranchAsync(
        string gitRepoPath, string branchName, string? fromRef = null,
        bool checkout = false, CancellationToken ct = default);
}

public sealed record GitExportResult(
    string RepositoryId,
    string SnapshotId,
    string TargetPath,
    string BranchName,
    bool Committed,
    string? CommitSha,
    bool Pushed = false,
    string? PushOutput = null,
    string? Message = null);
