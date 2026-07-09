using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime.Tests;

public class GoalGuardrailServiceTests
{
    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        public List<WorkUnit> Units { get; } = [];

        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Units.FirstOrDefault(u => u.WorkUnitId == workUnitId));

        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(Units.Where(u => u.ParentWorkUnitId == parentId).ToList());

        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(Units.ToList());

        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> AddDependencyAsync(string workUnitId, string dependsOnWorkUnitId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> AmendGoalForSteeredRetryAsync(string workUnitId, string amendedGoal, string steeringContext, string deadLetterEntryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> IncrementFailureAttemptCountAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> IncrementReviewRejectionCountAsync(string workUnitId, bool automated, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeConversationLogService : IConversationLogService
    {
        public Dictionary<string, List<ConversationLogEntry>> EntriesByWorkUnit { get; } = [];

        public Task<ConversationLogEntry> RecordAsync(ConversationLogEntry entry, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ConversationLogEntry>> GetEntriesAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ConversationLogEntry>>(
                EntriesByWorkUnit.TryGetValue(workUnitId, out var list) ? list : []);
    }

    private static WorkUnit MakeWorkUnit(string id, string? parentId, WorkUnitStatus status, DateTimeOffset createdAt) => new(
        WorkUnitId: id,
        Goal: "Goal " + id,
        BranchId: "branch-" + id,
        Status: status,
        CreatedAt: createdAt,
        UpdatedAt: createdAt,
        Owner: "test",
        AssignedAgent: null,
        SuccessCriteria: null,
        Metadata: null,
        ParentWorkUnitId: parentId,
        DependsOn: [],
        FileScope: []);

    private static ConversationLogEntry MakeEntry(string workUnitId, int inputTokens, int outputTokens) => new(
        LogId: Guid.NewGuid().ToString("N"),
        WorkUnitId: workUnitId,
        AgentId: "agent-1",
        AgentRole: "Worker",
        TaskId: null,
        CycleNumber: 0,
        AssistantText: null,
        ToolCalls: [],
        ToolResults: [],
        StopReason: "end_turn",
        OccurredAt: DateTimeOffset.UtcNow,
        InputTokens: inputTokens,
        OutputTokens: outputTokens);

    [Fact]
    public async Task GetStatusAsync_returns_null_for_unknown_work_unit()
    {
        var svc = new GoalGuardrailService(new FakeWorkUnitService(), new FakeConversationLogService(), new WorkspaceOptions());

        var result = await svc.GetStatusAsync("wu-missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetStatusAsync_sums_tokens_across_the_entire_subtree()
    {
        var workUnits = new FakeWorkUnitService();
        var now = DateTimeOffset.UtcNow;
        workUnits.Units.Add(MakeWorkUnit("goal", null, WorkUnitStatus.Executing, now));
        workUnits.Units.Add(MakeWorkUnit("child-1", "goal", WorkUnitStatus.Executing, now));
        workUnits.Units.Add(MakeWorkUnit("child-2", "goal", WorkUnitStatus.Executing, now));
        workUnits.Units.Add(MakeWorkUnit("grandchild-1", "child-1", WorkUnitStatus.Executing, now));

        var conversationLog = new FakeConversationLogService();
        conversationLog.EntriesByWorkUnit["goal"] = [MakeEntry("goal", 100, 50)];
        conversationLog.EntriesByWorkUnit["child-1"] = [MakeEntry("child-1", 200, 100), MakeEntry("child-1", 50, 25)];
        conversationLog.EntriesByWorkUnit["child-2"] = [MakeEntry("child-2", 10, 10)];
        conversationLog.EntriesByWorkUnit["grandchild-1"] = [MakeEntry("grandchild-1", 5, 5)];

        var svc = new GoalGuardrailService(workUnits, conversationLog, new WorkspaceOptions());

        var status = await svc.GetStatusAsync("goal");

        Assert.NotNull(status);
        Assert.Equal(100 + 50 + 200 + 100 + 50 + 25 + 10 + 10 + 5 + 5, status!.TotalTokens);
    }

    [Fact]
    public async Task GetStatusAsync_flags_TokensExceeded_only_once_past_the_configured_cap()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Units.Add(MakeWorkUnit("goal", null, WorkUnitStatus.Executing, DateTimeOffset.UtcNow));

        var conversationLog = new FakeConversationLogService();
        conversationLog.EntriesByWorkUnit["goal"] = [MakeEntry("goal", 600, 500)]; // 1100 total

        var svc = new GoalGuardrailService(workUnits, conversationLog, new WorkspaceOptions { MaxGoalTokens = 1000 });

        var status = await svc.GetStatusAsync("goal");

        Assert.True(status!.TokensExceeded);
        Assert.Equal(1100, status.TotalTokens);
        Assert.Equal(1000, status.MaxGoalTokens);
    }

    [Fact]
    public async Task GetStatusAsync_does_not_flag_TokensExceeded_when_no_cap_is_configured()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Units.Add(MakeWorkUnit("goal", null, WorkUnitStatus.Executing, DateTimeOffset.UtcNow));

        var conversationLog = new FakeConversationLogService();
        conversationLog.EntriesByWorkUnit["goal"] = [MakeEntry("goal", 1_000_000, 1_000_000)];

        var svc = new GoalGuardrailService(workUnits, conversationLog, new WorkspaceOptions()); // MaxGoalTokens null

        var status = await svc.GetStatusAsync("goal");

        Assert.False(status!.TokensExceeded);
    }

    [Fact]
    public async Task GetStatusAsync_flags_DurationExceeded_based_on_elapsed_time_since_CreatedAt()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Units.Add(MakeWorkUnit("goal", null, WorkUnitStatus.Executing, DateTimeOffset.UtcNow.AddMinutes(-90)));

        var svc = new GoalGuardrailService(workUnits, new FakeConversationLogService(), new WorkspaceOptions { MaxGoalDurationMinutes = 60 });

        var status = await svc.GetStatusAsync("goal");

        Assert.True(status!.DurationExceeded);
        Assert.True(status.ElapsedMinutes >= 90);
    }

    [Fact]
    public async Task GetActiveGoalStatusesAsync_excludes_terminal_top_level_goals_and_non_top_level_work_units()
    {
        var workUnits = new FakeWorkUnitService();
        var now = DateTimeOffset.UtcNow;
        workUnits.Units.Add(MakeWorkUnit("active-goal", null, WorkUnitStatus.Executing, now));
        workUnits.Units.Add(MakeWorkUnit("completed-goal", null, WorkUnitStatus.Completed, now));
        workUnits.Units.Add(MakeWorkUnit("merged-goal", null, WorkUnitStatus.Merged, now));
        workUnits.Units.Add(MakeWorkUnit("cancelled-goal", null, WorkUnitStatus.Cancelled, now));
        workUnits.Units.Add(MakeWorkUnit("failed-goal", null, WorkUnitStatus.Failed, now));
        workUnits.Units.Add(MakeWorkUnit("some-child", "active-goal", WorkUnitStatus.Executing, now));

        var svc = new GoalGuardrailService(workUnits, new FakeConversationLogService(), new WorkspaceOptions());

        var statuses = await svc.GetActiveGoalStatusesAsync();

        Assert.Single(statuses);
        Assert.Equal("active-goal", statuses[0].WorkUnitId);
    }
}
