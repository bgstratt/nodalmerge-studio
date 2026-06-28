using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Contracts.Domain;

/// <summary>
/// Marker interface for all domain events published through IParticipantEventBus.
/// </summary>
public interface IDomainEvent
{
    string EventType { get; }
    DateTimeOffset OccurredAt { get; }
    /// <summary>WorkUnit this event is scoped to, or null for system-wide events.</summary>
    string? WorkUnitId { get; }
}

// ─── Work unit lifecycle ───────────────────────────────────────────────────────

public sealed record WorkUnitStatusChangedEvent(
    string WorkUnitId,
    WorkUnitStatus PreviousStatus,
    WorkUnitStatus NewStatus,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "WorkUnitStatusChanged";
    string? IDomainEvent.WorkUnitId => WorkUnitId;
}

// ─── Merge lifecycle ──────────────────────────────────────────────────────────

public sealed record ProposalCreatedEvent(
    string ProposalId,
    string? WorkUnitId,
    string SourceBranch,
    string TargetBranch,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "ProposalCreated";
}

public sealed record ReviewCompletedEvent(
    string ProposalId,
    string? WorkUnitId,
    string Decision,
    bool Automated,
    string? ReviewerAgentId,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "ReviewCompleted";
}

public sealed record MergeAcceptedEvent(
    string ProposalId,
    string? WorkUnitId,
    string SourceBranch,
    string TargetBranch,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "MergeAccepted";
}

// ─── Projection materialization (published by Track 7) ───────────────────────

public sealed record ProjectionMaterializedEvent(
    string ProjectionId,
    string TargetKind,
    int FileCount,
    long DurationMs,
    string? WorkUnitId,
    DateTimeOffset OccurredAt) : IDomainEvent
{
    public string EventType => "ProjectionMaterialized";
}

// ─── Participant metadata ─────────────────────────────────────────────────────

/// <summary>
/// Declares what a participant observes and publishes.
/// reasoning = hosted in Studio (AgentProfile-backed); observer = external package.
/// </summary>
public sealed record ParticipantDefinition(
    string Kind,
    string Name,
    IReadOnlyList<string> Subscribes,
    IReadOnlyList<string> Publishes,
    string? Description = null);

public static class ParticipantKinds
{
    public const string Reasoning = "reasoning";
    public const string Observer  = "observer";
}
