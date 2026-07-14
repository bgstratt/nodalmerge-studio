using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.McpServer.Tools;
using TaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Tasks.Tests;

// Fix 3 — nm_v1_task_update must return a clean no-op success (not an error) when the id is a
// known work unit with no task record: the no-plan direct-execution path hands the worker no
// task, but the generic worker prompt still tells it to call task_update, so it passes its
// workUnitId as the taskId.
public class TaskToolsTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    private sealed class FakeTaskCommandService : ITaskCommandService
    {
        private readonly Dictionary<string, StudioTask> _tasks = new();

        public void Seed(StudioTask task) => _tasks[task.TaskId] = task;

        public Task<StudioTask> CreateAsync(TaskCreateCommand command, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StudioTask> UpdateAsync(
            string taskId,
            string? title = null,
            string? description = null,
            string? status = null,
            int? priority = null,
            CancellationToken cancellationToken = default)
        {
            if (!_tasks.TryGetValue(taskId, out var existing))
                throw new KeyNotFoundException($"Task '{taskId}' was not found.");

            var updated = existing with
            {
                Title = title ?? existing.Title,
                Description = description ?? existing.Description,
                Status = status is not null ? Enum.Parse<TaskStatus>(status, ignoreCase: true) : existing.Status,
                Priority = priority ?? existing.Priority
            };
            _tasks[taskId] = updated;
            return Task.FromResult(updated);
        }

        public Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        private readonly Dictionary<string, WorkUnit> _store = new();

        public void Seed(string workUnitId) =>
            _store[workUnitId] = new WorkUnit(workUnitId, "goal", "branch", WorkUnitStatus.Active,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner", null, null, null, null, [], []);

        public Task<WorkUnit?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_store.GetValueOrDefault(id));

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

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
        public Task<WorkUnit> AddDependencyAsync(string workUnitId, string dependsOnWorkUnitId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static StudioTask MakeTask(string taskId, string workUnitId, TaskStatus status = TaskStatus.Open) =>
        new(taskId, workUnitId, $"Task {taskId}", "desc", status, null, 0);

    private static (TaskTools tools, FakeTaskCommandService tasks, FakeWorkUnitService workUnits) Build()
    {
        var tasks = new FakeTaskCommandService();
        var workUnits = new FakeWorkUnitService();
        return (new TaskTools(tasks, workUnits), tasks, workUnits);
    }

    // ── Existing behavior preserved ─────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_updates_a_real_task()
    {
        var (tools, tasks, _) = Build();
        tasks.Seed(MakeTask("T-1", "WU-1"));

        var json = await tools.UpdateAsync("T-1", status: "InProgress");
        using var doc = JsonDocument.Parse(json);

        Assert.False(doc.RootElement.TryGetProperty("status", out var statusProp) && statusProp.GetString() == "error");
        // Default JsonSerializerOptions (no naming policy, no string-enum converter) preserves the
        // record's PascalCase names and serializes the enum as its underlying numeric value.
        Assert.Equal((int)TaskStatus.InProgress, doc.RootElement.GetProperty("data").GetProperty("Status").GetInt32());
    }

    [Fact]
    public async Task UpdateAsync_errors_for_genuinely_unknown_id()
    {
        var (tools, _, _) = Build();
        // Not seeded as a task or a work unit — genuinely unknown.

        var json = await tools.UpdateAsync("no-such-id", status: "InProgress");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        Assert.Contains("no-such-id", doc.RootElement.GetProperty("message").GetString());
    }

    // ── Fix 3 ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_is_clean_noop_when_id_is_a_workUnitId_with_no_task()
    {
        var (tools, _, workUnits) = Build();
        workUnits.Seed("WU-no-task");
        // No task seeded under "WU-no-task" — mirrors the no-plan direct-execution path where the
        // worker prompt passes its workUnitId as the taskId and there's no task record at all.

        var json = await tools.UpdateAsync("WU-no-task", status: "InProgress");
        using var doc = JsonDocument.Parse(json);

        // Must be a success envelope (no "status: error"), not the "Task '...' was not found" error.
        Assert.False(doc.RootElement.TryGetProperty("status", out _));
        Assert.False(doc.RootElement.GetProperty("data").GetProperty("updated").GetBoolean());
    }
}
