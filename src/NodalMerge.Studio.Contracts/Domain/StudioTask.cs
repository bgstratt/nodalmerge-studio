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
/// </summary>
public sealed record StudioTask(
    string TaskId,
    string WorkUnitId,
    string Title,
    string Description,
    TaskStatus Status,
    string? Assignee,
    int Priority);
