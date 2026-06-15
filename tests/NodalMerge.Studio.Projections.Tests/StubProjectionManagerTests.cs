using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using TaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Projections.Tests;

public class ProjectionManagerTests
{
    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        private readonly Dictionary<string, WorkUnit> _store = new();

        public void Seed(WorkUnit w) => _store[w.WorkUnitId] = w;

        public Task<WorkUnit?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_store.GetValueOrDefault(id));

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(
                _store.Values.Where(w => branchId is null || w.BranchId == branchId).ToList());

        public Task<WorkUnit> CreateAsync(WorkUnit w, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string id, WorkUnitStatus s, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeTaskService : ITaskService
    {
        private readonly List<StudioTask> _store = [];

        public void Seed(StudioTask t) => _store.Add(t);

        public Task<StudioTask?> GetAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult(_store.FirstOrDefault(t => t.TaskId == taskId));

        public Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StudioTask>>(
                _store.Where(t => workUnitId is null || t.WorkUnitId == workUnitId).ToList());

        public Task<StudioTask> CreateAsync(StudioTask t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StudioTask> UpdateAsync(StudioTask t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeMergeService : IMergeService
    {
        private readonly List<MergeProposal> _store = [];

        public void Seed(MergeProposal p) => _store.Add(p);

        public Task<MergeProposal?> GetAsync(string proposalId, CancellationToken ct = default) =>
            Task.FromResult(_store.FirstOrDefault(p => p.ProposalId == proposalId));

        public Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MergeProposal>>(
                _store.Where(p => sourceBranch is null || p.SourceBranch == sourceBranch).ToList());

        public Task<MergeProposal> ProposeAsync(MergeProposal p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> ValidateAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> ReviewAsync(string id, MergeProposalStatus d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> ApplyAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeAgentRuntimeService : IAgentRuntimeService
    {
        private readonly Dictionary<(string, string), ExecutionSnapshot> _store = new();

        public void Seed(ExecutionSnapshot s) => _store[(s.AgentId, s.WorkUnitId)] = s;

        public Task<ExecutionSnapshot> GetSnapshotAsync(string agentId, string workUnitId, CancellationToken ct = default)
        {
            var snapshot = _store.TryGetValue((agentId, workUnitId), out var s)
                ? s
                : new ExecutionSnapshot(agentId, workUnitId, null, null, null, [], [], 0, 0, null);
            return Task.FromResult(snapshot);
        }

        public Task RecordActionAsync(string agentId, string workUnitId, string action, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private static ProjectionManager BuildManager(
        FakeWorkUnitService? workUnits = null,
        FakeTaskService? tasks = null,
        FakeMergeService? merges = null,
        FakeAgentRuntimeService? agentRuntime = null) =>
        new(
            workUnits ?? new FakeWorkUnitService(),
            tasks ?? new FakeTaskService(),
            merges ?? new FakeMergeService(),
            agentRuntime ?? new FakeAgentRuntimeService());

    private static WorkUnit MakeWorkUnit(string id, string goal, string branch = "main", WorkUnitStatus status = WorkUnitStatus.Active) =>
        new(id, goal, branch, status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test", null, null, null);

    private static StudioTask MakeTask(string id, string workUnitId, TaskStatus status = TaskStatus.Open) =>
        new(id, workUnitId, $"Task {id}", "desc", status, null, 1);

    // ── WorkUnit projection ─────────────────────────────────────────────────

    [Fact]
    public async Task WorkUnit_Normal_returns_full_payload()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Implement auth"));
        var tasks = new FakeTaskService();
        tasks.Seed(MakeTask("T-1", "WU-1", TaskStatus.Open));
        tasks.Seed(MakeTask("T-2", "WU-1", TaskStatus.Completed));
        var manager = BuildManager(workUnits, tasks);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Normal, WorkUnitId: "WU-1"));

        Assert.Equal(ProjectionType.WorkUnit, result.Type);
        Assert.Equal(ProjectionLevel.Normal, result.Level);
        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal("Implement auth", doc.GetProperty("goal").GetString());
        Assert.Equal("Active", doc.GetProperty("status").GetString());
        Assert.Equal(1, doc.GetProperty("activeTasks").GetArrayLength());
    }

    [Fact]
    public async Task WorkUnit_Compact_omits_branch_and_successCriteria()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-2", "Refactor DB"));
        var manager = BuildManager(workUnits);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Compact, WorkUnitId: "WU-2"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.True(doc.TryGetProperty("workUnitId", out _));
        Assert.False(doc.TryGetProperty("branchId", out _));
        Assert.False(doc.TryGetProperty("successCriteria", out _));
    }

    [Fact]
    public async Task WorkUnit_Emergency_returns_status_and_count_only()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-3", "Fix crash"));
        var tasks = new FakeTaskService();
        tasks.Seed(MakeTask("T-3", "WU-3", TaskStatus.InProgress));
        var manager = BuildManager(workUnits, tasks);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Emergency, WorkUnitId: "WU-3"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.True(doc.TryGetProperty("activeTaskCount", out var count));
        Assert.Equal(1, count.GetInt32());
        Assert.False(doc.TryGetProperty("goal", out _));
    }

    [Fact]
    public async Task WorkUnit_without_scope_returns_list()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-A", "Goal A", "branch-x"));
        workUnits.Seed(MakeWorkUnit("WU-B", "Goal B", "branch-y"));
        var manager = BuildManager(workUnits);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Normal, BranchId: "branch-x"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal(1, doc.GetProperty("count").GetInt32());
    }

    // ── Task projection ─────────────────────────────────────────────────────

    [Fact]
    public async Task Task_Normal_separates_open_blocked_completed()
    {
        var tasks = new FakeTaskService();
        tasks.Seed(MakeTask("T-1", "WU-X", TaskStatus.Open));
        tasks.Seed(MakeTask("T-2", "WU-X", TaskStatus.Blocked));
        tasks.Seed(MakeTask("T-3", "WU-X", TaskStatus.Completed));
        var manager = BuildManager(tasks: tasks);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.Task, ProjectionLevel.Normal, WorkUnitId: "WU-X"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal(1, doc.GetProperty("openTasks").GetArrayLength());
        Assert.Equal(1, doc.GetProperty("blockedTasks").GetArrayLength());
        Assert.Equal(1, doc.GetProperty("completedTasks").GetArrayLength());
    }

    [Fact]
    public async Task Task_Emergency_returns_count_and_next_task()
    {
        var tasks = new FakeTaskService();
        tasks.Seed(MakeTask("T-A", "WU-Y", TaskStatus.Open));
        tasks.Seed(MakeTask("T-B", "WU-Y", TaskStatus.Open));
        var manager = BuildManager(tasks: tasks);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.Task, ProjectionLevel.Emergency, WorkUnitId: "WU-Y"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal(2, doc.GetProperty("activeCount").GetInt32());
        Assert.True(doc.TryGetProperty("nextTask", out _));
    }

    // ── MergeProposal projection ────────────────────────────────────────────

    [Fact]
    public async Task MergeProposal_Normal_lists_pending_proposals()
    {
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal("MP-1", "feat/x", "main", "Auth feature", "summary", "desc", null, null, null, MergeProposalStatus.ReadyForReview));
        merges.Seed(new MergeProposal("MP-2", "feat/x", "main", "DB fix", "summary", "desc", null, null, null, MergeProposalStatus.Merged));
        var manager = BuildManager(merges: merges);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.MergeProposal, ProjectionLevel.Normal, BranchId: "feat/x"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal(1, doc.GetProperty("pendingProposals").GetArrayLength());
        Assert.Equal(2, doc.GetProperty("reviewStatus").EnumerateObject().Count());
    }

    // ── ExecutionSnapshot projection ────────────────────────────────────────

    [Fact]
    public async Task ExecutionSnapshot_Normal_returns_agent_state()
    {
        var agentRuntime = new FakeAgentRuntimeService();
        agentRuntime.Seed(new ExecutionSnapshot("agent-1", "WU-Z", "Implement login", null, null, ["step1", "step2"], [], 0, 0, null));
        var manager = BuildManager(agentRuntime: agentRuntime);

        var result = await manager.GetAsync(new ProjectionRequest(
            ProjectionType.ExecutionSnapshot, ProjectionLevel.Normal, WorkUnitId: "WU-Z", AgentId: "agent-1"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal("Implement login", doc.GetProperty("currentGoal").GetString());
        Assert.Equal(2, doc.GetProperty("failureHistory").GetArrayLength());
    }

    [Fact]
    public async Task ExecutionSnapshot_without_agentId_returns_error()
    {
        var manager = BuildManager();

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.ExecutionSnapshot, ProjectionLevel.Normal));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.True(doc.TryGetProperty("error", out _));
    }

    // ── Routing ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProjectionType.WorkUnit, ProjectionLevel.Full)]
    [InlineData(ProjectionType.Task, ProjectionLevel.Normal)]
    [InlineData(ProjectionType.MergeProposal, ProjectionLevel.Compact)]
    [InlineData(ProjectionType.AuthoritativeState, ProjectionLevel.Emergency)]
    [InlineData(ProjectionType.ExecutionSnapshot, ProjectionLevel.Normal)]
    public async Task GetAsync_routes_all_types_without_throwing(ProjectionType type, ProjectionLevel level)
    {
        var result = await BuildManager().GetAsync(new ProjectionRequest(type, level));
        Assert.Equal(type, result.Type);
        Assert.Equal(level, result.Level);
        Assert.NotEmpty(result.DataJson);
    }
}
