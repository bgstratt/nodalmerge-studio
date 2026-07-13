using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime.Tests;

// Covers ReplanService's guard clauses — every branch that returns before ever spawning a
// PlannerAgentLoop (and therefore before needing a real or faked LLM call). The full
// "Replanned" happy path needs a real planner turn + fan-out, which is covered as an
// integration test instead (NodalMerge.Studio.Integration.Tests) since it needs the whole DI
// graph wired up, not just these four services.
public class ReplanServiceTests
{
    private sealed class FakeDeadLetterService : IDeadLetterService
    {
        public DeadLetterEntry? EntryToReturn;

        public Task<DeadLetterEntry> RecordFailureAsync(
            string workUnitId, string agentId, PipelineStage stage, string profileId, string reason,
            string? taskId = null, string? lastProjectionSnapshot = null, string? sessionId = null,
            string? model = null, string? baseUrl = null, string? apiKey = null, string? provider = null,
            FailureKind kind = FailureKind.Exception, string? credentialRef = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

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
            string? overrideProvider, string? overrideProfileId, string? overrideCredentialRef = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DeadLetterRetryResult> RetryWithContextAsync(
            string entryId, string steeringContext, string? overrideModel = null, string? overrideBaseUrl = null,
            string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null,
            string? overrideCredentialRef = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        public WorkUnit? UnitToReturn;
        public string? StatusUpdatedTo;

        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken cancellationToken = default)
        {
            StatusUpdatedTo = status.ToString();
            return Task.FromResult(UnitToReturn!);
        }

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

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? owner = null, CancellationToken cancellationToken = default) =>
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
        public GoalDefaultCredentials? CredentialsToReturn;

