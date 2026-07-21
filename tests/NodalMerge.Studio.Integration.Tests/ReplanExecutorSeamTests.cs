using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/phase-d-implementation.md D3 — ReplanService's planner spawn now goes through the same
/// executor seam D1 put behind the scheduler-driven Plan-stage branch: an explicit Plan-stage
/// Agent Topology assignment naming the claude-cli provider routes the re-plan attempt to a real
/// stub CLI (mirrors ClaudeCodeExecutorPlanModeTests' fixture shape) instead of the native
/// PlannerAgentLoop, and the resulting plan.json still folds through the unmodified
/// FanOutService path. Un-couples "a plan exists" from "the native orchestrator produced it,"
/// same as D1 did for the scheduler-driven Plan-stage branch.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class ReplanExecutorSeamTests : IAsyncLifetime
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"replan-seam-stub-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_stubDir);

    private string WriteStub(string name, string script)
    {
        Directory.CreateDirectory(_stubDir);
        var path = Path.Combine(_stubDir, name);
        File.WriteAllText(path, script);
        return path;
    }

    private const string ReplanPlanJson =
        """{"slices":[{"sliceId":"s-new-1","goal":"Fix A","fileScope":["src/A.cs"],"dependsOn":[],"steps":["step one"]},{"sliceId":"s-new-2","goal":"Fix B","fileScope":["src/B.cs"],"dependsOn":[],"steps":["step two"]}]}""";

    private static string SuccessJsonl(string sessionId = "stub-session-replan") => string.Join('\n',
        $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}"}""",
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1," +
        "\"result\":\"Wrote the plan.\",\"stop_reason\":\"end_turn\",\"session_id\":\"" + sessionId + "\"," +
        "\"total_cost_usd\":0.01,\"usage\":{\"input_tokens\":30,\"output_tokens\":8}}");

    [Fact]
    public async Task ReplanFailedSliceAsync_routes_through_a_CLI_executor_and_folds_the_resulting_plan()
    {
        var jsonlPath = Path.Combine(_stubDir, "output.jsonl");
        var planPath = Path.Combine(_stubDir, "plan.json");
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(jsonlPath, SuccessJsonl());
        File.WriteAllText(planPath, ReplanPlanJson);
        var stub = WriteStub("stub-claude.cmd",
            "@echo off\r\n" +
            "if not exist .workspace mkdir .workspace\r\n" +
            "copy /Y \"%~dp0plan.json\" \".workspace\\plan.json\" >nul\r\n" +
            "type \"%~dp0output.jsonl\"\r\n");

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ReplanFailedSliceLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new ClaudeCodeExecutorOptions { ExecutablePath = stub, TimeoutSeconds = 30 });
            });

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var deadLetter = app.Services.GetRequiredService<IDeadLetterService>();
        var replan = app.Services.GetRequiredService<IReplanService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var parent = await orchestratorSvc.CreateWorkUnitAsync(goal: "Parent goal", owner: "integration-test");
        await fileWorkspace.InitBranchAsync(parent.BranchId);

        // Explicit Plan-stage Agent Topology assignment naming the claude-cli provider — the
        // override that wins outright regardless of WorkspaceOptions.UsePlannerExecutorSelection
        // (off here, the default), same precedence D2 established.
        var stageCredentials = new Dictionary<PipelineStage, GoalDefaultCredentials>
        {
            [PipelineStage.Plan] = new GoalDefaultCredentials("claude-cli", "", "", "", null),
        };

        await agentControl.SpawnAsync(
            agentType: "orchestrator",
            workUnitId: parent.WorkUnitId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key",
            stageCredentials: stageCredentials);

        var failedChild = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Original slice goal that will fail",
            owner: "integration-test",
            parentWorkUnitId: parent.WorkUnitId,
            seedFromBranchId: parent.BranchId,
            sliceId: "s-old");

        var entry = await deadLetter.RecordFailureAsync(
            failedChild.WorkUnitId,
            "worker-x",
            PipelineStage.Execute,
            "worker",
            "Simulated failure for this test",
            kind: FailureKind.Exception);

        var result = await replan.ReplanFailedSliceAsync(entry.EntryId);

        Assert.Equal(ReplanOutcome.Replanned, result.Outcome);
        Assert.NotNull(result.NewWorkUnitIds);
        Assert.Equal(2, result.NewWorkUnitIds!.Count);

        var reloadedFailedChild = await workUnits.GetAsync(failedChild.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Cancelled, reloadedFailedChild!.Status);

        var siblings = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        Assert.Equal(3, siblings.Count); // original (now Cancelled) + 2 new sub-slices from the CLI plan
        Assert.Contains(siblings, s => s.FanOutInfo?.SliceId == "s-new-1");
        Assert.Contains(siblings, s => s.FanOutInfo?.SliceId == "s-new-2");
    }
}
