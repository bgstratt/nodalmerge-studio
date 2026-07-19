using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/harness-hosting-architecture.md Phase B.2 — ClaudeCodeExecutor against a stub CLI (a
/// batch file + companion .jsonl, written fresh per test run), never the real `claude` binary —
/// a single trivial real invocation during this phase's research cost $0.044, so automated tests
/// must not shell out to it. The stub's stream-json content is a cleaned-up copy of one real
/// `claude -p --output-format stream-json` run captured 2026-07-12 against claude 2.1.177.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class ClaudeCodeExecutorTests : IDisposable
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"claude-stub-{Guid.NewGuid():N}");

    public ClaudeCodeExecutorTests()
    {
        Directory.CreateDirectory(_stubDir);

        // Multi-turn shape (Phase C.1) modeled on the same real capture the original single-text
        // stub summarized: a `system`/`init` line (the ClaudeTranscriptParser.V1 format marker), a
        // first assistant turn that emits a tool_use block, a `user` line carrying that tool_use's
        // tool_result (matched by tool_use_id), a second assistant turn with only text, then the
        // terminal `result` line — unchanged from before so every pre-C1 assertion on tokens/cost/
        // resultText/sessionId still holds.
        var jsonl = string.Join('\n',
            """{"type":"system","subtype":"init","session_id":"stub-session-001"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-4-5-20250929","stop_reason":"tool_use","content":[{"type":"text","text":"Reading the file first."},{"type":"tool_use","id":"toolu_stub01","name":"Read","input":{"file_path":"stub-edit.txt"}}]},"session_id":"stub-session-001"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_stub01","content":"stub file content","is_error":false}]},"session_id":"stub-session-001"}""",
            """{"type":"assistant","message":{"model":"claude-sonnet-4-5-20250929","stop_reason":"end_turn","content":[{"type":"text","text":"Working on it."}]},"session_id":"stub-session-001"}""",
            """{"type":"result","subtype":"success","is_error":false,"num_turns":2,"result":"Stub run complete.","stop_reason":"end_turn","session_id":"stub-session-001","total_cost_usd":0.0123,"usage":{"input_tokens":42,"output_tokens":7}}""");
        File.WriteAllText(Path.Combine(_stubDir, "stub-output.jsonl"), jsonl);

        // %~dp0 = the directory containing this .cmd file itself, regardless of the process's
        // working directory (which will be the branch workdir, not _stubDir) — the same technique
        // an npm-style shim uses to find its own companion files. Also records whatever
        // ANTHROPIC_API_KEY it inherited (or didn't) so tests can assert on env-key injection, and
        // its own full argument list so tests can assert on --add-dir/--resume without needing to
        // inspect ProcessStartInfo directly (the real process is spawned via cmd.exe /c).
        var cmd = "@echo off\r\n" +
            "echo stub file content > stub-edit.txt\r\n" +
            "echo ANTHROPIC_API_KEY=%ANTHROPIC_API_KEY% > env-check.txt\r\n" +
            "echo %* > args.txt\r\n" +
            "type \"%~dp0stub-output.jsonl\"\r\n";
        File.WriteAllText(Path.Combine(_stubDir, "stub-claude.cmd"), cmd);
    }

    public void Dispose()
    {
        if (Directory.Exists(_stubDir))
            Directory.Delete(_stubDir, recursive: true);
    }

    private async Task<(IHarnessExecutor Executor, IWorkUnitService WorkUnits, IFileWorkspaceService FileWorkspace,
        IRepositoryRegistryService RepositoryRegistry, WorkUnit Wu, IConversationLogService ConversationLog)>
        BuildAsync(
            int timeoutSeconds = 30, string? executablePath = null,
            // Lets a test register a repo against this same app's IRepositoryRegistryService and
            // hand back the referenceFiles to seed the work unit with, before InitBranchAsync runs.
            Func<IRepositoryRegistryService, Task<IReadOnlyList<FileReferenceV1>?>>? referenceFilesFactory = null,
            // Phase C.4 (phase-c-implementation.md C3) — normally set post-bind from
            // IServerAddressesFeature (StudioWebApplication.Build); tests that need the
            // ".workspace/mcp.json" + "--mcp-config" gate to fire set it directly here instead of
            // spinning up a real Kestrel listener.
            string? harnessMcpBaseUrl = null)
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ScriptedLlmHandler()),
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new ClaudeCodeExecutorOptions
                {
                    ExecutablePath = executablePath ?? Path.Combine(_stubDir, "stub-claude.cmd"),
                    TimeoutSeconds = timeoutSeconds,
                    HarnessMcpBaseUrl = harnessMcpBaseUrl,
                });
            });

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var repositoryRegistry = app.Services.GetRequiredService<IRepositoryRegistryService>();
        var resolver = app.Services.GetRequiredService<IHarnessExecutorResolver>();
        var conversationLog = app.Services.GetRequiredService<IConversationLogService>();

        var referenceFiles = referenceFilesFactory is null ? null : await referenceFilesFactory(repositoryRegistry);
        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            "Exercise ClaudeCodeExecutor", "integration-test", referenceFiles: referenceFiles);
        await fileWorkspace.InitBranchAsync(wu.BranchId);

        return (resolver.Resolve("claude-code"), workUnits, fileWorkspace, repositoryRegistry, wu, conversationLog);
    }

    [Fact]
    public async Task RunAsync_spawns_in_the_real_branch_directory_and_parses_the_terminal_result()
    {
        var (executor, _, fileWorkspace, _, wu, _) = await BuildAsync();

        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-1", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);
        Assert.Equal("Stub run complete.", result.Summary);
        Assert.Equal(42, result.InputTokens);
        Assert.Equal(7, result.OutputTokens);
        Assert.Equal(0.0123, result.CostUsd);
        Assert.Equal("stub-session-001", result.HarnessSessionId);

        // Confirms cwd was the real branch directory — the stub's file edit landed on disk there.
        var edited = await fileWorkspace.ReadAsync(wu.BranchId, "stub-edit.txt");
        Assert.NotNull(edited);
        Assert.Contains("stub file content", edited);
    }

    [Fact]
    public async Task RunAsync_materializes_the_workspace_contract_and_a_generated_settings_file()
    {
        var (executor, _, fileWorkspace, _, wu, _) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-2", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.NotNull(await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/manifest.json"));
        Assert.NotNull(await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/goal.json"));

        var settingsJson = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/settings.json");
        Assert.NotNull(settingsJson);
        Assert.Contains("\"allow\"", settingsJson);
        Assert.Contains("Edit(", settingsJson);
        Assert.Contains("Write(", settingsJson);
        Assert.Contains("Read(", settingsJson);
    }

    // plans/harness-hosting-architecture.md Phase C.4 (phase-c-implementation.md C3) — .mcp.json
    // generation + --mcp-config, gated on Capabilities.SupportsMcp AND the Host's own listening
    // address being known.
    [Fact]
    public async Task RunAsync_generates_mcp_config_and_passes_mcp_config_arg_when_the_base_url_is_known()
    {
        var (executor, _, fileWorkspace, _, wu, _) = await BuildAsync(harnessMcpBaseUrl: "http://127.0.0.1:5279");

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-mcp-1", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: "session-mcp-1", IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var mcpConfigJson = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/mcp.json");
        Assert.NotNull(mcpConfigJson);
        Assert.Contains("\"nodalmerge-harness\"", mcpConfigJson);
        Assert.Contains("http://127.0.0.1:5279/mcp-harness", mcpConfigJson);
        Assert.Contains("\"Authorization\":\"Bearer ", mcpConfigJson);

        var args = await fileWorkspace.ReadAsync(wu.BranchId, "args.txt");
        Assert.NotNull(args);
        Assert.Contains("--mcp-config", args);

        // .workspace/mcp.json must never surface as a tracked (ADDED/MODIFIED/DELETED) path in a
        // branch diff — same exclusion WorkspacePathFilter.IgnoredDirNames already applies to
        // settings.json/manifest.json. (The diff's own content preview of args.txt legitimately
        // mentions the string "mcp.json" — it's the recorded --mcp-config argument value — so this
        // asserts on the specific "ADDED: .workspace/..." label line, not the bare substring.)
        var diff = await fileWorkspace.DiffAsync(wu.BranchId, "main");
        Assert.DoesNotContain("ADDED: .workspace", diff);
        Assert.DoesNotContain("MODIFIED: .workspace", diff);
        Assert.DoesNotContain("DELETED: .workspace", diff);
    }

    [Fact]
    public async Task RunAsync_does_not_generate_mcp_config_when_the_base_url_is_unknown()
    {
        // Default BuildAsync() leaves HarnessMcpBaseUrl null — the pre-C3/headless-BuildPeer case.
        var (executor, _, fileWorkspace, _, wu, _) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-mcp-2", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Null(await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/mcp.json"));

        var args = await fileWorkspace.ReadAsync(wu.BranchId, "args.txt");
        Assert.NotNull(args);
        Assert.DoesNotContain("--mcp-config", args);
    }

    [Fact]
    public async Task RunAsync_invokes_OnActivity_with_assistant_text_as_it_streams()
    {
        var (executor, _, _, _, wu, _) = await BuildAsync();
        var activity = new List<string?>();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-3", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: a => activity.Add(a)));

        Assert.Contains("Working on it.", activity);
    }

    [Fact]
    public async Task RunAsync_maps_a_wall_clock_timeout_to_MaxIterationsExceeded()
    {
        // A stub that never terminates — proves the CancelAfter + Kill(entireProcessTree) path,
        // not just the happy path.
        var hangingCmd = "@echo off\r\n:loop\r\nping -n 2 127.0.0.1 > nul\r\ngoto loop\r\n";
        File.WriteAllText(Path.Combine(_stubDir, "hanging-claude.cmd"), hangingCmd);

        var (executor, _, _, _, wu, _) = await BuildAsync(
            timeoutSeconds: 1, executablePath: Path.Combine(_stubDir, "hanging-claude.cmd"));

        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-4", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.MaxIterationsExceeded, result.Completion);
        Assert.Contains("wall-clock", result.FailureReason);
    }

    [Fact]
    public async Task RunAsync_does_not_inject_ANTHROPIC_API_KEY_by_default()
    {
        var (executor, _, fileWorkspace, _, wu, _) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-5", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null,
            ApiKey: "should-not-leak"));

        var envCheck = await fileWorkspace.ReadAsync(wu.BranchId, "env-check.txt");
        Assert.NotNull(envCheck);
        Assert.DoesNotContain("should-not-leak", envCheck);
    }

    [Fact]
    public async Task RunAsync_injects_ANTHROPIC_API_KEY_when_the_profile_opts_in()
    {
        var (executor, _, fileWorkspace, _, wu, _) = await BuildAsync();
        var profile = new AgentProfile(
            "claude-headless-profile", "Headless Claude", PipelineStage.Execute,
            SystemPrompt: "unused", AllowedTools: [], MaxIterations: 5, FileScopePatterns: [],
            Executor: "claude-code", InjectApiKeyEnv: true);

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-6", wu.WorkUnitId, "task-1",
            Profile: profile, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null,
            ApiKey: "sk-test-headless-key"));

        var envCheck = await fileWorkspace.ReadAsync(wu.BranchId, "env-check.txt");
        Assert.NotNull(envCheck);
        Assert.Contains("sk-test-headless-key", envCheck);
    }

    [Fact]
    public async Task RunAsync_passes_add_dir_and_a_read_only_allow_entry_for_cross_repo_reference_files()
    {
        var externalRepoDir = Path.Combine(Path.GetTempPath(), $"claude-external-repo-{Guid.NewGuid():N}");
        Directory.CreateDirectory(externalRepoDir);
        try
        {
            var (executor, _, fileWorkspace, _, wu, _) = await BuildAsync(referenceFilesFactory: async repositoryRegistry =>
            {
                var repo = await repositoryRegistry.RegisterAsync(externalRepoDir, "external-repo");
                return [new FileReferenceV1(repo.RepositoryId, "notes.md")];
            });

            await executor.RunAsync(new HarnessRunRequest(
                HarnessMode.Execute, "agent-claude-7", wu.WorkUnitId, "task-1",
                Profile: null, SessionId: null, IsResume: false,
                RuleFileContext: null, PromptGuidanceContext: null,
                SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

            var settingsJson = await fileWorkspace.ReadAsync(wu.BranchId, ".workspace/settings.json");
            Assert.NotNull(settingsJson);
            var repoPattern = "//" + externalRepoDir.Replace('\\', '/').Replace(":", "").ToLowerInvariant().TrimStart('/');
            Assert.Contains($"Read({repoPattern}/**)", settingsJson);
            Assert.DoesNotContain($"Edit({repoPattern}", settingsJson);
            Assert.DoesNotContain($"Write({repoPattern}", settingsJson);

            var args = await fileWorkspace.ReadAsync(wu.BranchId, "args.txt");
            Assert.NotNull(args);
            Assert.Contains("--add-dir", args);
            Assert.Contains(externalRepoDir, args);
        }
        finally
        {
            Directory.Delete(externalRepoDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_persists_the_claude_session_id_and_resumes_with_it_on_the_next_attempt()
    {
        var (executor, workUnits, fileWorkspace, _, wu, _) = await BuildAsync();

        var first = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-8a", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));
        Assert.Equal("stub-session-001", first.HarnessSessionId);

        var updated = await workUnits.GetAsync(wu.WorkUnitId);
        Assert.NotNull(updated!.Metadata);
        Assert.Equal("stub-session-001", updated.Metadata!["claudeCodeSessionId"]);

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-8b", wu.WorkUnitId, "task-2",
            Profile: null, SessionId: null, IsResume: true,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var args = await fileWorkspace.ReadAsync(wu.BranchId, "args.txt");
        Assert.NotNull(args);
        Assert.Contains("--resume", args);
        Assert.Contains("stub-session-001", args);
    }

    [Fact]
    public async Task RunAsync_does_not_resume_a_prior_session_on_a_fresh_non_resume_attempt()
    {
        var (executor, _, fileWorkspace, _, wu, _) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-9a", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-9b", wu.WorkUnitId, "task-2",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var args = await fileWorkspace.ReadAsync(wu.BranchId, "args.txt");
        Assert.NotNull(args);
        Assert.DoesNotContain("--resume", args);
    }

    // plans/harness-hosting-architecture.md Phase C.1 (phase-c-implementation.md C1.a/C1.b) —
    // ClaudeTranscriptParser + per-turn ConversationLogEntry recording.
    [Fact]
    public async Task RunAsync_records_one_conversation_log_entry_per_turn_plus_a_terminal_entry_with_tokens()
    {
        var (executor, _, _, _, wu, conversationLog) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-10", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var entries = await conversationLog.GetEntriesAsync(wu.WorkUnitId);

        // Fixture has 2 assistant turns (one with a tool_use + matching tool_result, one text-only)
        // plus 1 terminal run-level entry.
        Assert.Equal(3, entries.Count);

        var turn0 = entries.Single(e => e.CycleNumber == 0);
        Assert.Equal("Reading the file first.", turn0.AssistantText);
        Assert.Equal("claude-sonnet-4-5-20250929", turn0.Model);
        Assert.Equal("anthropic", turn0.Provider);
        Assert.Equal("tool_use", turn0.StopReason);
        Assert.Null(turn0.InputTokens);
        Assert.Null(turn0.OutputTokens);
        var toolCall = Assert.Single(turn0.ToolCalls);
        Assert.Equal("toolu_stub01", toolCall.ToolUseId);
        Assert.Equal("Read", toolCall.Name);
        var toolResult = Assert.Single(turn0.ToolResults);
        Assert.Equal("toolu_stub01", toolResult.ToolUseId);
        Assert.Equal("stub file content", toolResult.Result);

        var turn1 = entries.Single(e => e.CycleNumber == 1 && e.LogId.StartsWith("CLE-turn-", StringComparison.Ordinal));
        Assert.Equal("Working on it.", turn1.AssistantText);
        Assert.Empty(turn1.ToolCalls);
        Assert.Empty(turn1.ToolResults);

        var terminal = entries.Single(e => e.LogId.StartsWith("CLE-", StringComparison.Ordinal) &&
            !e.LogId.StartsWith("CLE-turn-", StringComparison.Ordinal));
        Assert.Equal(2, terminal.CycleNumber);
        Assert.Equal("Stub run complete.", terminal.AssistantText);
        Assert.Equal(42, terminal.InputTokens);
        Assert.Equal(7, terminal.OutputTokens);
        Assert.False(terminal.TokensEstimated);
        Assert.Equal("stub-session-001", terminal.SessionId);

        // Every turn/terminal entry shares the same session id captured off the transcript.
        Assert.All(entries, e => Assert.Equal("stub-session-001", e.SessionId));
    }

    [Fact]
    public async Task RunAsync_does_not_duplicate_conversation_log_entries_across_two_separate_runs()
    {
        var (executor, _, _, _, wu, conversationLog) = await BuildAsync();

        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-11a", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var afterFirst = await conversationLog.GetEntriesAsync(wu.WorkUnitId);
        Assert.Equal(3, afterFirst.Count);

        // A resumed second run reprocesses a fresh copy of the same stub transcript (the stub CLI
        // doesn't vary its output by --resume) — this must add exactly one more set of entries
        // (fresh LogIds), never re-record or duplicate the first run's rows.
        await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-11b", wu.WorkUnitId, "task-2",
            Profile: null, SessionId: null, IsResume: true,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        var afterSecond = await conversationLog.GetEntriesAsync(wu.WorkUnitId);
        Assert.Equal(6, afterSecond.Count);
        Assert.Equal(afterFirst.Select(e => e.LogId).ToHashSet().Count, afterFirst.Count);
        Assert.Equal(afterSecond.Select(e => e.LogId).Distinct().Count(), afterSecond.Count);
    }

    [Fact]
    public async Task RunAsync_degrades_to_a_single_run_level_entry_when_the_transcript_format_is_unrecognized()
    {
        // No `system`/`init` line at all — ClaudeTranscriptParser.V1's format marker never fires,
        // so turn reconstruction stays off even though assistant/user lines are present. The run
        // must still succeed using only the terminal `result` line's scalars (today's pre-C1
        // behavior), never fail because the transcript didn't match the expected shape.
        var jsonl = string.Join('\n',
            """{"type":"assistant","message":{"content":[{"type":"text","text":"Working on it."}]},"session_id":"stub-session-002"}""",
            """{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_unknown","content":"ignored"}]},"session_id":"stub-session-002"}""",
            """{"type":"result","subtype":"success","is_error":false,"result":"Degraded run complete.","session_id":"stub-session-002","total_cost_usd":0.01,"usage":{"input_tokens":10,"output_tokens":2}}""");
        File.WriteAllText(Path.Combine(_stubDir, "unrecognized-output.jsonl"), jsonl);

        var cmd = "@echo off\r\ntype \"%~dp0unrecognized-output.jsonl\"\r\n";
        File.WriteAllText(Path.Combine(_stubDir, "unrecognized-claude.cmd"), cmd);

        var (executor, _, _, _, wu, conversationLog) = await BuildAsync(
            executablePath: Path.Combine(_stubDir, "unrecognized-claude.cmd"));

        var result = await executor.RunAsync(new HarnessRunRequest(
            HarnessMode.Execute, "agent-claude-12", wu.WorkUnitId, "task-1",
            Profile: null, SessionId: null, IsResume: false,
            RuleFileContext: null, PromptGuidanceContext: null,
            SelfVerifyBuild: false, SelfVerifyTest: false, OnActivity: null));

        Assert.Equal(AgentLoopCompletion.Succeeded, result.Completion);
        Assert.Equal("Degraded run complete.", result.Summary);
        Assert.Equal("stub-session-002", result.HarnessSessionId);

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
