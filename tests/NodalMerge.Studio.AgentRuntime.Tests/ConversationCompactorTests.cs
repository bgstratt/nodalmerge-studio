using System.Text.Json;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.AgentRuntime;

namespace NodalMerge.Studio.AgentRuntime.Tests;

public class ConversationCompactorTests
{
    // Captures every log call's level and fully-formatted message so tests can assert on the
    // observability this session added (plans/orchestrator-reliability-and-observability.md's
    // "Add logging to ConversationCompactor" item) without needing a real logging provider.
    private sealed class FakeLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    // Verifies the compaction mechanism itself (trigger math, message reshaping, alternation,
    // no-op safety) without any live provider call — a fake IAgentToolClient stands in for the
    // one extra call the rolling-summary path makes. See
    // plans/orchestrator-reliability-and-observability.md Phase 3 item 2: this is exactly the
    // "mechanism correctness" verification that doesn't need (and can't be substituted for by) a
    // real API call — only summary *quality* and wire-format round-tripping need that.
    private sealed class FakeAgentToolClient : IAgentToolClient
    {
        public string Provider => "anthropic";
        public string Model => "fake-model";
        public string BaseUrl => "https://example.invalid";
        public string ApiKey => "fake-key";

        public int CallCount { get; private set; }
        public IReadOnlyList<NmMessage>? LastMessagesSent { get; private set; }
        public IReadOnlyList<LlmToolDef>? LastToolsSent { get; private set; }
        public Func<IReadOnlyList<NmMessage>, LlmResponse> ResponseFactory { get; set; } =
            _ => new LlmResponse([new NmText("Summary text.")], "end_turn");

        public Task<LlmResponse> SendAsync(
            IReadOnlyList<NmMessage> messages,
            IReadOnlyList<LlmToolDef> tools,
            string systemPrompt,
            CancellationToken ct = default,
            Func<TransientRetryAttempt, Task>? onTransientRetry = null)
        {
            CallCount++;
            LastMessagesSent = messages;
            LastToolsSent = tools;
            return Task.FromResult(ResponseFactory(messages));
        }

        public Task<string> DispatchAsync(
            string toolName, JsonElement input, IReadOnlyList<string>? allowedTools,
            CancellationToken ct, string? sessionId = null) =>
            throw new NotSupportedException("Compaction never dispatches tools.");
    }

    // Builds a kickoff message followed by `cycles` assistant(tool_use)/user(tool_result) pairs,
    // each tool result `resultLength` chars long, so tests can construct histories of a known
    // shape without hand-writing dozens of NmMessage literals.
    private static List<NmMessage> BuildHistory(int cycles, int resultLength = 500)
    {
        var messages = new List<NmMessage> { new("user", [new NmText("Kickoff instructions.")]) };

        for (var i = 0; i < cycles; i++)
        {
            var toolUseId = $"tool-{i}";
            messages.Add(new NmMessage("assistant", [new NmToolUse(toolUseId, "nm_v1_workspace_read", default)]));
            messages.Add(new NmMessage("user", [new NmToolResult(toolUseId, new string('x', resultLength))]));
        }

        return messages;
    }

    [Fact]
    public void ElideStaleToolResults_no_ops_when_history_is_short()
    {
        var messages = BuildHistory(cycles: 3); // 1 + 6 = 7 messages, under the 9-message floor
        var original = messages.Select(m => m).ToList();

        ConversationCompactor.ElideStaleToolResults(messages);

        Assert.Equal(original, messages);
    }