        public Task<string> SpawnAsync(string agentType, string workUnitId, string? taskId = null, string? model = null,
            string? baseUrl = null, string? apiKey = null, string? provider = null, string? profileId = null,
            string? autoReviewProfileId = null, IReadOnlyDictionary<PipelineStage, GoalDefaultCredentials>? stageCredentials = null,
            IReadOnlyList<string>? enabledDomainAgents = null, string? credentialRef = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, string? overrideModel = null, string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null, string? overrideCredentialRef = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> ResupplyCredentialsAsync(string workUnitId, string? overrideModel = null, string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null, string? overrideCredentialRef = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId) => null;
        public GoalDefaultCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage) => CredentialsToReturn;
        public GoalDefaultCredentials? GetGoalDefaultCredentials(string workUnitId) => CredentialsToReturn;
        public string? GetAutoReviewProfileId(string workUnitId) => null;
        public string? GetGoalDefaultProfileId(string workUnitId) => null;
        public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default) => Task.FromResult("unknown");
        public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
        public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AgentInfo>>([]);
        public Task<TResult> TrackInlineAgentAsync<TResult>(string agentId, string workUnitId, string? taskId, Func<Action<string?>, Task<TResult>> run, CancellationToken cancellationToken = default) =>
            run(_ => { });
    }

    private sealed class FakeFanOutService : IFanOutService
    {
        public Task<FanOutResult> TryFanOutFromPlanAsync(string parentWorkUnitId, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<FanOutResult> TryEnqueueReadyDependentsAsync(string parentWorkUnitId, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private sealed class NoopFileLeaseService : IFileLeaseService
    {
        public Task<(bool Granted, string? HolderWorkUnitId)> TryAcquireOrEnqueueAsync(
            string workUnitId, string path, CancellationToken ct = default) =>
            Task.FromResult<(bool Granted, string? HolderWorkUnitId)>((true, workUnitId));
        public Task<string?> ReleaseAndAdvanceAsync(string workUnitId, string path, CancellationToken ct = default) =>
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
            string? credentialRef = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<ScheduledItem?>(null);
        public Task ReleaseAsync(string workUnitId, bool success, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAwaitingResumeAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAwaitingFileLeaseAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAwaitingFileLeaseAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkAwaitingCredentialsAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task SupplyCredentialsAsync(string workUnitId, string? provider, string? model, string? baseUrl, string? apiKey, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledItem>>([]);
        public Task<IReadOnlyList<ScheduledItem>> ListAwaitingResumeAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledItem>>([]);
        public Task ApproveResumeAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> ApproveResumeAllAsync(CancellationToken ct = default) => Task.FromResult(0);
        public Task ForceResumeAsync(string workUnitId, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static WorkUnit MakeWorkUnit(string workUnitId, string? parentWorkUnitId) => new(
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
        ParentWorkUnitId: parentWorkUnitId,
        DependsOn: [],
        FileScope: []);

    private static ReplanService Build(
        FakeDeadLetterService? deadLetter = null,
        FakeWorkUnitService? workUnits = null,
        FakeAgentControlService? agentControl = null) =>
        new(
            deadLetter ?? new FakeDeadLetterService(),
            workUnits ?? new FakeWorkUnitService(),
            new FakeFanOutService(),
            agentControl ?? new FakeAgentControlService(),
            new NoopServiceProvider(),
            new NoopFileLeaseService(),
            new NoopScheduler());

    [Fact]
    public async Task ReplanFailedSliceAsync_returns_NotFound_when_entry_does_not_exist()
    {
        var svc = Build(new FakeDeadLetterService { EntryToReturn = null });

        var result = await svc.ReplanFailedSliceAsync("DL-missing");

        Assert.Equal(ReplanOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task ReplanFailedSliceAsync_proceeds_even_when_the_entry_already_exhausted_max_attempts()
    {
        // Deliberate: re-planning never resumes the failed work unit, so the retry-attempt cap
        // (which exists to stop retrying the *same* mistake) must not block it — an exhausted
        // entry is, if anything, the clearest case for reaching for this over plain retry.
        var entry = new DeadLetterEntry(
            "DL-1", "wu-failed", "worker-1", PipelineStage.Execute, "worker", "boom",
            null, 3, DateTimeOffset.UtcNow, MaxAttemptsReached: true);
        var workUnits = new FakeWorkUnitService { UnitToReturn = MakeWorkUnit("wu-failed", parentWorkUnitId: null) };
        var svc = Build(new FakeDeadLetterService { EntryToReturn = entry }, workUnits);

        var result = await svc.ReplanFailedSliceAsync("DL-1");

        // Proceeds past the (removed) attempts check and reaches the next real guard clause
        // (NotApplicable, since this fake work unit has no parent) rather than being rejected
        // outright for having exhausted its attempts.
        Assert.Equal(ReplanOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task ReplanFailedSliceAsync_returns_NotApplicable_when_failed_work_unit_has_no_parent()
    {
        var entry = new DeadLetterEntry(
            "DL-2", "wu-top-level", "worker-1", PipelineStage.Execute, "worker", "boom",
            null, 1, DateTimeOffset.UtcNow);
        var workUnits = new FakeWorkUnitService { UnitToReturn = MakeWorkUnit("wu-top-level", parentWorkUnitId: null) };
        var svc = Build(new FakeDeadLetterService { EntryToReturn = entry }, workUnits);

        var result = await svc.ReplanFailedSliceAsync("DL-2");

        Assert.Equal(ReplanOutcome.NotApplicable, result.Outcome);
    }

    [Fact]
    public async Task ReplanFailedSliceAsync_returns_PlanningFailed_when_no_credentials_resolvable()
    {
        var entry = new DeadLetterEntry(
            "DL-3", "wu-child", "worker-1", PipelineStage.Execute, "worker", "boom",
            null, 1, DateTimeOffset.UtcNow);
        var workUnits = new FakeWorkUnitService { UnitToReturn = MakeWorkUnit("wu-child", parentWorkUnitId: "wu-parent") };
        var agentControl = new FakeAgentControlService { CredentialsToReturn = null };
        var svc = Build(new FakeDeadLetterService { EntryToReturn = entry }, workUnits, agentControl);

        var result = await svc.ReplanFailedSliceAsync("DL-3");

        Assert.Equal(ReplanOutcome.PlanningFailed, result.Outcome);
    }
}
