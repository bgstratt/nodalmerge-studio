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
    IReadOnlyList<string> FileScope);

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
            (WorkUnitStatus.Proposed, WorkUnitStatus.Merged) => true,
            (WorkUnitStatus.Proposed, WorkUnitStatus.Queued) => true,
            (WorkUnitStatus.Reviewing, WorkUnitStatus.Merged) => true,
            (WorkUnitStatus.Reviewing, WorkUnitStatus.Executing) => true,

            (_, WorkUnitStatus.Cancelled) when from is not WorkUnitStatus.Completed and not WorkUnitStatus.Merged => true,
            _ => false
        };
}
