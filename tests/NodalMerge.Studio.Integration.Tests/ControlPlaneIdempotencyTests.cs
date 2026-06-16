using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Merge;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the success criteria from Slice 10b.3 — Control-Plane Idempotency:
/// retries of control-plane actions must not duplicate scheduler entries,
/// leases, artifact records, events, or merge proposals.
/// </summary>
[Trait("Category", "Integration")]
public class ControlPlaneIdempotencyTests
{
    private static (WorkSchedulerService Scheduler, ExecutionEventStreamService Events) BuildScheduler()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var workspaces = new AgentWorkspaceService(store, new SingleServiceProvider(new NoopWorkUnitService()), new NoopFileWorkspaceService(), events);
        var scheduler = new WorkSchedulerService(store, events, workspaces);
        return (scheduler, events);
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private sealed class NoopWorkUnitService : IWorkUnitService
    {
        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<WorkUnit?>(null);
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
    }

    // ── IWorkScheduler.EnqueueAsync — key: SessionId + WorkUnitId ────────────

    [Fact]
    public async Task EnqueueAsync_called_twice_produces_one_pending_entry()
    {
        var (scheduler, _) = BuildScheduler();

        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");
        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");

        var pending = await scheduler.ListPendingAsync();
        Assert.Single(pending);
        Assert.Equal("WU-1", pending[0].WorkUnitId);
    }

    [Fact]
    public async Task EnqueueAsync_called_twice_emits_only_one_WorkUnitScheduled_event()
    {
        var (scheduler, events) = BuildScheduler();

        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");
        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");

        var sessionEvents = await events.GetSessionEventsAsync("SES-1");
        Assert.Single(sessionEvents, e => e.Kind == ExecutionEventKind.WorkUnitScheduled);
    }

    // ── IWorkScheduler.TryAcquireAsync — key: WorkUnitId + AgentId ───────────

    [Fact]
    public async Task TryAcquireAsync_reacquire_by_same_agent_returns_existing_lease()
    {
        var (scheduler, _) = BuildScheduler();
        await scheduler.EnqueueAsync("WU-1", "profile-a");

        var first = await scheduler.TryAcquireAsync("agent-X");
        var second = await scheduler.TryAcquireAsync("agent-X");

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.WorkUnitId, second!.WorkUnitId);
        Assert.Equal(first.AttemptCount, second.AttemptCount);
    }

    [Fact]
    public async Task TryAcquireAsync_by_different_agent_does_not_steal_lease()
    {
        var (scheduler, _) = BuildScheduler();
        await scheduler.EnqueueAsync("WU-1", "profile-a");

        var first = await scheduler.TryAcquireAsync("agent-X");
        var second = await scheduler.TryAcquireAsync("agent-Y");

        Assert.NotNull(first);
        Assert.Null(second); // no other items pending, lease held by agent-X
    }

    // ── IArtifactLineageService.RecordAsync — key: ArtifactId ────────────────

    [Fact]
    public async Task ArtifactRefWriteAsync_called_twice_with_same_ArtifactId_is_noop()
    {
        var svc = new InMemoryArtifactRefService();
        var original = new ArtifactRef("ART-1", ArtifactType.BranchChangeset, null, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, "WU-1", null);
        var duplicate = original with { Status = ArtifactStatus.Superseded };

        await svc.WriteAsync(original);
        await svc.WriteAsync(duplicate);

        var list = await svc.ListAsync("WU-1");
        Assert.Single(list);
        Assert.Equal(ArtifactStatus.Active, list[0].Status); // first write wins; second is a no-op
    }

    // ── IExecutionEventStream.AppendAsync — key: EventId ─────────────────────

    [Fact]
    public async Task AppendAsync_called_twice_with_same_EventId_is_noop()
    {
        var stream = new ExecutionEventStreamService(new InMemoryStudioNodeStore());

        var first = await stream.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkUnitScheduled,
            new { }, eventId: "EVT-fixed");
        var second = await stream.AppendAsync("SES-1", "WU-1", ExecutionEventKind.WorkUnitScheduled,
            new { }, eventId: "EVT-fixed");

        Assert.Same(first, second);

        var sessionEvents = await stream.GetSessionEventsAsync("SES-1");
        Assert.Single(sessionEvents, e => e.EventId == "EVT-fixed");
    }

    // ── nm.v1.merge.propose (MCP) — key: CommandId ───────────────────────────

    [Fact]
    public async Task MergeProposeAsync_called_twice_with_same_CommandId_creates_one_proposal()
    {
        var store = new InMemoryStudioNodeStore();
        var mergeService = new InMemoryMergeService(
            store, new NoopFileWorkspaceService(), new WorkspaceOptions(), new NoopEventStream());
        var tools = new MergeTools(mergeService, store);

        var commandId = Guid.NewGuid().ToString("N");
        var first = await tools.ProposeAsync("feat/x", "main", "summary", commandId: commandId);
        var second = await tools.ProposeAsync("feat/x", "main", "summary", commandId: commandId);

        Assert.Equal(first, second);

        var proposals = await mergeService.ListAsync();
        Assert.Single(proposals);
    }

    [Fact]
    public async Task MergeProposeAsync_without_CommandId_creates_separate_proposals()
    {
        var store = new InMemoryStudioNodeStore();
        var mergeService = new InMemoryMergeService(
            store, new NoopFileWorkspaceService(), new WorkspaceOptions(), new NoopEventStream());
        var tools = new MergeTools(mergeService, store);

        await tools.ProposeAsync("feat/x", "main", "summary");
        await tools.ProposeAsync("feat/x", "main", "summary");

        var proposals = await mergeService.ListAsync();
        Assert.Equal(2, proposals.Count);
    }

    // ── Stubs ─────────────────────────────────────────────────────────────

    private sealed class NoopEventStream : IExecutionEventStream
    {
        public Task<ExecutionEvent> AppendAsync<T>(
            string sessionId, string? workUnitId, ExecutionEventKind kind, T payload,
            string? causedByEventId = null, string? eventId = null, CancellationToken ct = default) =>
            Task.FromResult(new ExecutionEvent(
                eventId ?? Guid.NewGuid().ToString("N"), sessionId, workUnitId, kind, "{}", causedByEventId, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<ExecutionEvent>> GetSessionEventsAsync(
            string sessionId, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExecutionEvent>>([]);

        public Task<ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default) =>
            Task.FromResult<ExecutionEvent?>(null);
    }

    private sealed class NoopFileWorkspaceService : IFileWorkspaceService
    {
        public Task InitBranchAsync(string b, string? s = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(string b, string p, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string b, string p, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string b, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string b, string p, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> ListAsync(string b, string? s = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string> DiffAsync(string s, string t, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task ApplyBranchAsync(string s, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetWorkingDirectoryAsync(string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