    [Fact]
    public void ElideStaleToolResults_replaces_old_large_tool_results_but_keeps_recent_ones_verbatim()
    {
        var messages = BuildHistory(cycles: 10, resultLength: 500); // 21 messages, well past the floor

        ConversationCompactor.ElideStaleToolResults(messages);

        var toolResults = messages
            .Where(m => m.Role == "user")
            .SelectMany(m => m.Content)
            .OfType<NmToolResult>()
            .ToList();

        var elided = toolResults.Where(r => r.Result.StartsWith("[elided", StringComparison.Ordinal)).ToList();
        var kept = toolResults.Where(r => !r.Result.StartsWith("[elided", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(elided);
        Assert.NotEmpty(kept);
        // The most recent tool result must never be elided — it's inside the always-verbatim tail.
        Assert.False(toolResults[^1].Result.StartsWith("[elided", StringComparison.Ordinal));
        Assert.Contains("nm_v1_workspace_read", elided[0].Result);
    }

    [Fact]
    public void ElideStaleToolResults_leaves_short_tool_results_untouched()
    {
        var messages = BuildHistory(cycles: 10, resultLength: 50); // below the elision char-length floor

        ConversationCompactor.ElideStaleToolResults(messages);

        var toolResults = messages.SelectMany(m => m.Content).OfType<NmToolResult>();
        Assert.All(toolResults, r => Assert.Equal(50, r.Result.Length));
    }

    [Fact]
    public void ElideStaleToolResults_never_touches_assistant_messages()
    {
        var messages = BuildHistory(cycles: 10, resultLength: 500);
        var assistantContentBefore = messages.Where(m => m.Role == "assistant").Select(m => m.Content).ToList();

        ConversationCompactor.ElideStaleToolResults(messages);

        var assistantContentAfter = messages.Where(m => m.Role == "assistant").Select(m => m.Content).ToList();
        Assert.Equal(assistantContentBefore, assistantContentAfter);
    }

    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_does_not_call_the_client_when_under_threshold()
    {
        var messages = BuildHistory(cycles: 5); // 11 messages, under the 20-message trigger
        var client = new FakeAgentToolClient();

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(messages, client, CancellationToken.None);

        Assert.Equal(0, client.CallCount);
        Assert.Equal(11, messages.Count);
    }

    // A single 40k+ token tool result (an AST dump, a large diff) can blow the effective context
    // well before message count crosses 20 — the token trigger exists precisely for this case.
    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_fires_on_provider_reported_tokens_even_under_the_message_count_threshold()
    {
        var messages = BuildHistory(cycles: 5); // 11 messages, well under the 20-message trigger
        var client = new FakeAgentToolClient();

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(
            messages, client, CancellationToken.None, lastInputTokens: 50_000);

        Assert.Equal(1, client.CallCount);
    }

    // When the provider doesn't report usage (lastInputTokens null — e.g. vscode-lm/Copilot),
    // falls back to estimating from raw character counts rather than skipping the token check
    // entirely.
    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_fires_on_estimated_tokens_when_the_provider_reports_none()
    {
        // 5 cycles * 40,000-char results = 200,000 chars =~ 50,000 estimated tokens (> 40k trigger),
        // while message count (11) stays well under the 20-message trigger.
        var messages = BuildHistory(cycles: 5, resultLength: 40_000);
        var client = new FakeAgentToolClient();

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(
            messages, client, CancellationToken.None, lastInputTokens: null);

        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_does_not_fire_when_both_count_and_tokens_are_under_threshold()
    {
        var messages = BuildHistory(cycles: 5); // 11 messages, small results
        var client = new FakeAgentToolClient();

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(
            messages, client, CancellationToken.None, lastInputTokens: 1_000);

        Assert.Equal(0, client.CallCount);
    }

    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_condenses_older_turns_folds_recap_into_kickoff_and_keeps_tail_verbatim()
    {
        var messages = BuildHistory(cycles: 12); // 25 messages, over the 20-message trigger
        var expectedTail = messages.Skip(messages.Count - 6).ToList(); // SummaryTailKeep = 6
        var client = new FakeAgentToolClient
        {
            ResponseFactory = _ => new LlmResponse([new NmText("Recap: read several files, no writes yet.")], "end_turn")
        };

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(messages, client, CancellationToken.None);

        Assert.Equal(1, client.CallCount);
        Assert.Empty(client.LastToolsSent!); // summarization call must be tool-free
        Assert.Equal("user", messages[0].Role); // kickoff's role is preserved, content extended in place
        var kickoffText = Assert.IsType<NmText>(Assert.Single(messages[0].Content));
        Assert.Contains("Kickoff instructions.", kickoffText.Text);
        Assert.Contains("Recap: read several files, no writes yet.", kickoffText.Text);
        Assert.Equal(expectedTail, messages.Skip(1));
        Assert.Equal(1 + expectedTail.Count, messages.Count); // kickoff (recap folded in) + tail, history strictly shrank
    }

    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_leaves_history_untouched_when_the_summary_call_returns_no_text()
    {
        var messages = BuildHistory(cycles: 12);
        var originalCount = messages.Count;
        var client = new FakeAgentToolClient { ResponseFactory = _ => new LlmResponse([], "end_turn") };

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(messages, client, CancellationToken.None);

        Assert.Equal(1, client.CallCount); // it did try
        Assert.Equal(originalCount, messages.Count); // but a useless response must not lose history
    }

    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_inserted_recap_preserves_user_assistant_alternation()
    {
        var messages = BuildHistory(cycles: 12);
        var client = new FakeAgentToolClient();

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(messages, client, CancellationToken.None);

        for (var i = 1; i < messages.Count; i++)
            Assert.NotEqual(messages[i - 1].Role, messages[i].Role);
    }

    [Fact]
    public void ElideStaleToolResults_logs_nothing_when_history_is_short()
    {
        var messages = BuildHistory(cycles: 3); // under the elision floor — no-op path
        var logger = new FakeLogger();

        ConversationCompactor.ElideStaleToolResults(messages, logger, "agent-1", "wu-1");

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void ElideStaleToolResults_logs_the_elided_count_and_chars_when_it_fires()
    {
        var messages = BuildHistory(cycles: 10, resultLength: 500);
        var logger = new FakeLogger();

        ConversationCompactor.ElideStaleToolResults(messages, logger, "agent-42", "wu-99");

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("agent-42", entry.Message);
        Assert.Contains("wu-99", entry.Message);
        Assert.Matches(@"Elided \d+ stale tool result", entry.Message);
    }

    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_logs_condensed_and_kept_counts_when_it_fires()
    {
        var messages = BuildHistory(cycles: 12); // 25 messages, over the trigger
        var logger = new FakeLogger();
        var client = new FakeAgentToolClient
        {
            ResponseFactory = _ => new LlmResponse([new NmText("Recap: read several files, no writes yet.")], "end_turn")
        };

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(
            messages, client, CancellationToken.None, logger, "agent-7", "wu-3");

        // Now two entries: the "trigger fired" line (count/token diagnostics) followed by this
        // "applied" line — assert on the latter, which carries the condensed/kept counts.
        var entry = Assert.Single(logger.Entries, e => e.Message.Contains("condensed 18 turns"));
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("agent-7", entry.Message);
        Assert.Contains("wu-3", entry.Message);
        Assert.Contains("condensed 18 turns", entry.Message);
        Assert.Contains("kept 6 trailing messages", entry.Message);
    }

    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_logs_a_warning_when_the_summary_call_returns_no_text()
    {
        var messages = BuildHistory(cycles: 12);
        var logger = new FakeLogger();
        var client = new FakeAgentToolClient { ResponseFactory = _ => new LlmResponse([], "end_turn") };

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(
            messages, client, CancellationToken.None, logger, "agent-9", "wu-5");

        // Now two entries: the "trigger fired" line, then this warning — assert on the latter.
        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Contains("agent-9", entry.Message);
        Assert.Contains("no usable text", entry.Message);
    }

    [Fact]
    public async Task ApplyRollingSummaryIfDueAsync_logs_nothing_when_under_threshold()
    {
        var messages = BuildHistory(cycles: 5); // under the 20-message trigger
        var logger = new FakeLogger();
        var client = new FakeAgentToolClient();

        await ConversationCompactor.ApplyRollingSummaryIfDueAsync(
            messages, client, CancellationToken.None, logger, "agent-1", "wu-1");

        Assert.Empty(logger.Entries);
    }
}
