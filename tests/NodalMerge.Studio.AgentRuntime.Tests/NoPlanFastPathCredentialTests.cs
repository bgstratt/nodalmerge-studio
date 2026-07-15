using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime.Tests;

// Fix 1 — when a Planner records no plan for an atomic goal, the work unit is handed straight
// to Execute (the "needsExecuteFallback" fast path in RunScheduledWorkerAsync). That re-enqueue
// must re-resolve the Execute-stage/worker profile's credentials rather than reusing the
// Planner's own queue-item credentials verbatim — every other enqueue site in the system already
// resolves stage creds this way (FanOutService, AutomatedReviewGateService, ContinueService,
// InlineReviewerService, ReplanService, dead-letter retry); this fast path was the lone exception.
public class NoPlanFastPathCredentialTests
{
    // ── Fakes ────────────────────────────────────────────────────────────────

    // Hands out a single planner-stage ScheduledItem on the first TryAcquireAsync call (mirroring
    // the real scheduler's "planner recorded no plan" hand-off to Execute), then goes quiet.
    // Captures whatever EnqueueAsync is called with next, via a TaskCompletionSource so the test
    // can await the fire-and-forget scheduler-poll loop without a sleep-and-hope race.
    private sealed class SpyScheduler : IWorkScheduler
    {
        private readonly ScheduledItem _itemToServe;
        private int _served;

        public readonly TaskCompletionSource<(string? Model, string? BaseUrl, string? ApiKey, string? Provider, string? CredentialRef)> EnqueueCall = new();

        public SpyScheduler(ScheduledItem itemToServe) => _itemToServe = itemToServe;

        public Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult(Interlocked.Exchange(ref _served, 1) == 0 ? _itemToServe : null);

        public Task EnqueueAsync(string workUnitId, string profileId, string? taskId = null, string? model = null,
            string? baseUrl = null, string? apiKey = null, string? provider = null, string? sessionId = null,
            string? credentialRef = null, CancellationToken ct = default)
        {
            EnqueueCall.TrySetResult((model, baseUrl, apiKey, provider, credentialRef));
            return Task.CompletedTask;
        }

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

    private sealed class FakeAgentProfileService : IAgentProfileService
    {
        private readonly AgentProfile _profile;
        public FakeAgentProfileService(AgentProfile profile) => _profile = profile;

        public Task<AgentProfile> CreateAsync(AgentProfile profile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<AgentProfile?> GetAsync(string profileId, CancellationToken ct = default) =>
            Task.FromResult<AgentProfile?>(profileId == _profile.AgentProfileId ? _profile : null);
        public Task<AgentProfile> UpdateAsync(AgentProfile profile, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentProfile>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentProfile>>([_profile]);
    }

    // A Plan-stage harness executor that always reports a clean "Succeeded" turn — the
    // "Planner correctly concludes nothing to decompose" case (no Plan artifact recorded) that
    // triggers needsExecuteFallback.
    private sealed class NoopPlanHarnessExecutor : IHarnessExecutor
    {
        public string Name => "native";
        public string DisplayName => "Native";
        public string? ProviderKey => null;
        public HarnessCapabilities Capabilities => new(
            SupportsTurnTelemetry: false, SupportsResume: false, SupportsHooks: false,
            SupportsSubagents: false, SupportsMcp: false, SupportsPlanningMode: true);
        public Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default) =>
            Task.FromResult(new HarnessRunResult(AgentLoopCompletion.Succeeded));
    }

    private sealed class FakeHarnessExecutorResolver : IHarnessExecutorResolver
    {
        private readonly IHarnessExecutor _executor = new NoopPlanHarnessExecutor();
        public IHarnessExecutor Resolve(string? executorName) => _executor;
        public bool IsCliProvider(string? provider) => false;
        public IHarnessExecutor ResolveForProvider(string? provider, string? profileExecutor) => _executor;
    }

    // Only IHarnessExecutorResolver is real here — every context-builder helper
    // (BuildConstraintsContextAsync, BuildPromptGuidanceContextAsync, BuildEngineeringStateContextAsync,
    // BuildRuleFileContextAsync) wraps its GetRequiredService lookups in try/catch and degrades to
    // null, so a bare "everything else is unregistered" provider is sufficient.
    private sealed class FakeServiceProvider(IHarnessExecutorResolver resolver) : IServiceProvider
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IHarnessExecutorResolver) ? resolver : null;
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
        public Task<IReadOnlyList<ExecutionEvent>> GetEventsByKindAsync(
            IReadOnlyList<ExecutionEventKind> kinds, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ExecutionEvent>>([]);
    }

    [Fact]
    public async Task NoPlan_fast_path_reenqueues_with_ExecuteStage_credentials_not_planners()
    {
        const string workUnitId = "wu-atomic-1";
        const string plannerProfileId = "planner-profile";

        var plannerProfile = new AgentProfile(
            plannerProfileId, "Planner", PipelineStage.Plan, "system prompt",
            AllowedTools: [], MaxIterations: 10, FileScopePatterns: []);

        // The scheduler hands the poller a Plan-stage item carrying the *planner's* model/creds.
        var plannerItem = new ScheduledItem(
            workUnitId, plannerProfileId, TaskId: null, LeasedBy: "poller-1", LeasedAt: DateTimeOffset.UtcNow,
            AttemptCount: 0, Model: "planner-model", BaseUrl: "http://planner", ApiKey: "planner-key",
            Provider: "anthropic");

        var scheduler = new SpyScheduler(plannerItem);
        var resolver = new FakeHarnessExecutorResolver();
        var svc = new InMemoryAgentRuntimeService(
            new FakeServiceProvider(resolver),
            NullLogger<InMemoryAgentRuntimeService>.Instance,
            new FakeAgentProfileService(plannerProfile),
            scheduler,
            new NoopEventStream(),
            new WorkspaceOptions { SchedulerPollIntervalMs = 10 },
            new NoopFileLeaseService(),
            new InMemoryStudioNodeStore(),
            new RuntimeCredentialCache());

        // Register a distinct Execute-stage (worker) profile via the same channel every other
        // enqueue site reads from (GetCredentialsForStage/GetGoalDefaultCredentials) — this is the
        // "configured worker profile" that the bug silently ignored.
        await svc.SpawnAsync(
            "orchestrator", workUnitId,
            model: "planner-model", baseUrl: "http://planner", apiKey: "planner-key", provider: "anthropic",
            stageCredentials: new Dictionary<PipelineStage, GoalDefaultCredentials>
            {
                [PipelineStage.Execute] = new GoalDefaultCredentials(
                    "openai", "worker-model", "http://worker", "worker-key", ProfileId: null, CredentialRef: "worker-cred-ref"),
            });

        await svc.StartAsync(CancellationToken.None);
        try
        {
            var enqueued = await scheduler.EnqueueCall.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("worker-model", enqueued.Model);
            Assert.Equal("http://worker", enqueued.BaseUrl);
            Assert.Equal("worker-key", enqueued.ApiKey);
            Assert.Equal("openai", enqueued.Provider);
            Assert.Equal("worker-cred-ref", enqueued.CredentialRef);
        }
        finally
        {
            await svc.StopAsync(CancellationToken.None);
        }
    }
}
