namespace NodalMerge.Studio.Contracts.Domain;

public enum TaskStatus
{
    Open,
    InProgress,
    Blocked,
    Completed,
    Cancelled
}

/// <summary>
/// Actionable work intent. Tasks MUST NOT contain DAG node references.
/// Domain is a free-form routing hint (e.g. "docs", "code", "demo") used
/// by the orchestrator to match tasks to agent profiles.
/// </summary>
public sealed record StudioTask(
    string TaskId,
    string WorkUnitId,
    string Title,
    string Description,
    TaskStatus Status,
    string? Assignee,
    int Priority,
    string? Domain = null);

public static class TaskTransitions
{
    public static bool CanTransition(TaskStatus from, TaskStatus to) =>
        (from, to) switch
        {
            (TaskStatus.Open, TaskStatus.InProgress) => true,
            (TaskStatus.InProgress, TaskStatus.Blocked) => true,
            (TaskStatus.Blocked, TaskStatus.InProgress) => true,
            (TaskStatus.InProgress, TaskStatus.Completed) => true,
            (_, TaskStatus.Cancelled) when from is not (TaskStatus.Completed or TaskStatus.Cancelled) => true,
            _ => false
        };
}
