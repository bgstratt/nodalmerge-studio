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
    string? Domain = null,
    // Slice 6.3a — denormalized at creation from the owning WorkUnit's own RepositoryId (a task
    // always has exactly one WorkUnitId, so this is a direct copy, never a chain walk). Null when
    // the work unit itself has no resolvable RepositoryId, or the task predates 6.3a.
    string? RepositoryId = null);

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
