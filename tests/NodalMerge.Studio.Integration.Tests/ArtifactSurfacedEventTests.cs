using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 23 — drives a real WorkerAgentLoop (under the "merger" profile, the lowest-friction
/// default profile whose AllowedTools include nm_v1_projection_get) through the actual singleton
/// dispatcher (no real LLM, via ProjectionGetSurfacedArtifactLlmHandler), to prove a domain-agent-
/// authored artifact (recognizable by its DomainAgentRegistry title prefix) present in the
/// AgentWorkspace projection response gets recorded as an ArtifactSurfaced event — the durable
/// "was this actually returned to an agent" signal the feedback loop is built on.
/// </summary>
[Trait("Category", "Integration")]
public class ArtifactSurfacedEventTests
{
    [Fact]
    public async Task ProjectionGet_RecordsArtifactSurfaced_ForDomainAgentAuthoredArtifact()
    {
        var fakeHandler = new ProjectionGetSurfacedArtifactLlmHandler();

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler        = app.Services.GetRequiredService<IWorkScheduler>();
        var tasks             = app.Services.GetRequiredService<ITaskService>();
        var artifactCommands  = app.Services.GetRequiredService<IArtifactCommandService>();
        var events            = app.Services.GetRequiredService<IExecutionEventStream>();
        var agentRuntime      = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();

        const string sessionId = "SES-surfaced-1";

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var wu = await orchestratorSvc.CreateWorkUnitAsync("Investigate token handling", "test");

            var recorded = await artifactCommands.RecordAsync(
                wu.WorkUnitId, "Constraint",
                DomainAgentRegistry.Security.TitlePrefix + "Missing threat model for token rotation",
                "Token rotation isn't covered by any recorded Decision.");

            var task = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), wu.WorkUnitId, wu.Goal, "Execute", StudioTaskStatus.Open, null, 0));

            await scheduler.EnqueueAsync(
                wu.WorkUnitId, "merger", task.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic",
                sessionId: sessionId);

            IReadOnlyList<ExecutionEvent> surfacedEvents = [];
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                surfacedEvents = await events.GetEventsByKindAsync([ExecutionEventKind.ArtifactSurfaced]);
                if (surfacedEvents.Count > 0) break;
                await Task.Delay(50);
            }

            Assert.Single(surfacedEvents);
            Assert.Equal(sessionId, surfacedEvents[0].SessionId);

            var payload = System.Text.Json.JsonSerializer.Deserialize<ArtifactSurfacedPayload>(surfacedEvents[0].PayloadJson)!;
            Assert.Equal(recorded.ArtifactId, payload.ArtifactId);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ProjectionGet_RecordsNoArtifactSurfaced_WhenNoDomainAgentArtifactsPresent()
    {
        var fakeHandler = new ProjectionGetSurfacedArtifactLlmHandler();

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler        = app.Services.GetRequiredService<IWorkScheduler>();
        var tasks             = app.Services.GetRequiredService<ITaskService>();
        var events            = app.Services.GetRequiredService<IExecutionEventStream>();
        var agentRuntime      = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();

        const string sessionId = "SES-surfaced-2";

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var wu = await orchestratorSvc.CreateWorkUnitAsync("A goal with no constraints", "test");

            var task = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), wu.WorkUnitId, wu.Goal, "Execute", StudioTaskStatus.Open, null, 0));

            await scheduler.EnqueueAsync(
                wu.WorkUnitId, "merger", task.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic",
                sessionId: sessionId);

            // No deterministic "done" signal to poll for here (the very thing we're proving is
            // absent) — wait a bounded amount of wall-clock time for the scripted turn to run.
            await Task.Delay(1000);

            var surfacedEvents = await events.GetEventsByKindAsync([ExecutionEventKind.ArtifactSurfaced]);
            Assert.Empty(surfacedEvents);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
