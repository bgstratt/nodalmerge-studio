using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 1.4 (plans/orchestrator-reliability-and-observability.md) Continue-track — resumes a
/// dead-lettered work unit with its own prior conversation reconstructed from
/// ConversationLogEntry, rather than starting the task over. See ContinueLlmHandler for how the
/// end-to-end proof that reconstructed context (not just a fresh restart) drove the resumed run.
/// </summary>
[Trait("Category", "Integration")]
public class ContinueIntegrationTests
{
    [Fact]
    public async Task ContinueWithPriorContextAsync_resumes_using_reconstructed_history_and_completes()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ContinueLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var scheduler       = app.Services.GetRequiredService<IWorkScheduler>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();
        var profiles        = app.Services.GetRequiredService<IAgentProfileService>();
        var continueService = app.Services.GetRequiredService<IContinueService>();

        await profiles.CreateAsync(new AgentProfile(
            "exhaust-worker",
            "Exhaust Worker",
            PipelineStage.Execute,
            string.Empty,
            [McpToolNames.WorkUnitGet],
            MaxIterations: 2,
            FileScopePatterns: []));

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var wu = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Task that will exhaust iterations before continuing",
                owner: "integration-test");

            await scheduler.EnqueueAsync(
                wu.WorkUnitId,
                "exhaust-worker",
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

            DeadLetterEntry? entry = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
            while (DateTimeOffset.UtcNow < deadline)
            {
                entry = await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId);
                if (entry is not null) break;
                await Task.Delay(100);
            }

            Assert.NotNull(entry);
            Assert.Equal(FailureKind.MaxIterationsExceeded, entry!.Kind);

            var statusDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < statusDeadline)
            {
                var unit = await workUnits.GetAsync(wu.WorkUnitId);
                if (unit?.Status == WorkUnitStatus.DeadLettered) break;
                await Task.Delay(100);
            }

            var result = await continueService.ContinueWithPriorContextAsync(entry.EntryId);

            Assert.Equal(ContinueOutcome.Continued, result.Outcome);
            Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);

            var reloaded = await workUnits.GetAsync(wu.WorkUnitId);
            Assert.NotNull(reloaded);
            Assert.Equal(WorkUnitStatus.Executing, reloaded!.Status);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
