using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/harness-hosting-architecture.md Phase B.1 — proves the executor seam is actually wired
/// into the real Worker spawn path (not just unit-testable in isolation, per
/// HarnessExecutorResolverTests): an AgentProfile.Executor value routes a Worker spawn to a
/// custom-registered IHarnessExecutor instead of the native loop.
///
/// This exercises StartWorkerLoop (the direct-spawn site — IAgentControlService.SpawnAsync with
/// a non-null taskId, e.g. the orchestrator's own nm_v1_agent_spawn tool call), NOT
/// RunScheduledWorkerAsync (the queue-driven site). The two sites have different downstream
/// bookkeeping: StartWorkerLoop discards HarnessRunResult entirely (no VerifyWorkerProgressAsync,
/// no scheduler/dead-letter interaction — see its own comment), so this test's task-completion
/// assertion only confirms the OnRun side effect actually ran, not any scheduler-side behavior.
/// RunScheduledWorkerAsync's seam call is currently verified only by code symmetry with this site
/// plus the full suite staying green with the native (default) executor — no dedicated test
/// spawns a custom IHarnessExecutor through the scheduler queue yet.
/// </summary>
[Trait("Category", "Integration")]
public class HarnessExecutorSeamIntegrationTests
{
    private sealed class FakeHarnessExecutor : IHarnessExecutor
    {
        public string Name => "fake-harness";
        public string DisplayName => "Fake Harness";
        public string? ProviderKey => null;
        public HarnessCapabilities Capabilities => new(false, false, false, false, false, false);
        public int InvocationCount { get; private set; }
        public HarnessRunRequest? LastRequest { get; private set; }

        // Real work: mimics what a Worker-role executor is supposed to do (complete the task) so
        // VerifyWorkerProgressAsync's downstream check treats this as genuine progress the same
        // way it would for a native run's task.update tool call.
        public Func<HarnessRunRequest, CancellationToken, Task>? OnRun { get; set; }

