using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — CodexCliExecutor
/// against a stub CLI (a batch file + companion .jsonl, written fresh per test run), never the real
/// `codex` binary — same posture as ClaudeCodeExecutorTests. The stub's --json content is a
/// sanitized, generic-path copy of the real `codex exec --json --skip-git-repo-check` capture taken
/// 2026-07-12 against codex-cli 0.144.1 (codex-probe/capture-3.jsonl): a thread.started line, an
/// agent_message-only first turn, a second turn whose file_change + command_execution items land
/// before the agent_message that reports on them (the real capture's own ordering), then
/// turn.completed with usage tokens and no cost field.
/// </summary>
[Trait("Category", "Integration")]
public class CodexCliExecutorTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"codex-stub-{Guid.NewGuid():N}");

    public CodexCliExecutorTests()
    {
        Directory.CreateDirectory(_stubDir);

        var jsonl = string.Join('\n',
            """{"type":"thread.started","thread_id":"stub-thread-001"}""",
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Reading the task."}}""",
            """{"type":"item.started","item":{"id":"item_1","type":"file_change","changes":[{"path":"stub-edit.txt","kind":"add"}],"status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"item_1","type":"file_change","changes":[{"path":"stub-edit.txt","kind":"add"}],"status":"completed"}}""",
            """{"type":"item.started","item":{"id":"item_2","type":"command_execution","command":"echo hi","aggregated_output":"","exit_code":null,"status":"in_progress"}}""",
            """{"type":"item.completed","item":{"id":"item_2","type":"command_execution","command":"echo hi","aggregated_output":"hi\n","exit_code":0,"status":"completed"}}""",
            """{"type":"item.completed","item":{"id":"item_3","type":"agent_message","text":"Stub run complete."}}""",
            """{"type":"turn.completed","usage":{"input_tokens":42,"cached_input_tokens":10,"output_tokens":7,"reasoning_output_tokens":3}}""");
        File.WriteAllText(Path.Combine(_stubDir, "stub-output.jsonl"), jsonl);

        // %~dp0 = this .cmd's own directory regardless of the process's working directory (the
        // branch workdir) — same npm-shim-style technique ClaudeCodeExecutorTests' stub uses.
        // `set /p dummy=` reads one line from stdin: if stdin was actually closed (the behavior
        // CodexCliExecutor.RunAsync must produce), it returns immediately at EOF; if stdin were
        // left attached/open instead, this line would block forever and the test would time out —
        // this is the "stdin-closed behavior" acceptance check.
        var cmd = "@echo off\r\n" +
            "set /p dummy=\r\n" +
            "echo stdin-read-completed > stdin-check.txt\r\n" +
            "echo stub file content > stub-edit.txt\r\n" +
            "echo OPENAI_API_KEY=%OPENAI_API_KEY% > env-check.txt\r\n" +
            "echo %* > args.txt\r\n" +
            "type \"%~dp0stub-output.jsonl\"\r\n";
        File.WriteAllText(Path.Combine(_stubDir, "stub-codex.cmd"), cmd);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stubDir))
            Directory.Delete(_stubDir, recursive: true);
    }

    private async Task<(IHarnessExecutor Executor, IWorkUnitService WorkUnits, IFileWorkspaceService FileWorkspace,
        WorkUnit Wu, IConversationLogService ConversationLog)>
        BuildAsync(int timeoutSeconds = 30, string? executablePath = null)
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new CodexCliExecutorOptions
                {
                    ExecutablePath = executablePath ?? Path.Combine(_stubDir, "stub-codex.cmd"),
                    TimeoutSeconds = timeoutSeconds,
                });
            });

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var resolver = app.Services.GetRequiredService<IHarnessExecutorResolver>();
        var conversationLog = app.Services.GetRequiredService<IConversationLogService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync("Exercise CodexCliExecutor", "integration-test");
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        return (resolver.Resolve("codex"), workUnits, fileWorkspace, wu, conversationLog);
    }

    [Fact]
    public async Task RunAsync_spawns_in_the_real_branch_directory_and_completes_with_null_cost()
    {
        var (executor, _, fileWorkspace, wu, _) = await BuildAsync();

        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-1", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);
        Assert.Equal("Stub run complete.", result.Summary);
        Assert.Equal(42, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Null(result.CostUsd);
        Assert.Equal("stub-thread-001", result.HarnessSessionId);

        var edited = await fileWorkspace.ReadAsync(wu.BranchId, "stub-edit.txt");
        Assert.NotNull(edited);
        Assert.Contains("stub file content", edited);
    }

    // Proves RunAsync closed stdin before/at spawn: `set /p dummy=` in the stub only returns
    // (rather than blocking indefinitely) when it hits immediate EOF on stdin.
    [Fact]
    public async Task RunAsync_closes_stdin_so_the_stub_does_not_hang_on_a_stdin_read()
    {
        var (executor, _, fileWorkspace, wu, _) = await BuildAsync(timeoutSeconds: 15);

        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-2", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);
        var stdinCheck = await fileWorkspace.ReadAsync(wu.BranchId, "stdin-check.txt");
        Assert.NotNull(stdinCheck);
        Assert.Contains("stdin-read-completed", stdinCheck);
    }

    [Fact]
    public async Task RunAsync_invokes_OnActivity_with_assistant_text_as_it_streams()
    {
        var (executor, _, _, wu, _) = await BuildAsync();
        var activity = new List<string?>();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-3", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: a => activity.Add(a)));

        Assert.Contains("Reading the task.", activity);
        Assert.Contains("Stub run complete.", activity);
    }

    [Fact]
    public async Task RunAsync_maps_a_wall_clock_timeout_to_MaxIterationsExceeded()
    {
        var hangingCmd = "@echo off\r\nset /p dummy=\r\n:loop\r\nping -n 2 127.0.0.1 > nul\r\ngoto loop\r\n";
        File.WriteAllText(Path.Combine(_stubDir, "hanging-codex.cmd"), hangingCmd);

        var (executor, _, _, wu, _) = await BuildAsync(
            timeoutSeconds: 1, executablePath: Path.Combine(_stubDir, "hanging-codex.cmd"));

        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-4", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.MaxIterationsExceeded, result.Completion);
        Assert.Contains("wall-clock", result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_does_not_inject_OPENAI_API_KEY_by_default()
    {
        var (executor, _, fileWorkspace, wu, _) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-5", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null,
            ApiKey: "should-not-leak"));

        var envCheck = await fileWorkspace.ReadAsync(wu.BranchId, "env-check.txt");
        Assert.NotNull(envCheck);
        Assert.DoesNotContain("should-not-leak", envCheck);
    }

    [Fact]
    public async Task RunAsync_injects_OPENAI_API_KEY_when_the_profile_opts_in()
    {
        var (executor, _, fileWorkspace, wu, _) = await BuildAsync();
        var profile = new AgentProfile(
            "codex-headless-profile", "Headless Codex", PipelineStage.Execute,
            SystemPrompt: "unused", AllowedTools: [], MaxIterations: 5, FileScopePatterns: [],
            Executor: "codex", InjectApiKeyEnv: true);

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-6", wu.WorkUnitId, "task-1",
            Profile: profile, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null,
            ApiKey: "sk-test-headless-key"));

        var envCheck = await fileWorkspace.ReadAsync(wu.BranchId, "env-check.txt");
        Assert.NotNull(envCheck);
        Assert.Contains("sk-test-headless-key", envCheck);
    }

    [Fact]
    public async Task RunAsync_persists_the_codex_thread_id_and_resumes_with_exec_resume_ordering()
    {
        var (executor, workUnits, fileWorkspace, wu, _) = await BuildAsync();

        var first = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-7a", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));
        Assert.Equal("stub-thread-001", first.HarnessSessionId);

        var updated = await workUnits.GetAsync(wu.WorkUnitId);
        Assert.NotNull(updated!.Metadata);
        Assert.Equal("stub-thread-001", updated.Metadata!["codexThreadId"]);

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-7b", wu.WorkUnitId, "task-2",
            Profile: null, SessionId: null, IsResume: true,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var args = await fileWorkspace.ReadAsync(wu.BranchId, "args.txt");
        Assert.NotNull(args);
        // "exec resume <thread_id>" ordering — resume is a positional subcommand of exec, verified
        // against the real codex-probe capture-5-resume, not a --resume flag like claude's.
        Assert.Contains("exec resume stub-thread-001", args);
    }

    [Fact]
    public async Task RunAsync_does_not_resume_a_prior_thread_on_a_fresh_non_resume_attempt()
    {
        var (executor, _, fileWorkspace, wu, _) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-8a", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-8b", wu.WorkUnitId, "task-2",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var args = await fileWorkspace.ReadAsync(wu.BranchId, "args.txt");
        Assert.NotNull(args);
        Assert.DoesNotContain("resume", args);
    }

    [Fact]
    public async Task RunAsync_records_one_conversation_log_entry_per_turn_plus_a_terminal_entry_with_tokens()
    {
        var (executor, _, _, wu, conversationLog) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-9", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var entries = await conversationLog.GetEntriesAsync(wu.WorkUnitId);

        // Fixture has 2 agent_message-committed turns (turn0 text-only, turn1 carrying the
        // file_change + command_execution items that arrived before it) plus 1 terminal entry.
        Assert.Equal(3, entries.Count);

        var turn0 = entries.Single(e => e.CycleNumber == 0);
        Assert.Equal("Reading the task.", turn0.AssistantText);
        Assert.Equal("openai", turn0.Provider);
        Assert.Null(turn0.InputTokens);
        Assert.Null(turn0.OutputTokens);
        Assert.Empty(turn0.ToolCalls);

        var turn1 = entries.Single(e => e.CycleNumber == 1 && e.LogId.StartsWith("CLE-turn-", StringComparison.Ordinal));
        Assert.Equal("Stub run complete.", turn1.AssistantText);
        Assert.Equal(2, turn1.ToolCalls.Count);
        var fileChangeCall = turn1.ToolCalls.Single(c => c.Name == "file_change");
        Assert.Contains("stub-edit.txt", fileChangeCall.InputJson);
        var commandCall = turn1.ToolCalls.Single(c => c.Name == "command_execution");
        Assert.Contains("echo hi", commandCall.InputJson);
        var commandResult = turn1.ToolResults.Single(r => r.ToolUseId == commandCall.ToolUseId);
        Assert.Contains("hi", commandResult.Result);

        var terminal = entries.Single(e => e.LogId.StartsWith("CLE-", StringComparison.Ordinal) &&
            !e.LogId.StartsWith("CLE-turn-", StringComparison.Ordinal));
        Assert.Equal(2, terminal.CycleNumber);
        Assert.Equal("Stub run complete.", terminal.AssistantText);
        Assert.Equal(42, terminal.InputTokens);
        Assert.Equal(7, terminal.OutputTokens);
        Assert.False(terminal.TokensEstimated);
        Assert.Equal("stub-thread-001", terminal.SessionId);

        Assert.All(entries, e => Assert.Equal("stub-thread-001", e.SessionId));
    }

    [Fact]
    public async Task RunAsync_degrades_to_a_single_run_level_entry_when_the_transcript_format_is_unrecognized()
    {
        // No `thread.started` line at all — CodexTranscriptParser.V1's format marker never fires,
        // so turn reconstruction stays off even though item.completed/turn.completed lines are
        // present. The run must still succeed using only the last agent_message text + turn.completed
        // usage tokens (the degrade rule), never fail because the transcript didn't match the shape.
        var jsonl = string.Join('\n',
            """{"type":"turn.started"}""",
            """{"type":"item.completed","item":{"id":"item_0","type":"agent_message","text":"Degraded run complete."}}""",
            """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":2}}""");
        File.WriteAllText(Path.Combine(_stubDir, "unrecognized-output.jsonl"), jsonl);

        var cmd = "@echo off\r\nset /p dummy=\r\ntype \"%~dp0unrecognized-output.jsonl\"\r\n";
        File.WriteAllText(Path.Combine(_stubDir, "unrecognized-codex.cmd"), cmd);

        var (executor, _, _, wu, conversationLog) = await BuildAsync(
            executablePath: Path.Combine(_stubDir, "unrecognized-codex.cmd"));

        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-codex-10", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);
        Assert.Equal("Degraded run complete.", result.Summary);
        Assert.Null(result.HarnessSessionId);

        var entries = await conversationLog.GetEntriesAsync(wu.WorkUnitId);
        var entry = Assert.Single(entries);
        Assert.Equal(0, entry.CycleNumber);
        Assert.Equal(10, entry.InputTokens);
        Assert.Equal(2, entry.OutputTokens);
        Assert.Equal("Degraded run complete.", entry.AssistantText);
        Assert.StartsWith("CLE-", entry.LogId, StringComparison.Ordinal);
        Assert.False(entry.LogId.StartsWith("CLE-turn-", StringComparison.Ordinal));
    }
}
