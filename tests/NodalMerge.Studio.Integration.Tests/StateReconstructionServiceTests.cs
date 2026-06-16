using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the success criteria from Slice 10b.4 — State Reconstruction API:
/// folding the event stream up to an event or timestamp must reproduce the
/// exact set of active work units, workspaces, and artifacts at that boundary,
/// without touching any agent, LLM, or external process.
/// </summary>
[Trait("Category", "Integration")]
public class StateReconstructionServiceTests
{
    private const string SessionId = "SES-1";

    [Fact]
    public async Task GetStateAtAsync_mid_run_shows_only_units_and_workspaces_existing_at_that_point()
    {
        var events = new ExecutionEventStreamService(new InMemoryStudioNodeStore());
        var reconstruction = new StateReconstructionService(events);

        await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.WorkUnitCreated, new { });
        await events.AppendAsync(SessionId, "WU-2", ExecutionEventKind.WorkUnitCreated, new { });
        var midRunEvent = await events.AppendAsync(SessionId, "WU-3", ExecutionEventKind.WorkUnitCreated, new { });

        // Events after the boundary must not affect the snapshot.
        await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.WorkUnitCompleted,
            new WorkUnitCompletedPayload("WU-1", "agent-X", null));
        await events.AppendAsync(SessionId, "WU-4", ExecutionEventKind.WorkUnitCreated, new { });

        var snapshot = await reconstruction.GetStateAtAsync(SessionId, midRunEvent.EventId);

        Assert.Equal(midRunEvent.EventId, snapshot.BoundaryEventId);
        Assert.Equal(["WU-1", "WU-2", "WU-3"], snapshot.ActiveWorkUnitIds);
        Assert.Empty(snapshot.ActiveWorkspaceIds);
    }

    [Fact]
    public async Task GetStateAtTimeAsync_between_enqueue_and_lease_shows_workunit_active_no_workspace()
    {
        var events = new ExecutionEventStreamService(new InMemoryStudioNodeStore());
        var reconstruction = new StateReconstructionService(events);

        await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.WorkUnitScheduled,
            new WorkUnitScheduledPayload("WU-1", "profile-a", 1));
        await Task.Delay(15); // ensure asOf falls strictly between the two events

        var asOf = DateTimeOffset.UtcNow;
        await Task.Delay(15);

        await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.WorkUnitStarted,
            new WorkUnitStartedPayload("WU-1", "agent-X"));
        await events.AppendAsync(SessionId, null, ExecutionEventKind.WorkspaceCreated,
            new WorkspaceCreatedPayload("WKS-1", "WU-1", "agent/wu-1", "main"));

        var snapshot = await reconstruction.GetStateAtTimeAsync(SessionId, asOf);

        Assert.Contains("WU-1", snapshot.ActiveWorkUnitIds);
        Assert.Empty(snapshot.ActiveWorkspaceIds);
    }

    [Fact]
    public async Task GetStateAtAsync_final_event_matches_post_run_state()
    {
        var events = new ExecutionEventStreamService(new InMemoryStudioNodeStore());
        var reconstruction = new StateReconstructionService(events);

        await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.WorkUnitCreated, new { });
        await events.AppendAsync(SessionId, null, ExecutionEventKind.WorkspaceCreated,
            new WorkspaceCreatedPayload("WKS-1", "WU-1", "agent/wu-1", "main"));
        await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.ArtifactProposed,
            new ArtifactProposedPayload("ART-1", "WU-1", ["src/foo.cs"]));
        await events.AppendAsync(SessionId, null, ExecutionEventKind.WorkspaceArchived,
            new WorkspaceArchivedPayload("WKS-1", "agent/wu-1"));
        var lastEvent = await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.WorkUnitCompleted,
            new WorkUnitCompletedPayload("WU-1", "agent-X", "ART-1"));

        var snapshot = await reconstruction.GetStateAtAsync(SessionId, lastEvent.EventId);

        Assert.Empty(snapshot.ActiveWorkUnitIds);   // WU-1 completed
        Assert.Empty(snapshot.ActiveWorkspaceIds);  // WKS-1 archived
        Assert.Equal(["ART-1"], snapshot.ArtifactIds);
        Assert.Equal(5, snapshot.CompletedEventIds.Count);
        Assert.Equal(lastEvent.EventId, snapshot.BoundaryEventId);
    }

    [Fact]
    public async Task GetStateAtAsync_unknown_event_id_throws()
    {
        var events = new ExecutionEventStreamService(new InMemoryStudioNodeStore());
        var reconstruction = new StateReconstructionService(events);

        await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.WorkUnitCreated, new { });

        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => reconstruction.GetStateAtAsync(SessionId, "EVT-does-not-exist"));
    }

    [Fact]
    public async Task GetStateAtAsync_does_not_depend_on_any_agent_or_external_process()
    {
        // StateReconstructionService is constructed from IExecutionEventStream alone —
        // no IAgentControlService, ILlmClient, or process dependency is reachable.
        var events = new ExecutionEventStreamService(new InMemoryStudioNodeStore());
        var reconstruction = new StateReconstructionService(events);

        var created = await events.AppendAsync(SessionId, "WU-1", ExecutionEventKind.WorkUnitCreated, new { });
        var snapshot = await reconstruction.GetStateAtAsync(SessionId, created.EventId);

        Assert.Equal(["WU-1"], snapshot.ActiveWorkUnitIds);
    }
}
