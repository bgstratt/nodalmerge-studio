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
/// Phase 2 item 2 follow-up — the recommended external-caller surface (nms_v1_*, distinct from
/// the detailed internal nm_v1_* agent surface — see McpServerToolNames.cs's own doc comment)
/// previously had zero failure-recovery visibility: a caller following the documented flow
/// (nms_v1_goal_run -> poll nms_v1_goal_status) had no way to discover a goal was dead-lettered,
/// let alone recover it, without dropping down to nm_v1_dead_letter_* or REST. nms_v1_goal_status
/// now surfaces an unresolved failure (with which actions apply), and nms_v1_goal_recover resolves
/// the goal's own latest dead-letter entry internally — no entryId/work-unit knowledge required.
/// </summary>
[Trait("Category", "Integration")]
public class ExternalGoalToolsRecoveryTests
{
    private static async Task<(InMemoryAgentRuntimeService AgentRuntime, string GoalId, Microsoft.AspNetCore.Builder.WebApplication App)>
        BuildWithDeadLetteredGoalAsync()
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
        var goalNodes = app.Services.GetRequiredService<IGoalNodeService>();

        await profiles.CreateAsync(new AgentProfile(
            "exhaust-worker-goal-recover",
            "Exhaust Worker (goal recover test)",
            PipelineStage.Execute,
            string.Empty,
            [McpToolNames.WorkUnitGet],
            MaxIterations: 2,
            FileScopePatterns: []));

        await agentRuntime.StartAsync(CancellationToken.None);

        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Task that will exhaust iterations (goal recover test)",
            owner: "integration-test");

        // ExternalGoalTools resolves everything by goalId via IGoalNodeService — for a top-level
        // goal, goalId == workUnitId (see ExternalGoalTools.RunAsync's own convention).
        await goalNodes.RecordAsync(new GoalNode(
            GoalId: wu.WorkUnitId,
            Goal: wu.Goal,
            WorkUnitId: wu.WorkUnitId,
            BranchId: wu.BranchId,
            Status: GoalStatus.Exploring,
            CreatedAt: wu.CreatedAt,
            UpdatedAt: wu.UpdatedAt,
            Owner: "integration-test",
            ParentGoalId: null));

        await scheduler.EnqueueAsync(
            wu.WorkUnitId,
            "exhaust-worker-goal-recover",
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId) is not null) break;
            await Task.Delay(100);
        }
        Assert.NotNull(await deadLetter.GetLatestForWorkUnitAsync(wu.WorkUnitId));

        return (agentRuntime, wu.WorkUnitId, app);
    }

    [Fact]
    public async Task StatusAsync_surfaces_the_dead_letter_and_its_recoverable_actions()
    {
        var (agentRuntime, goalId, app) = await BuildWithDeadLetteredGoalAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<ExternalGoalTools>(app.Services);

            var json = await tools.StatusAsync(goalId);
            using var doc = JsonDocument.Parse(json);
            var deadLetterInfo = doc.RootElement.GetProperty("data").GetProperty("deadLetter");

            Assert.Equal(nameof(FailureKind.MaxIterationsExceeded), deadLetterInfo.GetProperty("kind").GetString());
            var actions = deadLetterInfo.GetProperty("recoverableActions").EnumerateArray().Select(e => e.GetString()).ToList();
            Assert.Contains("retry", actions);
            Assert.Contains("continue", actions);
            Assert.Contains("replan", actions);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StatusAsync_reports_null_dead_letter_for_a_healthy_goal()
    {
        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var goalNodes = app.Services.GetRequiredService<IGoalNodeService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync(goal: "Healthy goal", owner: "integration-test");
        await goalNodes.RecordAsync(new GoalNode(
            wu.WorkUnitId, wu.Goal, wu.WorkUnitId, wu.BranchId, GoalStatus.Exploring,
            wu.CreatedAt, wu.UpdatedAt, "integration-test", null));

        var tools = ActivatorUtilities.CreateInstance<ExternalGoalTools>(app.Services);
        var json = await tools.StatusAsync(wu.WorkUnitId);
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("data").GetProperty("deadLetter").ValueKind);
    }

    [Fact]
    public async Task RecoverAsync_retry_resolves_the_goals_own_dead_letter_entry_without_an_entryId()
    {
        var (agentRuntime, goalId, app) = await BuildWithDeadLetteredGoalAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<ExternalGoalTools>(app.Services);

            var json = await tools.RecoverAsync(goalId, action: "retry");
            using var doc = JsonDocument.Parse(json);

            Assert.Equal(nameof(DeadLetterRetryOutcome.Retried), doc.RootElement.GetProperty("data").GetProperty("outcome").GetString());
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RecoverAsync_retry_with_context_requires_steering_context()
    {
        var (agentRuntime, goalId, app) = await BuildWithDeadLetteredGoalAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<ExternalGoalTools>(app.Services);

            var json = await tools.RecoverAsync(goalId, action: "retry_with_context", steeringContext: null);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RecoverAsync_rejects_an_unknown_action()
    {
        var (agentRuntime, goalId, app) = await BuildWithDeadLetteredGoalAsync();
        try
        {
            var tools = ActivatorUtilities.CreateInstance<ExternalGoalTools>(app.Services);

            var json = await tools.RecoverAsync(goalId, action: "nonsense");
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task RecoverAsync_errors_when_the_goal_has_no_dead_letter_entry()
    {
        await using var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var goalNodes = app.Services.GetRequiredService<IGoalNodeService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync(goal: "Healthy goal", owner: "integration-test");
        await goalNodes.RecordAsync(new GoalNode(
            wu.WorkUnitId, wu.Goal, wu.WorkUnitId, wu.BranchId, GoalStatus.Exploring,
            wu.CreatedAt, wu.UpdatedAt, "integration-test", null));

        var tools = ActivatorUtilities.CreateInstance<ExternalGoalTools>(app.Services);
        var json = await tools.RecoverAsync(wu.WorkUnitId, action: "retry");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal("error", doc.RootElement.GetProperty("status").GetString());
    }
}
