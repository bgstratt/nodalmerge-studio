using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/harness-hosting-architecture.md Phase B.3 — ClaudeCodeExecutor's harvest pipeline
/// (decisions/inbox harvest, merge.propose + merge.validate, the mechanical build/test gate,
/// AwaitingClarification pause) against a stub CLI. Mirrors FullAgentCycleTests' shape but
/// substitutes the stub-backed ClaudeCodeExecutor for the scripted native loop, per the plan's own
/// B3 acceptance text.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class ClaudeCodeExecutorHarvestTests : IAsyncLifetime
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"claude-harvest-stub-{Guid.NewGuid():N}");

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

    private static string SuccessJsonl(string sessionId = "stub-session-harvest") => string.Join('\n',
        $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}"}""",
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1," +
        "\"result\":\"Implemented the change.\",\"stop_reason\":\"end_turn\",\"session_id\":\"" + sessionId + "\"," +
        "\"total_cost_usd\":0.02,\"usage\":{\"input_tokens\":50,\"output_tokens\":10}}");

    private WebApplicationLike BuildApp(string executablePath, Action<WorkspaceOptions>? configureOptions = null)
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                var claudeOptions = new ClaudeCodeExecutorOptions { ExecutablePath = executablePath, TimeoutSeconds = 30 };
                services.AddSingleton(claudeOptions);
                if (configureOptions is not null)
                {
                    var workspaceOptions = new WorkspaceOptions();
                    configureOptions(workspaceOptions);
                    services.AddSingleton(workspaceOptions);
                }
            });
        return new WebApplicationLike(app);
    }

    // Thin wrapper so callers don't need to know the exact WebApplication type — keeps this file's
    // signatures short.
    private sealed class WebApplicationLike(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        public IServiceProvider Services => app.Services;
    }

    [Fact]
    public async Task FullCycle_stub_edit_reaches_ReadyForReview_and_can_be_approved_and_applied()
    {
        var jsonlPath = Path.Combine(_stubDir, "output.jsonl");
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(jsonlPath, SuccessJsonl());
        var stub = WriteStub("stub-claude.cmd",
            "@echo off\r\necho real change > change.txt\r\ntype \"%~dp0output.jsonl\"\r\n");

        var services = BuildApp(stub).Services;
        var orchestratorSvc = services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the B3 harvest pipeline", "integration-test");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        var executor = resolver.Resolve("claude-code");
        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-harvest-1", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: "session-harvest-1", IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);

        var proposals = await merge.ListAsync();
        var proposal = Assert.Single(proposals, p => p.WorkUnitId == wu.WorkUnitId);
        Assert.Equal(MergeProposalStatus.ReadyForReview, proposal.Status);
        Assert.Equal("agent-claude-harvest-1", proposal.AgentId);
        Assert.Equal("claude-code", proposal.Model);

        var approved = await merge.ReviewAsync(proposal.ProposalId, MergeProposalStatus.Approved);
        var merged = await merge.ApplyAsync(approved.ProposalId);
        Assert.Equal(MergeProposalStatus.Merged, merged.Status);
    }

    [Fact]
    public async Task Build_breaking_run_is_blocked_at_the_gate_and_never_reaches_ReadyForReview()
    {
        var jsonlPath = Path.Combine(_stubDir, "output.jsonl");
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(jsonlPath, SuccessJsonl("stub-session-gate"));
        var stub = WriteStub("stub-claude.cmd",
            "@echo off\r\necho broken change > change.txt\r\ntype \"%~dp0output.jsonl\"\r\n");

        var services = BuildApp(stub, o =>
        {
            o.RequireBuildBeforeProposal = true;
            o.BuildCommand = "exit 1";
        }).Services;
        var orchestratorSvc = services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the build gate", "integration-test");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        var executor = resolver.Resolve("claude-code");
        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-gate-1", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: "session-gate-1", IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: true, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Stalled, result.Completion);
        Assert.NotNull(result.FailureReason);

        var proposals = await merge.ListAsync();
        Assert.DoesNotContain(proposals, p => p.WorkUnitId == wu.WorkUnitId && p.Status == MergeProposalStatus.ReadyForReview);
    }

    [Fact]
    public async Task Inbox_question_pauses_the_work_unit_via_AwaitingClarification()
    {
        var jsonlPath = Path.Combine(_stubDir, "output.jsonl");
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(jsonlPath, SuccessJsonl("stub-session-inbox"));
        var stub = WriteStub("stub-claude.cmd",
            "@echo off\r\n" +
            "if not exist .workspace\\inbox mkdir .workspace\\inbox\r\n" +
            "echo Which database should this use? > .workspace\\inbox\\0001.md\r\n" +
            "type \"%~dp0output.jsonl\"\r\n");

        var services = BuildApp(stub).Services;
        var orchestratorSvc = services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var workUnits = services.GetRequiredService<IWorkUnitService>();
        var clarifications = services.GetRequiredService<IClarificationCommandService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise the inbox pause", "integration-test");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        var executor = resolver.Resolve("claude-code");
        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-inbox-1", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: "session-inbox-1", IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.AwaitingClarification, result.Completion);

        var activeRequests = await clarifications.ListActiveRequestsAsync();
        Assert.Contains(activeRequests, r => r.WorkUnitId == wu.WorkUnitId);

        var updatedWu = await workUnits.GetAsync(wu.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Waiting, updatedWu!.Status);
    }
}
