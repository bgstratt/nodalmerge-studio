using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;
using NodalMerge.Studio.Tasks;
using TaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Tasks.Tests;

public class InMemoryTaskServiceTests
{
    // ── Fake ───────────────────────────────────────────────────────────────

    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        private readonly Dictionary<string, WorkUnit> _store = new();

        public void Seed(string workUnitId) =>
            _store[workUnitId] = new WorkUnit(workUnitId, "goal", "branch", WorkUnitStatus.Active,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner", null, null, null, null, [], []);

        public Task<WorkUnit?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_store.GetValueOrDefault(id));

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(_store.Values.ToList());

        public Task<WorkUnit> CreateAsync(WorkUnit w, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string id, WorkUnitStatus s, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetCurrentStageAsync(string id, PipelineStage? stage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string id, string? blockedReason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementReviewRejectionCountAsync(string id, bool automated, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementFailureAttemptCountAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> AmendGoalForSteeredRetryAsync(string id, string amendedGoal, string steeringContext, string deadLetterEntryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static (InMemoryTaskService svc, FakeWorkUnitService workUnits) Build(params string[] seededWorkUnitIds)
    {
        var workUnits = new FakeWorkUnitService();
        foreach (var id in seededWorkUnitIds)
            workUnits.Seed(id);
        return (new InMemoryTaskService(workUnits, new InMemoryStudioNodeStore()), workUnits);
    }

    private static StudioTask MakeTask(string taskId, string workUnitId, TaskStatus status = TaskStatus.Open, int priority = 0) =>
        new(taskId, workUnitId, $"Task {taskId}", "desc", status, null, priority);

    // ── CreateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_rejects_missing_work_unit()
    {
        var (svc, _) = Build(); // no seeded WUs
        var task = MakeTask("T-1", "WU-MISSING");
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.CreateAsync(task));
    }

    [Fact]
    public async Task CreateAsync_succeeds_with_valid_work_unit()
    {
        var (svc, _) = Build("WU-1");
        var task = MakeTask("T-1", "WU-1");
        var created = await svc.CreateAsync(task);
        Assert.Equal("T-1", created.TaskId);
    }

    // ── GetAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_returns_stored_task()
    {
        var (svc, _) = Build("WU-1");
        await svc.CreateAsync(MakeTask("T-1", "WU-1"));
        var result = await svc.GetAsync("T-1");
        Assert.NotNull(result);
        Assert.Equal("T-1", result.TaskId);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_unknown_id()
    {
        var (svc, _) = Build("WU-1");
        var result = await svc.GetAsync("no-such-task");
        Assert.Null(result);
    }

    // ── UpdateAsync — transition enforcement ───────────────────────────────

    [Fact]
    public async Task UpdateAsync_allows_valid_transition()
    {
        var (svc, _) = Build("WU-1");
        await svc.CreateAsync(MakeTask("T-1", "WU-1", TaskStatus.Open));
        var updated = await svc.UpdateAsync(MakeTask("T-1", "WU-1", TaskStatus.InProgress));
        Assert.Equal(TaskStatus.InProgress, updated.Status);
    }

    [Fact]
    public async Task UpdateAsync_rejects_invalid_transition()
    {
        var (svc, _) = Build("WU-1");
        await svc.CreateAsync(MakeTask("T-1", "WU-1", TaskStatus.Open));
        // Open → Completed is not allowed; must go through InProgress
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateAsync(MakeTask("T-1", "WU-1", TaskStatus.Completed)));
    }

    [Fact]
    public async Task UpdateAsync_allows_same_status_as_no_op()
    {
        var (svc, _) = Build("WU-1");
        await svc.CreateAsync(MakeTask("T-1", "WU-1", TaskStatus.Open));
        // Same-status update (e.g. title change with no status change) must not throw
        var updated = await svc.UpdateAsync(MakeTask("T-1", "WU-1", TaskStatus.Open));
        Assert.Equal(TaskStatus.Open, updated.Status);
    }

    // ── AssignAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task AssignAsync_transitions_open_task_to_InProgress()
    {
        var (svc, _) = Build("WU-1");
        await svc.CreateAsync(MakeTask("T-1", "WU-1", TaskStatus.Open));
        var result = await svc.AssignAsync("T-1", "agent-42");
        Assert.Equal(TaskStatus.InProgress, result.Status);
        Assert.Equal("agent-42", result.Assignee);
    }

    [Fact]
    public async Task AssignAsync_rejects_completed_task()
    {
        var (svc, _) = Build("WU-1");
        await svc.CreateAsync(MakeTask("T-1", "WU-1", TaskStatus.Open));
        await svc.UpdateAsync(MakeTask("T-1", "WU-1", TaskStatus.InProgress));
        await svc.UpdateAsync(MakeTask("T-1", "WU-1", TaskStatus.Completed));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.AssignAsync("T-1", "agent-99"));
    }

    // ── ListAsync — priority ordering ──────────────────────────────────────

    [Fact]
    public async Task ListAsync_orders_by_priority_descending()
    {
        var (svc, _) = Build("WU-1");
        await svc.CreateAsync(MakeTask("T-low", "WU-1", priority: 1));
        await svc.CreateAsync(MakeTask("T-high", "WU-1", priority: 10));
        await svc.CreateAsync(MakeTask("T-mid", "WU-1", priority: 5));

        var list = await svc.ListAsync("WU-1");
        Assert.Equal(["T-high", "T-mid", "T-low"], list.Select(t => t.TaskId).ToArray());
    }

    [Fact]
    public async Task ListAsync_filters_by_work_unit_id()
    {
        var (svc, _) = Build("WU-A", "WU-B");
        await svc.CreateAsync(MakeTask("T-1", "WU-A"));
        await svc.CreateAsync(MakeTask("T-2", "WU-B"));

        var list = await svc.ListAsync("WU-A");
        Assert.Single(list);
        Assert.Equal("T-1", list[0].TaskId);
    }
}