        public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default)
        {
            InvocationCount++;
            LastRequest = request;
            if (OnRun is not null)
                await OnRun(request, ct).ConfigureAwait(false);
            return new HarnessRunResult(AgentLoopCompletion.Succeeded);
        }
    }

    [Fact]
    public async Task Worker_spawn_with_a_registered_executor_profile_routes_to_that_executor_not_native()
    {
        var fakeExecutor = new FakeHarnessExecutor();

        await using var app = StudioWebApplication.Build(
            [],
            // Required by StudioWebApplication.Build but never actually exercised here — since
            // the profile resolves to "fake-harness", NativeHarnessExecutor (the only thing that
            // would ever call into this HTTP client) is never invoked at all.
            llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton<IHarnessExecutor>(fakeExecutor);
            });

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        var tasks = app.Services.GetRequiredService<ITaskService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the executor seam", "integration-test");
        var task = await tasks.CreateAsync(new StudioTask(
            Guid.NewGuid().ToString("N"), wu.WorkUnitId, wu.Goal, "Execute", StudioTaskStatus.Open, null, 0));

        var profile = await profiles.CreateAsync(new AgentProfile(
            "fake-harness-profile", "Fake Harness Worker", PipelineStage.Execute,
            SystemPrompt: "unused", AllowedTools: [], MaxIterations: 5, FileScopePatterns: [],
            Executor: "fake-harness"));

        fakeExecutor.OnRun = async (request, ct) =>
        {
            // TaskTransitions.CanTransition has no direct Open → Completed edge — must pass
            // through InProgress first, same as a real worker's own task.update tool calls would.
            await tasks.UpdateAsync(task with { Status = StudioTaskStatus.InProgress }, ct).ConfigureAwait(false);
            await tasks.UpdateAsync(task with { Status = StudioTaskStatus.Completed }, ct).ConfigureAwait(false);
        };

        await agentControl.SpawnAsync(
            agentType: "worker",
            workUnitId: wu.WorkUnitId,
            taskId: task.TaskId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key",
            profileId: profile.AgentProfileId);

        // Poll for the OnRun-driven task completion, not just InvocationCount — InvocationCount
        // increments before OnRun is awaited, so waiting on it alone races the update below.
        StudioTask? updatedTask = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            updatedTask = await tasks.GetAsync(task.TaskId);
            if (updatedTask?.Status == StudioTaskStatus.Completed) break;
            await Task.Delay(25);
        }

        Assert.Equal(1, fakeExecutor.InvocationCount);
        Assert.Equal(wu.WorkUnitId, fakeExecutor.LastRequest!.WorkUnitId);
        Assert.Equal(task.TaskId, fakeExecutor.LastRequest.TaskId);
        Assert.Equal("fake-harness", fakeExecutor.LastRequest.Profile?.Executor);
        Assert.Equal(StudioTaskStatus.Completed, updatedTask!.Status);
    }

    /// <summary>
    /// The RunScheduledWorkerAsync (queue-driven) counterpart to the direct-spawn test above —
    /// closes the gap that test's own docstring calls out. Drives a real ScheduledItem through
    /// IWorkScheduler.EnqueueAsync and PollSchedulerAsync's background hosted-service loop (not a
    /// direct SpawnAsync call), so this exercises the site that actually calls
    /// VerifyWorkerProgressAsync and the scheduler release/dead-letter bookkeeping downstream of
    /// the seam.
    /// </summary>
    [Fact]
    public async Task Queue_driven_worker_spawn_with_a_registered_executor_profile_routes_to_that_executor_and_releases_on_success()
    {
        var fakeExecutor = new FakeHarnessExecutor();

        await using var app = StudioWebApplication.Build(
            [],
            // StartAsync below is only needed for the hosted-service scheduler loop — without
            // UseTestServer it would also bind real Kestrel on the default port 5000, which
            // collides (AddressInUseException) with any parallel test class doing the same.
            configureWebHost: webHost => webHost.UseTestServer(),
            llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton<IHarnessExecutor>(fakeExecutor);
                // Faster than WorkspaceOptions' 2s default so the test doesn't wait unnecessarily
                // for PollSchedulerAsync's next tick — last registration wins (see
                // AddInMemoryStorage's own comment on why this must be a real AddSingleton, not
                // TryAdd).
                services.AddSingleton(new WorkspaceOptions { SchedulerPollIntervalMs = 50 });
            });
        await app.StartAsync();

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();
        var tasks = app.Services.GetRequiredService<ITaskService>();
        var deadLetter = app.Services.GetRequiredService<IDeadLetterService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the queue-driven executor seam", "integration-test");
        var task = await tasks.CreateAsync(new StudioTask(
            Guid.NewGuid().ToString("N"), wu.WorkUnitId, wu.Goal, "Execute", StudioTaskStatus.Open, null, 0));

        var profile = await profiles.CreateAsync(new AgentProfile(
            "fake-harness-queue-profile", "Fake Harness Worker (queue)", PipelineStage.Execute,
            SystemPrompt: "unused", AllowedTools: [], MaxIterations: 5, FileScopePatterns: [],
            Executor: "fake-harness"));

        fakeExecutor.OnRun = async (request, ct) =>
        {
            await tasks.UpdateAsync(task with { Status = StudioTaskStatus.InProgress }, ct).ConfigureAwait(false);
            await tasks.UpdateAsync(task with { Status = StudioTaskStatus.Completed }, ct).ConfigureAwait(false);
        };

        await scheduler.EnqueueAsync(
            wu.WorkUnitId, profile.AgentProfileId, taskId: task.TaskId,
            model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key");

        StudioTask? updatedTask = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            updatedTask = await tasks.GetAsync(task.TaskId);
            if (updatedTask?.Status == StudioTaskStatus.Completed) break;
            await Task.Delay(25);
        }

        Assert.Equal(1, fakeExecutor.InvocationCount);
        Assert.Equal(wu.WorkUnitId, fakeExecutor.LastRequest!.WorkUnitId);
        Assert.Equal(task.TaskId, fakeExecutor.LastRequest.TaskId);
        Assert.Equal("fake-harness", fakeExecutor.LastRequest.Profile?.Executor);
        Assert.Equal(StudioTaskStatus.Completed, updatedTask!.Status);

        // Scheduler bookkeeping: VerifyWorkerProgressAsync must have seen the completed task and
        // treated the run as a genuine success — ReleaseAsync(success: true) removes the item from
        // the pending queue entirely (not parked, not re-queued), and no dead-letter entry exists.
        // The task-Completed signal polled above lands BEFORE the release runs, so poll for the
        // release too rather than asserting it already happened.
        IReadOnlyList<ScheduledItem> pending = [];
        var releaseDeadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < releaseDeadline)
        {
            pending = await scheduler.ListPendingAsync();
            if (!pending.Any(i => i.WorkUnitId == wu.WorkUnitId)) break;
            await Task.Delay(25);
        }
        Assert.DoesNotContain(pending, i => i.WorkUnitId == wu.WorkUnitId);
        var deadLetterEntry = await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId);
        Assert.Null(deadLetterEntry);
    }
}
