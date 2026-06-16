using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class StateReconstructionService : IStateReconstructionService
{
    private readonly IExecutionEventStream _events;

    public StateReconstructionService(IExecutionEventStream events) => _events = events;

    public async Task<SessionStateSnapshot> GetStateAtAsync(
        string sessionId, string upToEventId, CancellationToken ct = default)
    {
        var all = await _events.GetSessionEventsAsync(sessionId, ct: ct).ConfigureAwait(false);

        var boundaryIndex = -1;
        for (var i = 0; i < all.Count; i++)
        {
            if (all[i].EventId == upToEventId)
            {
                boundaryIndex = i;
                break;
            }
        }

        if (boundaryIndex < 0)
            throw new KeyNotFoundException($"Event '{upToEventId}' was not found in session '{sessionId}'.");

        var boundary = all[boundaryIndex];
        var slice = all.Take(boundaryIndex + 1).ToList();
        return Fold(sessionId, slice, boundary.EventId, boundary.OccurredAt);
    }

    public async Task<SessionStateSnapshot> GetStateAtTimeAsync(
        string sessionId, DateTimeOffset asOf, CancellationToken ct = default)
    {
        var all = await _events.GetSessionEventsAsync(sessionId, ct: ct).ConfigureAwait(false);
        var slice = all.Where(e => e.OccurredAt <= asOf).ToList();
        var boundaryEventId = slice.Count > 0 ? slice[^1].EventId : string.Empty;
        return Fold(sessionId, slice, boundaryEventId, asOf);
    }

    private static SessionStateSnapshot Fold(
        string sessionId, IReadOnlyList<ExecutionEvent> events, string boundaryEventId, DateTimeOffset boundaryTime)
    {
        var activeWorkUnits = new List<string>();
        var activeWorkspaces = new List<string>();
        var artifactIds = new List<string>();
        var completedEventIds = new List<string>(events.Count);

        foreach (var ev in events)
        {
            completedEventIds.Add(ev.EventId);

            switch (ev.Kind)
            {
                case ExecutionEventKind.WorkUnitCreated:
                case ExecutionEventKind.WorkUnitScheduled:
                case ExecutionEventKind.WorkUnitStarted:
                    if (ev.WorkUnitId is not null && !activeWorkUnits.Contains(ev.WorkUnitId))
                        activeWorkUnits.Add(ev.WorkUnitId);
                    break;

                case ExecutionEventKind.WorkUnitCompleted:
                case ExecutionEventKind.WorkUnitFailed:
                case ExecutionEventKind.WorkUnitAbandoned:
                    if (ev.WorkUnitId is not null)
                        activeWorkUnits.Remove(ev.WorkUnitId);
                    break;

                case ExecutionEventKind.WorkspaceCreated:
                    var created = JsonSerializer.Deserialize<WorkspaceCreatedPayload>(ev.PayloadJson);
                    if (created is not null && !activeWorkspaces.Contains(created.WorkspaceId))
                        activeWorkspaces.Add(created.WorkspaceId);
                    break;

                case ExecutionEventKind.WorkspaceBranchCreated:
                    var branched = JsonSerializer.Deserialize<WorkspaceBranchCreatedPayload>(ev.PayloadJson);
                    if (branched is not null && !activeWorkspaces.Contains(branched.NewWorkspaceId))
                        activeWorkspaces.Add(branched.NewWorkspaceId);
                    break;

                case ExecutionEventKind.WorkspaceArchived:
                    var archived = JsonSerializer.Deserialize<WorkspaceArchivedPayload>(ev.PayloadJson);
                    if (archived is not null)
                        activeWorkspaces.Remove(archived.WorkspaceId);
                    break;

                case ExecutionEventKind.WorkspaceDestroyed:
                    var destroyed = JsonSerializer.Deserialize<WorkspaceDestroyedPayload>(ev.PayloadJson);
                    if (destroyed is not null)
                        activeWorkspaces.Remove(destroyed.WorkspaceId);
                    break;

                case ExecutionEventKind.ArtifactProposed:
                    var proposed = JsonSerializer.Deserialize<ArtifactProposedPayload>(ev.PayloadJson);
                    if (proposed is not null && !artifactIds.Contains(proposed.ArtifactId))
                        artifactIds.Add(proposed.ArtifactId);
                    break;
            }
        }

        return new SessionStateSnapshot(
            sessionId,
            boundaryEventId,
            boundaryTime,
            activeWorkUnits,
            activeWorkspaces,
            artifactIds,
            completedEventIds);
    }
}
