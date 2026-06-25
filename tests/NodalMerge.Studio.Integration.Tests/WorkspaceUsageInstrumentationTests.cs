using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 14 — drives a real WorkerAgentLoop through the actual singleton dispatcher (no real LLM,
/// via WorkspaceUsageInstrumentationLlmHandler) to prove nm_v1_workspace_search/nm_v1_workspace_read_many
/// calls actually get recorded as WorkspaceSearchExecuted/WorkspaceReadExecuted events, and that
/// WorkspaceUsageMetricsService aggregates them correctly — the evidence future Phase-12 leasing
/// decisions are meant to hinge on.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceUsageInstrumentationTests
{
    [Fact]
    public async Task SearchThenReadMany_RecordsEvents_AndAggregatesIntoUsageMetrics()
    {
        const string query = "TargetSymbol";
        var hitPaths = new[] { "src/A.cs", "src/B.cs" };
        var fakeHandler = new WorkspaceUsageInstrumentationLlmHandler { Query = query, HitPaths = hitPaths };

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler        = app.Services.GetRequiredService<IWorkScheduler>();
        var tasks             = app.Services.GetRequiredService<ITaskService>();
        var fileWorkspace     = app.Services.GetRequiredService<IFileWorkspaceService>();
        var events            = app.Services.GetRequiredService<IExecutionEventStream>();
        var usageMetrics      = app.Services.GetRequiredService<IWorkspaceUsageMetricsService>();
        var agentRuntime      = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();

        const string sessionId = "SES-usage-1";

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var wu = await orchestratorSvc.CreateWorkUnitAsync("Find TargetSymbol", "test");
            var task = await tasks.CreateAsync(new StudioTask(
                Guid.NewGuid().ToString("N"), wu.WorkUnitId, wu.Goal, "Execute", StudioTaskStatus.Open, null, 0));

            await fileWorkspace.WriteAsync(wu.BranchId, hitPaths[0], "// TargetSymbol foo");
            await fileWorkspace.WriteAsync(wu.BranchId, hitPaths[1], "// TargetSymbol bar");

            await scheduler.EnqueueAsync(
                wu.WorkUnitId, "worker", task.TaskId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key", provider: "anthropic",
                sessionId: sessionId);

            // Poll for the read event — the last scripted step before end_turn — as the signal the
            // run actually completed both tool calls.
            IReadOnlyList<ExecutionEvent> readEvents = [];
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                readEvents = await events.GetEventsByKindAsync([ExecutionEventKind.WorkspaceReadExecuted]);
                if (readEvents.Count > 0) break;
                await Task.Delay(50);
            }

            Assert.Single(readEvents);
            Assert.Equal(sessionId, readEvents[0].SessionId);

            var searchEvents = await events.GetEventsByKindAsync([ExecutionEventKind.WorkspaceSearchExecuted]);
            Assert.Single(searchEvents);

            var topHits = await usageMetrics.GetTopFileHitsAsync();
            Assert.Contains(topHits, h => h.Path == hitPaths[0]);
            Assert.Contains(topHits, h => h.Path == hitPaths[1]);

            var searchUsage = await usageMetrics.GetSearchUsageAsync(wu.WorkUnitId);
            Assert.Equal(1, searchUsage.SearchCount);
            Assert.Equal(2, searchUsage.TotalMatches);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
