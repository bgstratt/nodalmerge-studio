using NodalMerge.Studio.Contracts.Domain;

namespace NodalMerge.Studio.Core.Services;

// plans/harness-hosting-architecture.md Phase B.1 — the executor seam. IAgentToolClient (see
// NodalMerge.Studio.AgentRuntime) abstracts one LLM round-trip + one tool dispatch, called in a
// loop from inside WorkerAgentLoop; this seam sits one level up and abstracts the entire
// "construct a loop, run it, return a completion" call — the loop classes themselves are the
// native executor's private implementation detail, never exposed across this interface.
//
// Scope (B1): only the two Worker-role construction sites in InMemoryAgentRuntimeService go
// behind this seam. Planner/Reviewer/Orchestrator stay native-only, and ContinueService's
// conversation-reconstruction resume path stays native-only too (an external harness resumes via
// its own session id, not via replayed ConversationLogEntry turns) — see the plan's B1 section for
// the full reasoning.
public enum HarnessMode
{
    // Plan/Review are known future values (Phase D folds a Plan mode; PlannerAgentLoop/
    // ReviewerAgentLoop already exist natively) — not speculative, just not built into the seam
    // yet. A mode enum grows without breaking existing adapters; a multi-method interface would
    // force every adapter to answer questions it can't yet.
    Execute,
}

public sealed record HarnessRunRequest(
    HarnessMode Mode,
    string AgentId,
    string WorkUnitId,
    string TaskId,
    AgentProfile? Profile,
    string? SessionId,
    bool IsResume,
    string? RuleFileContext,
    string? PromptGuidanceContext,
    bool SelfVerifyBuild,
    bool SelfVerifyTest,
    Action<string?>? OnActivity,
    // Native-only — ClaudeCodeExecutor (Phase B.2) ignores these entirely; it uses the machine's
    // ambient CLI auth by default (Studio holds no secret for it), per the plan's resolved
    // "ambient auth, key opt-in" decision. AgentProfile carries no Provider/Model/BaseUrl/ApiKey
    // of its own (confirmed 2026-07-12) — those live on the caller's per-spawn parameters, so
    // they're threaded through the request rather than resolved from Profile.
    string? Provider = null,
    string? Model = null,
    string? BaseUrl = null,
    string? ApiKey = null);

public sealed record HarnessRunResult(
    AgentLoopCompletion Completion,
    string? FailureReason = null,
    // plans/harness-hosting-architecture.md Phase B.2/B.3 — populated by executors that don't
    // produce per-turn ConversationLogEntry rows themselves the way the native loop does (via
    // WorkerAgentLoop's own turn-by-turn recording). ClaudeCodeExecutor is the first producer:
    // this is the only place the run's stream-json output is ever seen, so it's captured here
    // rather than discarded — B3's harvest step records one ConversationLogEntry per external run
    // from these fields. Native leaves them all null.
    string? Summary = null,
    int? InputTokens = null,
    int? OutputTokens = null,
    double? CostUsd = null,
    // The harness's own session identity (e.g. Claude Code's session_id), for a future --resume
    // respawn. Native leaves this null — its resume story is ContinueService's conversation
    // reconstruction, not a harness-side session id.
    string? HarnessSessionId = null);

public interface IHarnessExecutor
{
    // "native", "claude-code", ... — matches AgentProfile.Executor's string values so an
    // unrecognized/future executor name degrades to a resolver lookup miss, not a deserialization
    // failure (same additive-forward-compat reasoning as HarnessMode being a growable enum).
    string Name { get; }

    Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default);
}

// Resolves AgentProfile.Executor (null on every profile that predates this field, or an
// unrecognized value from a newer/older client) to the "native" executor — never throws on an
// unknown name, since falling back to native is always a safe degrade.
public interface IHarnessExecutorResolver
{
    IHarnessExecutor Resolve(string? executorName);
}
