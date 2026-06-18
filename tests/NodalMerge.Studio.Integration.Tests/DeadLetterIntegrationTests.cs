using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 4 slice 11e — dead-letter capture and human retry from dashboard API.
/// </summary>
[Trait("Category", "Integration")]
public class DeadLetterIntegrationTests
{
    [Fact]
    public async Task Max_iterations_writes_dead_letter_and_DeadLettered_status()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ExhaustingLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var scheduler       = app.Services.GetRequiredService<IWorkScheduler>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();
        var profiles        = app.Services.GetRequiredService<IAgentProfileService>();

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
                goal: "Task that will exhaust iterations",
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
            Assert.Equal("Max iterations reached", entry.Reason);
            Assert.Equal(1, entry.AttemptCount);

            var unit = await workUnits.GetAsync(wu.WorkUnitId);
            Assert.NotNull(unit);
            Assert.Equal(WorkUnitStatus.DeadLettered, unit!.Status);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Retry_from_dead_letter_re_enqueues_work_unit()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ExhaustingLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var scheduler       = app.Services.GetRequiredService<IWorkScheduler>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();
        var profiles        = app.Services.GetRequiredService<IAgentProfileService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();

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
                goal: "Retryable failure",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                "orchestrator",
                wu.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

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

            var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
            var statusDeadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < statusDeadline)
            {
                var unit = await workUnits.GetAsync(wu.WorkUnitId);
                if (unit?.Status == WorkUnitStatus.DeadLettered) break;
                await Task.Delay(100);
            }

            var retry = await deadLetter.RetryAsync(entry!.EntryId);
            Assert.Equal(DeadLetterRetryOutcome.Retried, retry.Outcome);

            var pending = await scheduler.ListPendingAsync();
            Assert.Contains(pending, p => p.WorkUnitId == wu.WorkUnitId);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
