using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Route B (plans/organizational-knowledge-and-workgroup-scope.md) — a codex-cli Model Profile runs
/// the insight scan through the one-shot CLI completer. Against a stub `codex` CLI (a .cmd echoing a
/// canned `codex exec --json` event stream whose final agent_message text is the findings JSON),
/// never the real binary — same convention as ClaudeCodeExecutorTests / InsightCliScanTests.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class InsightCodexCliScanTests : IAsyncLifetime
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"codex-oneshot-stub-{Guid.NewGuid():N}");

    public InsightCodexCliScanTests()
    {
        Directory.CreateDirectory(_stubDir);

        // `codex exec --json` emits item.completed events; the model's final answer is the last
        // agent_message item's `text` (verified shape, CodexTranscriptParser). Here that text IS the
        // findings JSON the model was asked to emit.
        var jsonl = string.Join('\n',
            """{"type":"thread.started","thread_id":"stub-thread-1"}""",
            """{"type":"item.completed","item":{"type":"agent_message","id":"item-1","text":"{\"findings\":[{\"kind\":\"KnowledgeGuideline\",\"title\":\"Codex rule\",\"summary\":\"Seen often.\"}]}"}}""",
            """{"type":"turn.completed","usage":{"input_tokens":10,"output_tokens":5}}""");
        File.WriteAllText(Path.Combine(_stubDir, "stub-events.jsonl"), jsonl);

        // `more > file` captures the piped stdin (the prompt) so the test can assert it arrived.
        File.WriteAllText(
            Path.Combine(_stubDir, "stub-codex.cmd"),
            "@echo off\r\nmore > \"%~dp0stdin-capture.txt\"\r\ntype \"%~dp0stub-events.jsonl\"\r\n");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_stubDir);

    [Fact]
    public async Task Insight_scan_with_a_codex_cli_provider_runs_via_the_one_shot_completer()
    {
        await using var app = StudioWebApplication.Build(
            [],
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new CodexCliExecutorOptions
                {
                    ExecutablePath = Path.Combine(_stubDir, "stub-codex.cmd"),
                    TimeoutSeconds = 30,
                });
            });

        var analyzer = app.Services.GetRequiredService<IInsightLlmAnalyzerService>();

        var result = await analyzer.AnalyzeAsync(new InsightLlmScanRequest(
            Provider: "codex-cli", Model: "", BaseUrl: "", ApiKey: "", ContextText: "distinctive-context-marker-99"));

        Assert.Single(result.Findings);
        Assert.Equal("Codex rule", result.Findings[0].Title);
        Assert.Equal(FindingKind.KnowledgeGuideline, result.Findings[0].Kind);
        Assert.NotNull(result.RawCliOutput);

        // The full context reached the CLI via stdin (not a truncated cmd.exe arg).
        var capturedStdin = await File.ReadAllTextAsync(Path.Combine(_stubDir, "stdin-capture.txt"));
        Assert.Contains("distinctive-context-marker-99", capturedStdin);
    }
}
