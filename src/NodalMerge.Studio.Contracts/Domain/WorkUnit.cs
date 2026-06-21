namespace NodalMerge.Studio.Contracts.Domain;

public enum WorkUnitStatus
{
    Created,
    Active,
    Waiting,
    Completed,
    Failed,
    Cancelled,

    // Phase 4 slice 11a — queue-driven pipeline states. Additive: Active/Waiting/Completed/Failed
    // remain in use by the legacy direct-spawn path (IAgentControlService.SpawnAsync("worker",...)),
    // which never goes through WorkSchedulerService and so never reaches these. Planned and Rejected
    // are deliberately omitted — no planner stage or rejection path produces them yet.
    Queued,
    Executing,
    Proposed,
    Reviewing,
    Merged,
    DeadLettered,
    Retrying
}

// What a completed task on this work unit should produce, used by the automated reviewer (and
// optionally MergeCommandService, behind WorkspaceOptions.EnforceExpectedOutputKind) to tell a
// proposal that only describes work apart from one that actually did it. FileChange is the
// default — most worker tasks modify files. KnowledgeArtifact covers tasks satisfied by recording
// a Research/Decision/Constraint artifact (nm.v1.artifact.record) instead, e.g. pure research.
// Either disables the check for tasks where both are valid outcomes.
public enum WorkUnitExpectedOutputKind
{
    FileChange,
    KnowledgeArtifact,
    Either
}

public sealed record WorkUnit(
    string WorkUnitId,
    string Goal,
    string BranchId,
    WorkUnitStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string Owner,
    string? AssignedAgent,
    string? SuccessCriteria,
    IReadOnlyDictionary<string, string>? Metadata,
    string? ParentWorkUnitId,
    IReadOnlyList<string> DependsOn,
    IReadOnlyList<string> FileScope,
    // Phase 4 completion pass — typed state, promoted out of the Metadata grab-bag. Metadata
    // remains for genuine ad-hoc/future use; these fields are for state with known shape and
    // multiple call sites that previously round-tripped through string keys.
    PipelineStage? CurrentStage = null,
    WorkUnitExecutionInfo? ExecutionInfo = null,
    WorkUnitFanOutInfo? FanOutInfo = null,
    string? BranchedFromProposalId = null,
    HypothesisForkType? ForkType = null,
    ReviewPolicy ReviewPolicy = ReviewPolicy.HumanRequired,
    // Slice 21c — per-work-unit override: when true, applies always target the proposal's
    // TargetBranch directly even if WorkspaceOptions.UsePromotionBranch is on session-wide.
    bool BypassPromotionBranch = false,
    WorkUnitExpectedOutputKind ExpectedOutputKind = WorkUnitExpectedOutputKind.FileChange);

/// <summary>Failure/rejection counters, previously stored as parsed strings in Metadata.</summary>
public sealed record WorkUnitExecutionInfo(
    int FailureAttemptCount,
    int AutomatedReviewRejectionCount,
    int HumanReviewRejectionCount = 0);

/// <summary>Fan-out lineage: which plan slice this work unit fulfills and which branch it was seeded from.</summary>
// Slice 14b — BlockedReason is set when a BeforeEnqueue policy rule rejects this slice (e.g.
// NonOverlappingFileScopeRule) and cleared the next time it enqueues successfully. The work unit
// stays Created while blocked, so a later fan-out call retries it automatically once the
// conflicting sibling finishes — this field is purely the human-readable "why," not authoritative
// state.
public sealed record WorkUnitFanOutInfo(string? SliceId, string? SeedFromBranchId, string? BlockedReason = null);

public static class WorkUnitTransitions
{
    public static bool CanTransition(WorkUnitStatus from, WorkUnitStatus to) =>
        (from, to) switch
        {
            (WorkUnitStatus.Created, WorkUnitStatus.Active) => true,
            (WorkUnitStatus.Active, WorkUnitStatus.Waiting) => true,
            (WorkUnitStatus.Waiting, WorkUnitStatus.Active) => true,
            (WorkUnitStatus.Active, WorkUnitStatus.Completed) => true,
            (WorkUnitStatus.Active, WorkUnitStatus.Failed) => true,
            (WorkUnitStatus.Waiting, WorkUnitStatus.Failed) => true,

            // Phase 4 slice 11a — queue-driven pipeline.
            (WorkUnitStatus.Created, WorkUnitStatus.Queued) => true,
            (WorkUnitStatus.Queued, WorkUnitStatus.Executing) => true,
            (WorkUnitStatus.Executing, WorkUnitStatus.Proposed) => true,
            (WorkUnitStatus.Executing, WorkUnitStatus.Retrying) => true,
            (WorkUnitStatus.Retrying, WorkUnitStatus.Executing) => true,
            (WorkUnitStatus.Executing, WorkUnitStatus.DeadLettered) => true,
            (WorkUnitStatus.Retrying, WorkUnitStatus.DeadLettered) => true,
            (WorkUnitStatus.DeadLettered, WorkUnitStatus.Retrying) => true,
            (WorkUnitStatus.Proposed, WorkUnitStatus.Reviewing) => true,
            // A fan-out parent is the orchestrator's own work unit, spawned via the legacy
            // direct-spawn path (IAgentControlService.SpawnAsync("orchestrator", ...)) — it never
            // goes through Queued/Executing/Proposed itself, so it's still Created (or, if an agent
            // ever called workunit.update with assignedAgent, Active) when the merger detects a
            // conflict among its children and needs to flag it for human attention.
            (WorkUnitStatus.Created, WorkUnitStatus.Reviewing) => true,
            (WorkUnitStatus.Active, WorkUnitStatus.Reviewing) => true,
            (WorkUnitStatus.Proposed, WorkUnitStatus.Merged) => true,
            (WorkUnitStatus.Proposed, WorkUnitStatus.Queued) => true,
            (WorkUnitStatus.Reviewing, WorkUnitStatus.Merged) => true,
            (WorkUnitStatus.Reviewing, WorkUnitStatus.Executing) => true,

            (_, WorkUnitStatus.Cancelled) when from is not WorkUnitStatus.Completed and not WorkUnitStatus.Merged => true,
            _ => false
        };
}
