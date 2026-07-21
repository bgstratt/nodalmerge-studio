using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/phase-d-implementation.md D1 — ClaudeCodeExecutor's Mode==Plan path against a stub CLI:
/// valid plan.json folds into WorkUnits via the existing FanOutService, invalid plan.json fails
/// cleanly with no artifact/children, and a plan run that also edits a source file has its diff
/// discarded (with a warning event) while the plan still folds. Mirrors
/// ClaudeCodeExecutorHarvestTests' shape for the Execute path.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class ClaudeCodeExecutorPlanModeTests : IAsyncLifetime
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"claude-plan-stub-{Guid.NewGuid():N}");

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

    private const string ValidPlanJson =
        """{"slices":[{"sliceId":"s1","goal":"Do the first slice","fileScope":["src/A.cs"],"dependsOn":[],"steps":["step one"]}]}""";

    private const string InvalidPlanJson = """{"not":"a plan"}""";

    private static string SuccessJsonl(string sessionId = "stub-session-plan") => string.Join('\n',
        $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}"}""",
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1," +
        "\"result\":\"Wrote the plan.\",\"stop_reason\":\"end_turn\",\"session_id\":\"" + sessionId + "\"," +
        "\"total_cost_usd\":0.01,\"usage\":{\"input_tokens\":30,\"output_tokens\":8}}");

    private IServiceProvider BuildApp(string executablePath)
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new ClaudeCodeExecutorOptions { ExecutablePath = executablePath, TimeoutSeconds = 30 });
            });
        return app.Services;
    }

    [Fact]
    public async Task Valid_plan_json_records_Plan_artifact_and_fans_out_children()
    {
        var jsonlPath = Path.Combine(_stubDir, "output.jsonl");
        var planPath = Path.Combine(_stubDir, "plan.json");
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(jsonlPath, SuccessJsonl());
        File.WriteAllText(planPath, ValidPlanJson);
        var stub = WriteStub("stub-claude.cmd",
            "@echo off\r\n" +
            "if not exist .workspace mkdir .workspace\r\n" +
            "copy /Y \"%~dp0plan.json\" \".workspace\\plan.json\" >nul\r\n" +
            "type \"%~dp0output.jsonl\"\r\n");

        var services = BuildApp(stub);
        var orchestratorSvc = services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var artifacts = services.GetRequiredService<IArtifactLineageService>();
        var workUnits = services.GetRequiredService<IWorkUnitService>();
        var fanOut = services.GetRequiredService<IFanOutService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise D1 plan-mode fold", "integration-test");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        var executor = resolver.Resolve("claude-code");
        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Plan, "agent-claude-plan-1", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: "session-plan-1", IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);

        var chain = await artifacts.GetChainAsync(wu.WorkUnitId);
        var planArtifact = Assert.Single(chain, a => a.Type == ArtifactType.Plan);
        Assert.Contains("s1", planArtifact.Body);

        var fanOutResult = await fanOut.TryFanOutFromPlanAsync(wu.WorkUnitId);
        Assert.Contains(FanOutAction.ChildrenCreated, fanOutResult.Actions);

        var children = await workUnits.GetChildrenAsync(wu.WorkUnitId);
        var child = Assert.Single(children);
        Assert.Equal("s1", child.FanOutInfo?.SliceId);
        Assert.Equal(["src/A.cs"], child.FileScope);
        Assert.Empty(child.DependsOn);
        Assert.Contains(child.WorkUnitId, fanOutResult.EnqueuedWorkUnitIds);
    }

    [Fact]
    public async Task Invalid_plan_json_fails_cleanly_with_no_artifact_and_no_children()
    {
        var jsonlPath = Path.Combine(_stubDir, "output.jsonl");
        var planPath = Path.Combine(_stubDir, "plan.json");
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(jsonlPath, SuccessJsonl("stub-session-invalid"));
        File.WriteAllText(planPath, InvalidPlanJson);
        var stub = WriteStub("stub-claude.cmd",
            "@echo off\r\n" +
            "if not exist .workspace mkdir .workspace\r\n" +
            "copy /Y \"%~dp0plan.json\" \".workspace\\plan.json\" >nul\r\n" +
            "type \"%~dp0output.jsonl\"\r\n");

        var services = BuildApp(stub);
        var orchestratorSvc = services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var artifacts = services.GetRequiredService<IArtifactLineageService>();
        var workUnits = services.GetRequiredService<IWorkUnitService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise D1 invalid plan-mode", "integration-test");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        var executor = resolver.Resolve("claude-code");
        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Plan, "agent-claude-plan-invalid", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: "session-plan-invalid", IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Stalled, result.Completion);
        Assert.NotNull(result.FailureReason);

        var chain = await artifacts.GetChainAsync(wu.WorkUnitId);
        Assert.DoesNotContain(chain, a => a.Type == ArtifactType.Plan);

        var children = await workUnits.GetChildrenAsync(wu.WorkUnitId);
        Assert.Empty(children);
    }

    [Fact]
    public async Task Plan_run_that_also_edits_a_source_file_discards_the_diff_but_still_folds_the_plan()
    {
        var jsonlPath = Path.Combine(_stubDir, "output.jsonl");
        var planPath = Path.Combine(_stubDir, "plan.json");
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(jsonlPath, SuccessJsonl("stub-session-dirty"));
        File.WriteAllText(planPath, ValidPlanJson);
        var stub = WriteStub("stub-claude.cmd",
            "@echo off\r\n" +
            "if not exist .workspace mkdir .workspace\r\n" +
            "copy /Y \"%~dp0plan.json\" \".workspace\\plan.json\" >nul\r\n" +
            "echo unauthorized change > change.txt\r\n" +
            "type \"%~dp0output.jsonl\"\r\n");

        var services = BuildApp(stub);
        var orchestratorSvc = services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var artifacts = services.GetRequiredService<IArtifactLineageService>();
        var events = services.GetRequiredService<IExecutionEventStream>();
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise D1 discarded plan-mode diff", "integration-test");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        var executor = resolver.Resolve("claude-code");
        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Plan, "agent-claude-plan-dirty", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: "session-plan-dirty", IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);

        var chain = await artifacts.GetChainAsync(wu.WorkUnitId);
        Assert.Contains(chain, a => a.Type == ArtifactType.Plan);

        // Diff discarded — no merge proposal ever created for this plan run.
        var proposals = await merge.ListAsync();
        Assert.DoesNotContain(proposals, p => p.WorkUnitId == wu.WorkUnitId);

        var sessionEvents = await events.GetSessionEventsAsync("session-plan-dirty");
        Assert.Contains(sessionEvents, e => e.Kind == ExecutionEventKind.HarnessPlanDiffDiscarded);
    }
}
