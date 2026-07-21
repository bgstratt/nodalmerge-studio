using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/review-seam-and-clarification-sessions.md S2 — Mode==Review through the executor seam
/// against stub CLIs, mirroring the *PlanModeTests shape: the adapter materializes
/// .workspace/review-request.json before the spawn, the stub writes a .workspace/review.json
/// verdict, and HarnessHarvestPipeline maps it onto the same IMergeService.AutomatedReviewAsync
/// call the native nm_v1_merge_review tool makes (policy-dependent terminal status included).
/// Also proves the inline site: InlineReviewerService with a claude-cli Review-stage provider
/// routes to the CLI adapter and its Approved verdict satisfies the AgentApproval policy gate.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class HarnessReviewModeSeamTests : IAsyncLifetime
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"review-stub-{Guid.NewGuid():N}");

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

    private static string ClaudeSuccessJsonl(string sessionId = "stub-session-review") => string.Join('\n',
        $$"""{"type":"system","subtype":"init","session_id":"{{sessionId}}"}""",
        "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"num_turns\":1," +
        "\"result\":\"Reviewed the proposal.\",\"stop_reason\":\"end_turn\",\"session_id\":\"" + sessionId + "\"," +
        "\"total_cost_usd\":0.01,\"usage\":{\"input_tokens\":30,\"output_tokens\":8}}");

    private static string CodexSuccessJsonl(string threadId = "stub-thread-review") => string.Join('\n',
        $$"""{"type":"thread.started","thread_id":"{{threadId}}"}""",
        """{"type":"turn.started"}""",
        """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Reviewed the proposal."}}""",
        """{"type":"turn.completed","usage":{"input_tokens":30,"output_tokens":8}}""");

    private string WriteClaudeStub(string? verdictJson, string jsonl)
    {
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(Path.Combine(_stubDir, "output.jsonl"), jsonl);
        var script =
            "@echo off\r\n" +
            "if not exist .workspace mkdir .workspace\r\n";
        if (verdictJson is not null)
        {
            File.WriteAllText(Path.Combine(_stubDir, "review.json"), verdictJson);
            script += "copy /Y \"%~dp0review.json\" \".workspace\\review.json\" >nul\r\n";
        }
        script += "type \"%~dp0output.jsonl\"\r\n";
        return WriteStub("stub-claude.cmd", script);
    }

    // Simulates a model that cd'd into a nested project directory to run tests and never cd'd back:
    // it writes its verdict to <subdir>/.workspace/review.json instead of the branch-root
    // .workspace/review.json. Exercises HarnessHarvestPipeline's misplaced-verdict recovery.
    private string WriteClaudeStubNestedVerdict(string verdictJson, string jsonl)
    {
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(Path.Combine(_stubDir, "output.jsonl"), jsonl);
        File.WriteAllText(Path.Combine(_stubDir, "review.json"), verdictJson);
        var script =
            "@echo off\r\n" +
            "if not exist nested\\.workspace mkdir nested\\.workspace\r\n" +
            "copy /Y \"%~dp0review.json\" \"nested\\.workspace\\review.json\" >nul\r\n" +
            "type \"%~dp0output.jsonl\"\r\n";
        return WriteStub("stub-claude-nested.cmd", script);
    }

    private static IServiceProvider BuildApp(Action<IServiceCollection> configure)
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                configure(services);
            });
        return app.Services;
    }

    // Arranges the reviewable state every test needs: a work unit, a real edit on its branch, and
    // a validated ReadyForReview proposal (TaskId carries the proposalId into the Review run).
    private static async Task<(WorkUnit Wu, MergeProposal Proposal)> ArrangeProposalAsync(
        IServiceProvider services, ReviewPolicy taskReviewPolicy)
    {
        var orchestratorSvc = services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var mergeCommands = services.GetRequiredService<IMergeCommandService>();

        // Both policies: these are top-level work units, which WorkspaceReviewScope gates by
        // WorkspaceReviewPolicy (their apply can reach the real repo), not TaskReviewPolicy.
        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            "Exercise S2 review mode", "integration-test",
            taskReviewPolicy: taskReviewPolicy, workspaceReviewPolicy: taskReviewPolicy);
        await fileWorkspace.InitBranchAsync("main");
        await fileWorkspace.InitBranchAsync(wu.BranchId);
        await fileWorkspace.WriteAsync(wu.BranchId, "src/A.cs", "// proposed change");

        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: wu.BranchId, targetBranch: "main", summary: "Add A.cs",
            workUnitId: wu.WorkUnitId, agentId: "worker-1");
        proposal = await mergeCommands.ValidateAsync(proposal.ProposalId);
        Assert.Equal(MergeProposalStatus.ReadyForReview, proposal.Status);
        return (wu, proposal);
    }

    private static HarnessRunRequest ReviewRequest(WorkUnit wu, string proposalId, string agentId) => new(
        HarnessMode.Review, agentId, wu.WorkUnitId, proposalId,
        Profile: null, SessionId: $"session-{agentId}", IsResume: false,
        RuleFileContext: null, PromptGuidanceContext: null,
        SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null);

    [Fact]
    public async Task Claude_stub_approved_verdict_lands_via_AutomatedReviewAsync()
    {
        var stub = WriteClaudeStub(
            """{"decision":"Approved","verificationResults":"Change matches the goal; nothing out of scope."}""",
            ClaudeSuccessJsonl());
        var services = BuildApp(s =>
            s.AddSingleton(new ClaudeCodeExecutorOptions { ExecutablePath = stub, TimeoutSeconds = 30 }));

        // AgentApproval on a real-repo proposal auto-applies on approval: the verdict lands as
        // Approved and then immediately merges (the documented "auto-applies on approval"), so the
        // observable terminal status is Merged with AutoApplied set.
        var (wu, proposal) = await ArrangeProposalAsync(services, ReviewPolicy.AgentApproval);
        var fileWorkspace = services.GetRequiredService<IFileWorkspaceService>();
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var executor = resolver.Resolve("claude-code");
        Assert.True(executor.Capabilities.SupportsReviewMode);
        var result = await executor.RunAsync(ReviewRequest(wu, proposal.ProposalId, "agent-claude-review-1"));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);

        // The request contract was materialized for the stub before the spawn.
        var reviewRequestJson = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/review-request.json");
        Assert.Contains(proposal.ProposalId, reviewRequestJson);
        Assert.Contains("src/A.cs", reviewRequestJson);

        var reviewed = await merge.GetAsync(proposal.ProposalId);
        Assert.Equal(MergeProposalStatus.Merged, reviewed!.Status);
        Assert.True(reviewed.AutoApplied);
        Assert.Contains("matches the goal", reviewed.VerificationResults);
        Assert.Equal("agent-claude-review-1", reviewed.ReviewedBy);
    }

    [Fact]
    public async Task Claude_stub_rejected_verdict_carries_verificationResults_for_the_retry()
    {
        var stub = WriteClaudeStub(
            """{"decision":"Rejected","verificationResults":"Missing tests for the new branch logic."}""",
            ClaudeSuccessJsonl("stub-session-reject"));
        var services = BuildApp(s =>
            s.AddSingleton(new ClaudeCodeExecutorOptions { ExecutablePath = stub, TimeoutSeconds = 30 }));

        var (wu, proposal) = await ArrangeProposalAsync(services, ReviewPolicy.AgentApproval);
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var result = await resolver.Resolve("claude-code")
            .RunAsync(ReviewRequest(wu, proposal.ProposalId, "agent-claude-review-2"));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);
        var reviewed = await merge.GetAsync(proposal.ProposalId);
        Assert.Equal(MergeProposalStatus.Rejected, reviewed!.Status);
        Assert.Contains("Missing tests", reviewed.VerificationResults);
    }

    [Fact]
    public async Task Missing_verdict_file_stalls_and_leaves_the_proposal_reviewable()
    {
        var stub = WriteClaudeStub(verdictJson: null, ClaudeSuccessJsonl("stub-session-noverdict"));
        var services = BuildApp(s =>
            s.AddSingleton(new ClaudeCodeExecutorOptions { ExecutablePath = stub, TimeoutSeconds = 30 }));

        var (wu, proposal) = await ArrangeProposalAsync(services, ReviewPolicy.AgentApproval);
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var result = await resolver.Resolve("claude-code")
            .RunAsync(ReviewRequest(wu, proposal.ProposalId, "agent-claude-review-3"));

        Assert.Equal(AgentLoopCompletion.Stalled, result.Completion);
        Assert.Contains("review.json", result.FailureReason);

        // Inconclusive, not rejected — the proposal stays where Retry/Continue can pick it up.
        var untouched = await merge.GetAsync(proposal.ProposalId);
        Assert.Equal(MergeProposalStatus.ReadyForReview, untouched!.Status);
    }

    [Fact]
    public async Task Codex_stub_approved_verdict_lands_the_same_way()
    {
        Directory.CreateDirectory(_stubDir);
        File.WriteAllText(Path.Combine(_stubDir, "output.jsonl"), CodexSuccessJsonl());
        File.WriteAllText(Path.Combine(_stubDir, "review.json"),
            """{"decision":"Approved","verificationResults":"Verified: matches goal, builds clean."}""");
        var stub = WriteStub("stub-codex.cmd",
            "@echo off\r\n" +
            "set /p dummy=\r\n" +
            "if not exist .workspace mkdir .workspace\r\n" +
            "copy /Y \"%~dp0review.json\" \".workspace\\review.json\" >nul\r\n" +
            "type \"%~dp0output.jsonl\"\r\n");
        var services = BuildApp(s =>
            s.AddSingleton(new CodexCliExecutorOptions { ExecutablePath = stub, TimeoutSeconds = 30 }));

        var (wu, proposal) = await ArrangeProposalAsync(services, ReviewPolicy.AgentApproval);
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var executor = resolver.Resolve("codex");
        Assert.True(executor.Capabilities.SupportsReviewMode);
        var result = await executor.RunAsync(ReviewRequest(wu, proposal.ProposalId, "agent-codex-review-1"));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);
        var reviewed = await merge.GetAsync(proposal.ProposalId);
        Assert.Equal(MergeProposalStatus.Merged, reviewed!.Status);
        Assert.True(reviewed.AutoApplied);
        Assert.Contains("builds clean", reviewed.VerificationResults);
    }

    [Fact]
    public async Task Inline_reviewer_with_claude_cli_provider_routes_to_the_CLI_adapter_and_approves()
    {
        var stub = WriteClaudeStub(
            """{"decision":"Approved","verificationResults":"Inline route verified."}""",
            ClaudeSuccessJsonl("stub-session-inline"));
        var services = BuildApp(s =>
            s.AddSingleton(new ClaudeCodeExecutorOptions { ExecutablePath = stub, TimeoutSeconds = 30 }));

        var (wu, proposal) = await ArrangeProposalAsync(services, ReviewPolicy.AgentApproval);
        var agentControl = services.GetRequiredService<IAgentControlService>();
        var inlineReviewer = services.GetRequiredService<IInlineReviewerService>();
        var merge = services.GetRequiredService<IMergeService>();

        // The user's Agent Topology choice: a claude-cli Model Profile on this goal. No baseUrl
        // and no apiKey — blank means ambient CLI auth, and registration must accept that for CLI
        // providers (plans/orchestrator-pure-service.md M1 removed the placeholder-baseUrl
        // requirement). What matters is provider="claude-cli" routing the inline reviewer to
        // ClaudeCodeExecutor instead of DefaultAgentToolClient's garbage HTTP.
        var registered = await agentControl.ResupplyCredentialsAsync(
            wu.WorkUnitId, overrideModel: "", overrideProvider: "claude-cli");
        Assert.True(registered);

        var inlineResult = await inlineReviewer.ReviewAsync(wu.WorkUnitId, proposal.ProposalId);

        Assert.True(inlineResult.Approved);
        Assert.Contains("Inline route verified", inlineResult.Notes);
        var reviewed = await merge.GetAsync(proposal.ProposalId);
        Assert.Equal(MergeProposalStatus.Merged, reviewed!.Status);
        Assert.True(reviewed.AutoApplied);
    }

    [Fact]
    public async Task Misplaced_verdict_in_nested_workspace_is_recovered_not_stalled()
    {
        // The model wrote its verdict to nested/.workspace/review.json (a dir it cd'd into) instead
        // of the branch-root .workspace/review.json. The harvest must recover it rather than Stall a
        // review that genuinely produced a verdict.
        var stub = WriteClaudeStubNestedVerdict(
            """{"decision":"Approved","verificationResults":"Recovered from nested dir."}""",
            ClaudeSuccessJsonl("stub-session-nested"));
        var services = BuildApp(s =>
            s.AddSingleton(new ClaudeCodeExecutorOptions { ExecutablePath = stub, TimeoutSeconds = 30 }));

        var (wu, proposal) = await ArrangeProposalAsync(services, ReviewPolicy.AgentApproval);
        var merge = services.GetRequiredService<IMergeService>();
        var resolver = services.GetRequiredService<IHarnessExecutorResolver>();

        var result = await resolver.Resolve("claude-code")
            .RunAsync(ReviewRequest(wu, proposal.ProposalId, "agent-claude-review-nested"));

        // Not Stalled: the verdict was recovered from the misplaced nested path and landed the same
        // way a root-written verdict would (AgentApproval auto-applies → Merged).
        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);
        var reviewed = await merge.GetAsync(proposal.ProposalId);
        Assert.Equal(MergeProposalStatus.Merged, reviewed!.Status);
        Assert.True(reviewed.AutoApplied);
        Assert.Contains("Recovered from nested dir", reviewed.VerificationResults);
    }
}
