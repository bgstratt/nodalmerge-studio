using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the success criteria from Slice 10e — Orchestration Decision Log: every routing
/// decision is persisted with a non-null projection snapshot, queryable per work unit, and
/// mirrored into the unified event stream (10b.2) when a session is active.
/// </summary>
[Trait("Category", "Integration")]
public class OrchestrationDecisionLogTests
{
    [Fact]
    public async Task RecordAsync_persists_event_retrievable_by_GetEventsAsync()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var log = new OrchestrationDecisionLogService(store, events);

        var recorded = await log.RecordAsync(
            "WU-1", "orch-1", PipelineStage.Execute, "{\"chain\":[]}",
            OrchestrationAction.Enqueue, ["WU-1"], "scheduler.enqueue");

        Assert.NotNull(recorded.InputProjectionSnapshot);
        Assert.NotEmpty(recorded.InputProjectionSnapshot);

        var fetched = await log.GetEventsAsync("WU-1");
        Assert.Single(fetched);
        Assert.Equal(recorded.EventId, fetched[0].EventId);
    }

    [Fact]
    public async Task GetEventsAsync_returns_events_ordered_by_OccurredAt()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var log = new OrchestrationDecisionLogService(store, events);

        await log.RecordAsync("WU-1", "orch-1", PipelineStage.Plan, "{}", OrchestrationAction.Enqueue, [], null);
        await log.RecordAsync("WU-1", "orch-1", PipelineStage.Execute, "{}", OrchestrationAction.SpawnWorker, ["agent-1"], null);
        await log.RecordAsync("WU-1", "orch-1", PipelineStage.Review, "{}", OrchestrationAction.NoOp, [], "Done.");

        var fetched = await log.GetEventsAsync("WU-1");

        Assert.Equal(
            [OrchestrationAction.Enqueue, OrchestrationAction.SpawnWorker, OrchestrationAction.NoOp],
            fetched.Select(e => e.Action));
    }

    [Fact]
    public async Task GetEventsAsync_is_scoped_to_a_single_work_unit()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var log = new OrchestrationDecisionLogService(store, events);

        await log.RecordAsync("WU-1", "orch-1", PipelineStage.Plan, "{}", OrchestrationAction.Enqueue, [], null);
        await log.RecordAsync("WU-2", "orch-1", PipelineStage.Plan, "{}", OrchestrationAction.Enqueue, [], null);

        var fetched = await log.GetEventsAsync("WU-1");
        Assert.Single(fetched);
        Assert.Equal("WU-1", fetched[0].WorkUnitId);
    }

    [Fact]
    public async Task RecordAsync_with_sessionId_mirrors_into_the_unified_event_stream()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var log = new OrchestrationDecisionLogService(store, events);

        await log.RecordAsync(
            "WU-1", "orch-1", PipelineStage.Execute, "{}",
            OrchestrationAction.ApplyMerge, ["MP-1"], "merge.apply", sessionId: "SES-1");

        var sessionEvents = await events.GetSessionEventsAsync("SES-1");
        Assert.Single(sessionEvents, e => e.Kind == ExecutionEventKind.OrchestrationDecision);
    }

    [Fact]
    public async Task RecordAsync_without_sessionId_does_not_touch_the_event_stream()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var log = new OrchestrationDecisionLogService(store, events);

        await log.RecordAsync("WU-1", "orch-1", PipelineStage.Plan, "{}", OrchestrationAction.NoOp, [], null);

        var sessionEvents = await events.GetSessionEventsAsync("WU-1"); // no session was ever created
        Assert.Empty(sessionEvents);
    }
}
