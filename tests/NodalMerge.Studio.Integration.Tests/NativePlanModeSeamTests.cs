using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/phase-d-implementation.md D1.a — native regression: a scheduler-driven Plan-stage item
/// (IWorkScheduler.EnqueueAsync + PollSchedulerAsync's background hosted-service loop, same
/// queue-driven site HarnessExecutorSeamIntegrationTests exercises for Worker/Execute) now
/// resolves an executor and runs PlannerAgentLoop through NativeHarnessExecutor's Mode==Plan
/// branch instead of InMemoryAgentRuntimeService constructing it directly. Proves the move behind
/// the seam preserved both outcomes: a normal plan success (Plan artifact recorded, item
/// released, no dead-letter) and the needsExecuteFallback handoff (a Succeeded plan run with no
/// recorded Plan artifact hands the same work unit straight to Execute).
/// </summary>
[Trait("Category", "Integration")]
public class NativePlanModeSeamTests
{
    [Fact]
    public async Task Scheduler_driven_planner_success_records_a_Plan_artifact_via_the_seam_and_releases_clean()
    {
        await using var app = StudioWebApplication.Build(
            [],
            // StartAsync below is only needed for the hosted-service scheduler loop — without
            // UseTestServer it would also bind real Kestrel on the default port 5000, which
            // collides (AddressInUseException) with any parallel test class doing the same.
            configureWebHost: webHost => webHost.UseTestServer(),
            llmHttpClient: new HttpClient(new FanOutLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { SchedulerPollIntervalMs = 50 });
            });
        await app.StartAsync();

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var artifacts = app.Services.GetRequiredService<IArtifactLineageService>();
        var deadLetter = app.Services.GetRequiredService<IDeadLetterService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the native Plan seam", "integration-test");

        await scheduler.EnqueueAsync(
            wu.WorkUnitId, "planner", taskId: null,
            model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key");

        ArtifactRef? planArtifact = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var chain = await artifacts.GetChainAsync(wu.WorkUnitId);
            planArtifact = chain.FirstOrDefault(a => a.Type == ArtifactType.Plan);
            if (planArtifact is not null) break;
            await Task.Delay(25);
        }

        Assert.NotNull(planArtifact);
        Assert.Contains("s1", planArtifact!.Body);

        // The Plan artifact (polled above) is recorded BEFORE RunScheduledWorkerAsync's
        // ReleaseAsync(success: true) removes the queue item, so the item can legitimately still
        // be visible (leased) for a moment — poll for the release instead of asserting it
        // already happened the instant the artifact appeared.
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

    [Fact]
    public async Task Scheduler_driven_planner_with_no_recorded_plan_hands_off_to_Execute()
    {
        // ImmediateEndTurnLlmHandler ends every turn immediately without ever calling
        // nm_v1_artifact_record_plan — a Succeeded plan run with no recorded Plan artifact, the
        // needsExecuteFallback case InMemoryAgentRuntimeService's caller-side check handles.
        await using var app = StudioWebApplication.Build(
            [],
            // Same UseTestServer rationale as the first test — avoid binding real Kestrel :5000.
            configureWebHost: webHost => webHost.UseTestServer(),
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { SchedulerPollIntervalMs = 50 });
            });
        await app.StartAsync();

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var deadLetter = app.Services.GetRequiredService<IDeadLetterService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the needsExecuteFallback handoff", "integration-test");

        await scheduler.EnqueueAsync(
            wu.WorkUnitId, "planner", taskId: null,
            model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key");

        // The planner run succeeds with no plan recorded -> enqueued directly to "worker" -> the
        // worker run also ends its turn immediately (same handler) with no real progress ->
        // VerifyWorkerProgressAsync fails it -> dead-lettered. Poll for that terminal state rather
        // than the intermediate "worker" queue entry, which may already have been picked up and
        // released by the time this polls.
        DeadLetterEntry? deadLetterEntry = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            deadLetterEntry = await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId);
            if (deadLetterEntry is not null) break;
            await Task.Delay(25);
        }

        Assert.NotNull(deadLetterEntry);
        Assert.Equal("worker", deadLetterEntry!.ProfileId);
    }
}
