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
        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? blockedReason, CancellationToken ct = default) => throw new NotSupportedException();
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

    // ── Phase 4 slice 11a — WorkUnitStatus wiring + orchestrator re-invocation ─

    private static (WorkSchedulerService Scheduler, RecordingWorkUnitService WorkUnits, RecordingAgentControlService AgentControl)
        BuildSchedulerWithLifecycle()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var workUnits = new RecordingWorkUnitService();
        var agentControl = new RecordingAgentControlService();
        var serviceProvider = new MultiServiceProvider(workUnits, agentControl);
        var workspaces = new AgentWorkspaceService(store, serviceProvider, new NoopFileWorkspaceService(), events);
        var scheduler = new WorkSchedulerService(store, events, workspaces, serviceProvider);
        return (scheduler, workUnits, agentControl);
    }

    [Fact]
    public async Task EnqueueAsync_first_call_transitions_work_unit_to_Queued()
    {
        var (scheduler, workUnits, _) = BuildSchedulerWithLifecycle();

        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");

        Assert.Equal(WorkUnitStatus.Queued, workUnits.Calls.Single().Status);
    }

    [Fact]
    public async Task EnqueueAsync_called_twice_does_not_double_transition()
    {
        var (scheduler, workUnits, _) = BuildSchedulerWithLifecycle();

        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");
        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");

        Assert.Single(workUnits.Calls);
    }

    [Fact]
    public async Task TryAcquireAsync_transitions_work_unit_to_Executing()
    {
        var (scheduler, workUnits, _) = BuildSchedulerWithLifecycle();
        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");

        await scheduler.TryAcquireAsync("agent-X");

        Assert.Equal(WorkUnitStatus.Executing, workUnits.Calls.Last().Status);
    }

    [Fact]
    public async Task ReleaseAsync_success_reinvokes_the_registered_orchestrator()
    {
        var (scheduler, _, agentControl) = BuildSchedulerWithLifecycle();
        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");
        await scheduler.TryAcquireAsync("agent-X");

        await scheduler.ReleaseAsync("WU-1", success: true);

        var call = Assert.Single(agentControl.ReinvokeCalls);
        Assert.Equal("WU-1", call.WorkUnitId);
        Assert.Equal("SES-1", call.SessionId);
    }

    [Fact]
    public async Task ReleaseAsync_failure_does_not_auto_retry_or_reinvoke_orchestrator()
    {
        var (scheduler, _, agentControl) = BuildSchedulerWithLifecycle();
        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");
        await scheduler.TryAcquireAsync("agent-X");

        await scheduler.ReleaseAsync("WU-1", success: false);

        Assert.Empty(agentControl.ReinvokeCalls);
    }

    [Fact]
    public async Task ReleaseAsync_without_a_resolvable_IAgentControlService_does_not_throw()
    {
        // The existing BuildScheduler() helper passes no IServiceProvider at all — confirms
        // backward compatibility with every other test in this file that already exercises
        // ReleaseAsync's call sites indirectly via the scheduler's own queue lifecycle.
        var (scheduler, _) = BuildScheduler();
        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");
        await scheduler.TryAcquireAsync("agent-X");

        await scheduler.ReleaseAsync("WU-1", success: true);
    }

    private sealed class MultiServiceProvider(params object[] services) : IServiceProvider
    {
        public object? GetService(Type serviceType) => services.FirstOrDefault(serviceType.IsInstanceOfType);
    }

    private sealed class NoopServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class RecordingWorkUnitService : IWorkUnitService
    {
        public List<(string WorkUnitId, WorkUnitStatus Status, string? SessionId)> Calls { get; } = [];
        private WorkUnitStatus _status = WorkUnitStatus.Created;

        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken ct = default)
        {
            Calls.Add((workUnitId, status, sessionId));
            _status = status;
            var updated = new WorkUnit(workUnitId, "goal", "branch-1", status, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, "owner", null, null, null, null, [], []);
            return Task.FromResult(updated);
        }

        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<WorkUnit?>(new WorkUnit(workUnitId, "goal", "branch-1", _status, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, "owner", null, null, null, null, [], []));
        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? blockedReason, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
    }

    private sealed class RecordingAgentControlService : IAgentControlService
    {
        public List<(string WorkUnitId, string? SessionId)> ReinvokeCalls { get; } = [];

        public Task<string> SpawnAsync(string agentType, string workUnitId, string? taskId = null, string? model = null,
            string? baseUrl = null, string? apiKey = null, string? provider = null, string? profileId = null,
            string? autoReviewProfileId = null, IReadOnlyDictionary<PipelineStage, OrchestratorCredentials>? stageCredentials = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            ReinvokeCalls.Add((workUnitId, sessionId));
            return Task.CompletedTask;
        }

        public OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId) => null;
        public OrchestratorCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage) => null;
        public string? GetAutoReviewProfileId(string workUnitId) => null;

        public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    // ── IArtifactLineageService.RecordAsync — key: ArtifactId ────────────────

    [Fact]
    public async Task ArtifactLineageRecordAsync_called_twice_with_same_ArtifactId_is_noop()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        var original = new ArtifactRef("ART-1", ArtifactType.BranchChangeset, null, ArtifactStatus.Active,
            DateTimeOffset.UtcNow, "WU-1", null);
        var duplicate = original with { Status = ArtifactStatus.Superseded };

        await svc.RecordAsync(original);
        await svc.RecordAsync(duplicate);

        var chain = await svc.GetChainAsync("WU-1");
        Assert.Single(chain);
        Assert.Equal(ArtifactStatus.Active, chain[0].Status); // first write wins; second is a no-op
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
        var events = new NoopEventStream();
        var fileWorkspace = new NoopFileWorkspaceService();
        var artifacts = new ArtifactLineageService(store);
        var mergeService = new InMemoryMergeService(store, fileWorkspace, new WorkspaceOptions(), events, artifacts);
        var mergeCommands = new MergeCommandService(mergeService, fileWorkspace, artifacts, events, store, new NoopServiceProvider());
        var tools = new MergeTools(mergeCommands);

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
        var events = new NoopEventStream();
        var fileWorkspace = new NoopFileWorkspaceService();
        var artifacts = new ArtifactLineageService(store);
        var mergeService = new InMemoryMergeService(store, fileWorkspace, new WorkspaceOptions(), events, artifacts);
        var mergeCommands = new MergeCommandService(mergeService, fileWorkspace, artifacts, events, store, new NoopServiceProvider());
        var tools = new MergeTools(mergeCommands);

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
        public Task<IReadOnlyList<string>> ListAsync(string b, string? s = null, string? p = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string> DiffAsync(string s, string t, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task ApplyBranchAsync(string s, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task CopyFilesAsync(string s, string t, IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetWorkingDirectoryAsync(string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<WorkspaceDiff> DiffExternalPathAsync(string b, string e, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceDiff([], [], [], string.Empty));
        public Task ApplyExternalPathAsync(string b, string e, CancellationToken ct = default) => Task.CompletedTask;
    }
}
