using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime.Tests;

// Covers ContinueService's guard clauses (no LLM call needed) plus ReconstructTurns' pure
// conversion logic directly. The full happy-path — reconstruction actually feeding a resumed
// WorkerAgentLoop, and it picking up where it left off — is covered as an integration test
// instead (NodalMerge.Studio.Integration.Tests), since it needs the whole DI graph.
public class ContinueServiceTests
{
    private sealed class FakeDeadLetterService : IDeadLetterService
    {
        public DeadLetterEntry? EntryToReturn;
        public List<(string Reason, FailureKind Kind)> RecordedFailures { get; } = [];

        public Task<DeadLetterEntry> RecordFailureAsync(
            string workUnitId, string agentId, PipelineStage stage, string profileId, string reason,
            string? taskId = null, string? lastProjectionSnapshot = null, string? sessionId = null,
            string? model = null, string? baseUrl = null, string? apiKey = null, string? provider = null,
            FailureKind kind = FailureKind.Exception, CancellationToken cancellationToken = default)
        {
            RecordedFailures.Add((reason, kind));
            return Task.FromResult(new DeadLetterEntry(
                "DL-new", workUnitId, agentId, stage, profileId, reason, null, 1, DateTimeOffset.UtcNow, taskId,
                Kind: kind));
        }

        public Task<DeadLetterEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(EntryToReturn);

        public Task<DeadLetterEntry?> GetLatestForWorkUnitAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DeadLetterEntry?>(null);

        public Task<IReadOnlyList<DeadLetterEntry>> GetHistoryForWorkUnitAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);

