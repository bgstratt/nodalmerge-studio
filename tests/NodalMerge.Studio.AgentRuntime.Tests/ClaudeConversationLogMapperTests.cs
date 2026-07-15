using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime.Tests;

/// <summary>
/// Regression (found live 2026-07-13): every claude-cli run recorded AgentRole "worker"
/// unconditionally, including HarnessMode.Review runs — the reviewer's own transcript turns were
/// indistinguishable from the task worker's turns on the same work unit's Conversation tab. The
/// native loops (ReviewerAgentLoop/PlannerAgentLoop via ConversationLogRecorder) always passed
/// their own role string; the CLI mapper never did.
/// </summary>
public class ClaudeConversationLogMapperTests
{
    private static TranscriptRunSummary Summary() => new(
        ResultText: "done", Subtype: "success", IsError: false,
        InputTokens: 10, OutputTokens: 5, TotalCostUsd: 0.01, SessionId: "sess-1",
        PermissionDenials: [], Turns: [new ClaudeTranscriptTurn(0, "hi", [], [], "claude-3", "end_turn")]);

    [Fact]
    public void Execute_mode_defaults_to_worker_role()
    {
        var entries = ClaudeConversationLogMapper.BuildEntries(
            Summary(), "wu-1", "worker-1", "task-1", "claude-code", HarnessMode.Execute);

        Assert.All(entries, e => Assert.Equal("worker", e.AgentRole));
    }

    [Fact]
    public void Review_mode_records_reviewer_role()
    {
        var entries = ClaudeConversationLogMapper.BuildEntries(
            Summary(), "wu-1", "reviewer-auto-1", "MP-1", "claude-code", HarnessMode.Review);

        Assert.All(entries, e => Assert.Equal("reviewer", e.AgentRole));
    }

    [Fact]
    public void Plan_mode_records_planner_role()
    {
        var entries = ClaudeConversationLogMapper.BuildEntries(
            Summary(), "wu-1", "planner-1", null, "claude-code", HarnessMode.Plan);

        Assert.All(entries, e => Assert.Equal("planner", e.AgentRole));
    }
}
