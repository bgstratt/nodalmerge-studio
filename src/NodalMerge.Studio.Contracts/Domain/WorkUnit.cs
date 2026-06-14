namespace NodalMerge.Studio.Contracts.Domain;

public enum WorkUnitStatus
{
    Created,
    Active,
    Waiting,
    Completed,
    Failed,
    Cancelled
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
    IReadOnlyDictionary<string, string>? Metadata);

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
            (_, WorkUnitStatus.Cancelled) when from is not WorkUnitStatus.Completed => true,
            _ => false
        };
}
