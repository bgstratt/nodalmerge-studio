using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class ClarificationWorkflowTests : IAsyncLifetime
{
    private readonly string _rootPath = Path.Combine(Path.GetTempPath(), $"studio-clarify-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_rootPath);

    private WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new WorkspaceOptions { RootPath = _rootPath });
            });

    [Fact]
    public async Task Clarification_request_parks_scheduler_item_and_response_resumes_it()
    {
        // Deliberately NOT started: this test plays the worker itself (TryAcquireAsync below) and
        // asserts the resumed item is back at Queued — StartAsync would run the real scheduler
        // poll loop, which legitimately acquires the resumed item and moves it to Executing
        // before the assertion reads it. Every service used here resolves without the host running.
        await using var app = BuildTestApp();

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var clarifications = app.Services.GetRequiredService<IClarificationCommandService>();
        var events = app.Services.GetRequiredService<IExecutionEventStream>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var workUnit = await orchestrator.CreateWorkUnitAsync("Implement feature", "tester");

        await scheduler.EnqueueAsync(
            workUnit.WorkUnitId,
            profileId: "worker",
            sessionId: "sess-clarify");

        var leased = await scheduler.TryAcquireAsync("worker-agent");
        Assert.NotNull(leased);
        Assert.Equal(workUnit.WorkUnitId, leased!.WorkUnitId);

        var request = await clarifications.RequestAsync(
            workUnit.WorkUnitId,
            "Should validation be enforced at API or DB layer?",
            context: "Schema currently lacks unique index.",
            options: ["API only", "DB only", "Both"],
            requestedByAgentId: "worker-agent",
            sessionId: "sess-clarify");

        Assert.True(request.ParkedAwaitingResponse);

        var awaiting = await scheduler.ListAwaitingResumeAsync();
        Assert.Contains(awaiting, i => i.WorkUnitId == workUnit.WorkUnitId && i.AwaitingResume);

        var waitingUnit = await workUnits.GetAsync(workUnit.WorkUnitId);
        Assert.NotNull(waitingUnit);
        Assert.Equal(WorkUnitStatus.Waiting, waitingUnit!.Status);

        var response = await clarifications.RespondAsync(
            workUnit.WorkUnitId,
            response: "Both",
            note: "Apply API guard + unique DB index.",
            respondedBy: "human-reviewer",
            resume: true,
            sessionId: "sess-clarify");

        Assert.True(response.Resumed);

        var awaitingAfter = await scheduler.ListAwaitingResumeAsync();
        Assert.DoesNotContain(awaitingAfter, i => i.WorkUnitId == workUnit.WorkUnitId && i.AwaitingResume);

        var queuedUnit = await workUnits.GetAsync(workUnit.WorkUnitId);
        Assert.NotNull(queuedUnit);
        Assert.Equal(WorkUnitStatus.Queued, queuedUnit!.Status);

        var sessionEvents = await events.GetSessionEventsAsync("sess-clarify");
        var requestedEvent = sessionEvents.LastOrDefault(e => e.Kind == ExecutionEventKind.ClarificationRequested);
        var respondedEvent = sessionEvents.LastOrDefault(e => e.Kind == ExecutionEventKind.ClarificationResponded);
        Assert.NotNull(requestedEvent);
        Assert.NotNull(respondedEvent);

        var requestedPayload = JsonSerializer.Deserialize<ClarificationRequestedPayload>(requestedEvent!.PayloadJson);
        var respondedPayload = JsonSerializer.Deserialize<ClarificationRespondedPayload>(respondedEvent!.PayloadJson);
        Assert.NotNull(requestedPayload);
        Assert.NotNull(respondedPayload);
        Assert.Equal("Should validation be enforced at API or DB layer?", requestedPayload!.Question);
        Assert.Equal("Both", respondedPayload!.Response);

        // plans/harness-hosting-architecture.md Phase B3 — the outbox half of the pause/resume
        // loop: RespondAsync(resume: true) writes the answer where a respawned ClaudeCodeExecutor
        // (--resume) is told to look. Harmless for this native-worker scenario; just confirms the
        // file lands.
        var outboxFiles = await fileWorkspace.ListIncludingDotfilesAsync(workUnit.BranchId, ".workspace/outbox");
        var outboxFile = Assert.Single(outboxFiles);
        var outboxContent = await fileWorkspace.ReadAsync(workUnit.BranchId, outboxFile);
        Assert.Equal("Both", outboxContent);
    }
}
