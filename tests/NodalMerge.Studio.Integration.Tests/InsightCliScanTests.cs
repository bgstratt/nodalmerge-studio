using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Route B (plans/organizational-knowledge-and-workgroup-scope.md) — a claude-cli Model Profile can
/// run the insight LLM scan through the one-shot CLI completer (no HTTP baseUrl). Against a stub
/// `claude` CLI (a .cmd that echoes a canned --output-format json envelope), never the real binary —
/// same convention as ClaudeCodeExecutorTests.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Requires", "LocalCliProcess")]
public class InsightCliScanTests : IAsyncLifetime
{
    private readonly string _stubDir = Path.Combine(Path.GetTempPath(), $"claude-oneshot-stub-{Guid.NewGuid():N}");

    public InsightCliScanTests()
    {
        Directory.CreateDirectory(_stubDir);

        // `claude -p ... --output-format json` returns {"type":"result","result":"<final text>",...}.
        // Here .result IS the findings JSON the model was asked to emit (a JSON string containing the
        // object), exactly the shape the one-shot completer unwraps then the analyzer parses.
        var envelope =
            "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false," +
            "\"result\":\"{\\\"findings\\\":[{\\\"kind\\\":\\\"KnowledgeGuideline\\\"," +
            "\\\"title\\\":\\\"Prefer repository abstraction\\\"," +
            "\\\"summary\\\":\\\"Seen in 8 of 10 runs.\\\"}]}\"," +
            "\"session_id\":\"stub-oneshot-1\"}";
        File.WriteAllText(Path.Combine(_stubDir, "stub-oneshot.json"), envelope);

        // %~dp0 = the .cmd's own directory (not the process workdir, which is a throwaway temp dir),
        // the same self-locating technique ClaudeCodeExecutorTests' stub uses. `more > file` captures
        // the piped stdin (the prompt) so the test can assert it arrived — the whole point of the
        // stdin fix (a multi-line prompt passed as a cmd.exe arg was truncated at its first newline).
        File.WriteAllText(
            Path.Combine(_stubDir, "stub-claude.cmd"),
            "@echo off\r\nmore > \"%~dp0stdin-capture.txt\"\r\ntype \"%~dp0stub-oneshot.json\"\r\n");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 batch 2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry, via
    // the shared helper. No ClearAllPools -- this class does not open a file SQLite db, so it must
    // not disturb the SQLite tests running in parallel.
    public Task DisposeAsync() => TestTeardown.DeleteDirectoriesAsync(_stubDir);

    [Fact]
    public async Task Insight_scan_with_a_claude_cli_provider_runs_via_the_one_shot_completer()
    {
        await using var app = StudioWebApplication.Build(
            [],
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton(new ClaudeCodeExecutorOptions
                {
                    ExecutablePath = Path.Combine(_stubDir, "stub-claude.cmd"),
                    TimeoutSeconds = 30,
                });
            });

        var analyzer = app.Services.GetRequiredService<IInsightLlmAnalyzerService>();

        // A claude-cli provider with no baseUrl — the path that used to 400 — now routes to the CLI
        // completer, spawns the stub, and parses its findings JSON.
        var result = await analyzer.AnalyzeAsync(new InsightLlmScanRequest(
            Provider: "claude-cli", Model: "", BaseUrl: "", ApiKey: "", ContextText: "distinctive-context-marker-42"));

        Assert.Single(result.Findings);
        Assert.Equal("Prefer repository abstraction", result.Findings[0].Title);
        Assert.Equal(FindingKind.KnowledgeGuideline, result.Findings[0].Kind);
        Assert.NotNull(result.RawCliOutput); // the verbatim model response is carried back for the UI

        // The full context reached the CLI via stdin (not a truncated cmd.exe arg).
        var capturedStdin = await File.ReadAllTextAsync(Path.Combine(_stubDir, "stdin-capture.txt"));
        Assert.Contains("distinctive-context-marker-42", capturedStdin);
    }
}
