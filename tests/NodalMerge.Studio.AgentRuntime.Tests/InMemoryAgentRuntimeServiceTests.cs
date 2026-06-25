using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime.Tests;

public class InMemoryAgentRuntimeServiceTests
{
    private static InMemoryAgentRuntimeService Build() =>
        new(new NoopServiceProvider(), NullLogger<InMemoryAgentRuntimeService>.Instance, new NoopAgentProfileService(), new NoopScheduler(), new NoopEventStream(), new WorkspaceOptions(), new NoopFileLeaseService());

    private sealed class NoopFileLeaseService : IFileLeaseService
    {
        public Task<(bool Granted, string? HolderWorkUnitId)> TryAcquireOrEnqueueAsync(
            string workUnitId, string path, CancellationToken ct = default) =>
            Task.FromResult<(bool Granted, string? HolderWorkUnitId)>((true, workUnitId));
        public Task<string?> ReleaseAndAdvanceAsync(string path, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);
        public Task<IReadOnlyList<string>> ForceReleaseAllForWorkUnitAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<FileLeaseInfo>> ListAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<FileLeaseInfo>>([]);
    }

    private sealed class NoopScheduler : IWorkScheduler
    {
        public Task EnqueueAsync(string workUnitId, string profileId, string? taskId = null, string? model = null,
            string? baseUrl = null, string? apiKey = null, string? provider = null, string? sessionId = null,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<ScheduledItem?>(null);
        public Task ReleaseAsync(string workUnitId, bool success, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAwaitingResumeAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAwaitingFileLeaseAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAwaitingFileLeaseAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledItem>>([]);
        public Task<IReadOnlyList<ScheduledItem>> ListAwaitingResumeAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledItem>>([]);
        public Task ApproveResumeAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ApproveResumeAllAsync(CancellationToken ct = default) => Task.FromResult(0);
    }

    private sealed class NoopEventStream : IExecutionEventStream
    {
        public Task<NodalMerge.Studio.Contracts.Domain.ExecutionEvent> AppendAsync<T>(
            string sessionId, string? workUnitId,
            NodalMerge.Studio.Contracts.Domain.ExecutionEventKind kind, T payload,
            string? causedByEventId = null, string? eventId = null, CancellationToken ct = default) =>
            Task.FromResult(new NodalMerge.Studio.Contracts.Domain.ExecutionEvent(
                eventId ?? Guid.NewGuid().ToString("N"), sessionId, workUnitId, kind, "{}", causedByEventId, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>> GetSessionEventsAsync(
            string sessionId, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>>([]);

        public Task<NodalMerge.Studio.Contracts.Domain.ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default) =>
            Task.FromResult<NodalMerge.Studio.Contracts.Domain.ExecutionEvent?>(null);

        public Task<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>> GetEventsByKindAsync(
            IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEventKind> kinds, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>>([]);
    }

    private sealed class NoopServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoopAgentProfileService : IAgentProfileService
    {
        public Task<AgentProfile> CreateAsync(AgentProfile profile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentProfile?> GetAsync(string profileId, CancellationToken ct = default) => Task.FromResult<AgentProfile?>(null);
        public Task<AgentProfile> UpdateAsync(AgentProfile profile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentProfile>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentProfile>>([]);
    }

    // ── SpawnAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SpawnAsync_returns_agentId_with_agentType_prefix()
    {
        var svc = Build();
        var agentId = await svc.SpawnAsync("worker", "wu-1");
        Assert.StartsWith("worker-", agentId);
    }

    [Fact]
    public async Task SpawnAsync_creates_agent_with_active_status()
    {
        var svc = Build();
        var agentId = await svc.SpawnAsync("worker", "wu-1");
        var status = await svc.GetStatusAsync(agentId);
        Assert.Equal("active", status);
    }

    [Fact]
    public async Task SpawnAsync_tracks_workUnit_association_in_ListActive()
    {
        var svc = Build();
        var agentId = await svc.SpawnAsync("worker", "wu-42");

        var active = await svc.ListActiveAsync();

        Assert.Single(active);
        Assert.Equal(agentId, active[0].AgentId);
        Assert.Equal("wu-42", active[0].WorkUnitId);
        Assert.Equal("active", active[0].Status);
    }

    // ── PauseAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task PauseAsync_pauses_active_agent()
    {
        var svc = Build();
        var agentId = await svc.SpawnAsync("worker", "wu-1");

        await svc.PauseAsync(agentId);

        Assert.Equal("paused", await svc.GetStatusAsync(agentId));
    }

    [Fact]
    public async Task PauseAsync_throws_for_unknown_agent()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Build().PauseAsync("no-such-agent"));
    }

    // ── ResumeAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ResumeAsync_resumes_paused_agent()
    {
        var svc = Build();
        var agentId = await svc.SpawnAsync("worker", "wu-1");
        await svc.PauseAsync(agentId);

        await svc.ResumeAsync(agentId);

        Assert.Equal("active", await svc.GetStatusAsync(agentId));
    }

    [Fact]
    public async Task ResumeAsync_throws_for_unknown_agent()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Build().ResumeAsync("no-such-agent"));
    }

    // ── StopAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task StopAsync_stops_agent()
    {
        var svc = Build();
        var agentId = await svc.SpawnAsync("worker", "wu-1");

        await svc.StopAsync(agentId);

        Assert.Equal("stopped", await svc.GetStatusAsync(agentId));
    }

    [Fact]
    public async Task StopAsync_throws_for_unknown_agent()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Build().StopAsync("no-such-agent"));
    }

    // ── GetStatusAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_returns_unknown_for_never_spawned_agent()
    {
        var status = await Build().GetStatusAsync("ghost");
        Assert.Equal("unknown", status);
    }

    // ── ListActiveAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ListActiveAsync_excludes_paused_and_stopped_agents()
    {
        var svc = Build();
        var a1 = await svc.SpawnAsync("worker", "wu-1");
        var a2 = await svc.SpawnAsync("worker", "wu-2");
        var a3 = await svc.SpawnAsync("worker", "wu-3");

        await svc.PauseAsync(a2);
        await svc.StopAsync(a3);

        var active = await svc.ListActiveAsync();

        Assert.Single(active);
        Assert.Equal(a1, active[0].AgentId);
    }

    [Fact]
    public async Task ListActiveAsync_returns_empty_when_no_agents_spawned()
    {
        var active = await Build().ListActiveAsync();
        Assert.Empty(active);
    }

    // ── RecordActionAsync + GetSnapshotAsync ─────────────────────────────────

    [Fact]
    public async Task RecordActionAsync_appends_to_snapshot_RecentActions()
    {
        var svc = Build();
        await svc.RecordActionAsync("agent-1", "wu-1", "analyzed code");
        await svc.RecordActionAsync("agent-1", "wu-1", "wrote tests");

        var snapshot = await svc.GetSnapshotAsync("agent-1", "wu-1");

        Assert.Equal(2, snapshot.RecentActions.Count);
        Assert.Equal("analyzed code", snapshot.RecentActions[0]);
        Assert.Equal("wrote tests", snapshot.RecentActions[1]);
    }

    [Fact]
    public async Task GetSnapshotAsync_returns_empty_snapshot_for_unknown_agent()
    {
        var snapshot = await Build().GetSnapshotAsync("ghost", "wu-0");

        Assert.Equal("ghost", snapshot.AgentId);
        Assert.Equal("wu-0", snapshot.WorkUnitId);
        Assert.Empty(snapshot.RecentActions);
    }

    // ── ReinvokeOrchestratorAsync ────────────────────────────────────────────

    [Fact]
    public async Task ReinvokeOrchestratorAsync_noop_when_no_prior_orchestrator_registered()
    {
        var svc = Build();

        await svc.ReinvokeOrchestratorAsync("wu-never-spawned");

        Assert.Empty(await svc.ListAllAsync());
    }

    [Fact]
    public async Task ReinvokeOrchestratorAsync_starts_a_new_agent_for_a_registered_work_unit()
    {
        var svc = Build();
        await svc.SpawnAsync("orchestrator", "wu-1", model: "m", baseUrl: "http://fake-llm", apiKey: "k");

        await svc.ReinvokeOrchestratorAsync("wu-1");

        var all = await svc.ListAllAsync();
        Assert.Equal(2, all.Count);
        Assert.All(all, a => Assert.Equal("wu-1", a.WorkUnitId));
        Assert.NotEqual(all[0].AgentId, all[1].AgentId);
    }

    [Fact]
    public async Task ReinvokeOrchestratorAsync_does_not_register_when_credentials_are_missing()
    {
        var svc = Build();
        // No baseUrl/apiKey — canStartLoop is false, so SpawnAsync never registers an orchestrator.
        await svc.SpawnAsync("orchestrator", "wu-1");

        await svc.ReinvokeOrchestratorAsync("wu-1");

        // Only the original SpawnAsync's AgentRecord exists — reinvoke found nothing registered.
        Assert.Single(await svc.ListAllAsync());
    }
}
