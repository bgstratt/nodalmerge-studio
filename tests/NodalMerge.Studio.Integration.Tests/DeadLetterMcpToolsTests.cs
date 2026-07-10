using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 2 item 2 follow-up — DeadLetterTools (nm_v1_dead_letter_*) mirror the REST dead-letter
/// endpoints one-for-one so an external MCP client has the same recovery actions the VS Code
/// dashboard's dead-letter card already exposes. These are thin-adapter tests: the underlying
/// service behavior (retry/replan/continue semantics) is already covered by
/// DeadLetterIntegrationTests/ReplanServiceIntegrationTests/ContinueIntegrationTests — this file
/// only proves the MCP tool layer correctly bridges to those services and JSON-encodes the result
/// (McpJson.Ok wraps as {contractVersion, data}, McpJson.Error as {contractVersion, tool, status,
/// message} — no naming-policy override, so nested domain records keep their PascalCase C#
/// property names), including redacting ApiKey the same way the REST layer does.
/// </summary>
[Trait("Category", "Integration")]
public class DeadLetterMcpToolsTests
{
    private static async Task<(InMemoryAgentRuntimeService AgentRuntime, string WorkUnitId, string EntryId, Microsoft.AspNetCore.Builder.WebApplication App)>
        BuildWithDeadLetteredWorkUnitAsync()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ExhaustingLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentRuntime = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var deadLetter = app.Services.GetRequiredService<IDeadLetterService>();
        var profiles = app.Services.GetRequiredService<IAgentProfileService>();

        await profiles.CreateAsync(new AgentProfile(
            "exhaust-worker-mcp",
            "Exhaust Worker (MCP test)",
            PipelineStage.Execute,
            string.Empty,
            [McpToolNames.WorkUnitGet],
            MaxIterations: 2,
            FileScopePatterns: []));

        await agentRuntime.StartAsync(CancellationToken.None);

        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Task that will exhaust iterations (MCP tools test)",
            owner: "integration-test");

        await scheduler.EnqueueAsync(
            wu.WorkUnitId,
            "exhaust-worker-mcp",
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key-0123456789");

        DeadLetterEntry? entry = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            entry = await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId);
            if (entry is not null) break;
            await Task.Delay(100);
        }
        Assert.NotNull(entry);

        return (agentRuntime, wu.WorkUnitId, entry!.EntryId, app);
    }

    [Fact]
    public async Task ListAsync_never_includes_the_api_key()
    {
        var (agentRuntime, _, entryId, app) = await BuildWithDeadLetteredWorkUnitAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<DeadLetterTools>(app.Services);

            var json = await tools.ListAsync();
            using var doc = JsonDocument.Parse(json);
            var entries = doc.RootElement.GetProperty("data").EnumerateArray().ToList();

            Assert.Contains(entries, e => e.GetProperty("EntryId").GetString() == entryId);
            var match = entries.First(e => e.GetProperty("EntryId").GetString() == entryId);

            // DeadLetterEntry.ApiKey is [JsonIgnore]d — never persisted, never serialized out over
            // any transport. No redaction step is needed (or possible) because the property is
            // simply absent from the JSON entirely.
            Assert.False(match.TryGetProperty("ApiKey", out _));
            Assert.DoesNotContain("fake-key-0123456789", json);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task GetAsync_returns_the_entry_by_id_and_errors_for_an_unknown_id()
    {
        var (agentRuntime, _, entryId, app) = await BuildWithDeadLetteredWorkUnitAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<DeadLetterTools>(app.Services);

            var found = await tools.GetAsync(entryId);
            using var foundDoc = JsonDocument.Parse(found);
            Assert.Equal(entryId, foundDoc.RootElement.GetProperty("data").GetProperty("EntryId").GetString());

            var missing = await tools.GetAsync("no-such-entry");
            using var missingDoc = JsonDocument.Parse(missing);
            Assert.Equal("error", missingDoc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ByWorkUnitAsync_and_HistoryAsync_resolve_the_same_work_unit()
    {
        var (agentRuntime, workUnitId, entryId, app) = await BuildWithDeadLetteredWorkUnitAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<DeadLetterTools>(app.Services);

            var byWorkUnit = await tools.ByWorkUnitAsync(workUnitId);
            using var byWorkUnitDoc = JsonDocument.Parse(byWorkUnit);
            Assert.Equal(entryId, byWorkUnitDoc.RootElement.GetProperty("data").GetProperty("EntryId").GetString());

            var history = await tools.HistoryAsync(workUnitId);
            using var historyDoc = JsonDocument.Parse(history);
            Assert.Contains(
                historyDoc.RootElement.GetProperty("data").EnumerateArray(),
                e => e.GetProperty("EntryId").GetString() == entryId);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RetryAsync_retries_the_dead_lettered_work_unit()
    {
        var (agentRuntime, _, entryId, app) = await BuildWithDeadLetteredWorkUnitAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<DeadLetterTools>(app.Services);

            var json = await tools.RetryAsync(entryId);
            using var doc = JsonDocument.Parse(json);

            var outcome = (DeadLetterRetryOutcome)doc.RootElement.GetProperty("data").GetProperty("Outcome").GetInt32();
            Assert.Equal(DeadLetterRetryOutcome.Retried, outcome);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RetryWithContextAsync_requires_a_non_empty_steering_context()
    {
        var (agentRuntime, _, entryId, app) = await BuildWithDeadLetteredWorkUnitAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<DeadLetterTools>(app.Services);

            var json = await tools.RetryWithContextAsync(entryId, steeringContext: "   ");
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ContinueAsync_errors_for_a_non_MaxIterationsExceeded_entry()
    {
        // Reuses the retry-path dead-letter fixtures indirectly isn't necessary here — build a
        // fresh app and record a non-MaxIterationsExceeded entry directly via IDeadLetterService,
        // proving DeadLetterTools surfaces IContinueService's own NotApplicable guard correctly.
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var deadLetter = app.Services.GetRequiredService<IDeadLetterService>();
        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync(goal: "Some goal", owner: "integration-test");
        var entry = await deadLetter.RecordFailureAsync(
            wu.WorkUnitId, "agent-1", PipelineStage.Execute, "worker", "Simulated exception",
            kind: FailureKind.Exception);

        var tools = ActivatorUtilities.CreateInstance<DeadLetterTools>(app.Services);
        var json = await tools.ContinueAsync(entry.EntryId);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
    }
}