        public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);

        public Task<DeadLetterRetryResult> RetryAsync(string entryId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeadLetterRetryResult> RetryWithCredentialOverrideAsync(
            string entryId, string? overrideModel, string? overrideBaseUrl, string? overrideApiKey,
            string? overrideProvider, string? overrideProfileId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeadLetterRetryResult> RetryWithContextAsync(
            string entryId, string steeringContext, string? overrideModel = null, string? overrideBaseUrl = null,
            string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        public WorkUnit? UnitToReturn;

        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(UnitToReturn!);

        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? reason, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(UnitToReturn);

        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);

        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);

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

    private sealed class FakeAgentControlService : IAgentControlService
    {
        public OrchestratorCredentials? CredentialsToReturn;

        public Task<string> SpawnAsync(string agentType, string workUnitId, string? taskId = null, string? model = null,
            string? baseUrl = null, string? apiKey = null, string? provider = null, string? profileId = null,
            string? autoReviewProfileId = null, IReadOnlyDictionary<PipelineStage, OrchestratorCredentials>? stageCredentials = null,
            IReadOnlyList<string>? enabledDomainAgents = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId) => null;
        public OrchestratorCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage) => CredentialsToReturn;
        public OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId) => CredentialsToReturn;
        public string? GetAutoReviewProfileId(string workUnitId) => null;
        public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default) => Task.FromResult("unknown");
        public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
        public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
    }

    private sealed class NoopServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static WorkUnit MakeWorkUnit(string workUnitId) => new(
        WorkUnitId: workUnitId,
        Goal: "Do the thing",
        BranchId: $"branch-{workUnitId}",
        Status: WorkUnitStatus.DeadLettered,
        CreatedAt: DateTimeOffset.UtcNow,
        UpdatedAt: DateTimeOffset.UtcNow,
        Owner: "test",
        AssignedAgent: null,
        SuccessCriteria: null,
        Metadata: null,
        ParentWorkUnitId: null,
        DependsOn: [],
        FileScope: []);

    private static ContinueService Build(
        FakeDeadLetterService? deadLetter = null,
        FakeWorkUnitService? workUnits = null,
        FakeAgentControlService? agentControl = null) =>
        new(
            deadLetter ?? new FakeDeadLetterService(),
            workUnits ?? new FakeWorkUnitService(),
            agentControl ?? new FakeAgentControlService(),
            new NoopServiceProvider());

    [Fact]
    public async Task ContinueWithPriorContextAsync_returns_NotFound_when_entry_does_not_exist()
    {
        var svc = Build(new FakeDeadLetterService { EntryToReturn = null });

        var result = await svc.ContinueWithPriorContextAsync("DL-missing");

        Assert.Equal(ContinueOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task ContinueWithPriorContextAsync_returns_NotApplicable_for_non_MaxIterationsExceeded_kind()
    {
        var entry = new DeadLetterEntry(
            "DL-1", "wu-1", "worker-1", PipelineStage.Execute, "worker", "boom",
            null, 1, DateTimeOffset.UtcNow, Kind: FailureKind.Exception);
        var svc = Build(new FakeDeadLetterService { EntryToReturn = entry });

        var result = await svc.ContinueWithPriorContextAsync("DL-1");

        Assert.Equal(ContinueOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task ContinueWithPriorContextAsync_returns_NotCompleted_when_no_credentials_resolvable()
    {
        var entry = new DeadLetterEntry(
            "DL-2", "wu-1", "worker-1", PipelineStage.Execute, "worker", "max iterations",
            null, 1, DateTimeOffset.UtcNow, Kind: FailureKind.MaxIterationsExceeded);
        var workUnits = new FakeWorkUnitService { UnitToReturn = MakeWorkUnit("wu-1") };
        var agentControl = new FakeAgentControlService { CredentialsToReturn = null };
        var svc = Build(new FakeDeadLetterService { EntryToReturn = entry }, workUnits, agentControl);

        var result = await svc.ContinueWithPriorContextAsync("DL-2");

        Assert.Equal(ContinueOutcome.NotCompleted, result.Outcome);
    }

    [Fact]
    public void ReconstructTurns_rebuilds_assistant_and_tool_result_pairs_per_cycle()
    {
        var entries = new List<ConversationLogEntry>
        {
            new("log-1", "wu-1", "agent-1", "Worker", "task-1", CycleNumber: 0,
                AssistantText: "Let me check the file.",
                ToolCalls: [new ConversationToolCall("tu-1", "nm_v1_workspace_read", "{\"path\":\"Foo.cs\"}")],
                ToolResults: [new ConversationToolResult("tu-1", "class Foo {}", Truncated: false)],
                StopReason: "tool_use", OccurredAt: DateTimeOffset.UtcNow),
            new("log-2", "wu-1", "agent-1", "Worker", "task-1", CycleNumber: 1,
                AssistantText: null,
                ToolCalls: [new ConversationToolCall("tu-2", "nm_v1_workspace_write", "{\"path\":\"Foo.cs\",\"content\":\"class Foo { public int X; }\"}")],
                ToolResults: [new ConversationToolResult("tu-2", "ok", Truncated: false)],
                StopReason: "tool_use", OccurredAt: DateTimeOffset.UtcNow),
        };

        var turns = ContinueService.ReconstructTurns(entries);

        Assert.Equal(4, turns.Count); // assistant, user, assistant, user
        Assert.Equal("assistant", turns[0].Role);
        Assert.Equal("user", turns[1].Role);
        Assert.Equal("assistant", turns[2].Role);
        Assert.Equal("user", turns[3].Role);

        var firstAssistantContent = turns[0].Content;
        Assert.Contains(firstAssistantContent, c => c is NmText t && t.Text == "Let me check the file.");
        Assert.Contains(firstAssistantContent, c => c is NmToolUse tu && tu.Id == "tu-1" && tu.Name == "nm_v1_workspace_read");

        var firstUserContent = turns[1].Content;
        var toolResult = Assert.IsType<NmToolResult>(Assert.Single(firstUserContent));
        Assert.Equal("tu-1", toolResult.ToolUseId);
        Assert.Equal("class Foo {}", toolResult.Result);

        // Second cycle had no AssistantText — its assistant turn should carry only the tool use.
        var secondAssistantContent = turns[2].Content;
        Assert.Single(secondAssistantContent);
        Assert.IsType<NmToolUse>(secondAssistantContent[0]);
    }

    [Fact]
    public void ReconstructTurns_returns_empty_list_for_no_entries()
    {
        var turns = ContinueService.ReconstructTurns([]);

        Assert.Empty(turns);
    }
}
