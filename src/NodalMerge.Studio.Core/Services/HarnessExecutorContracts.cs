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

// plans/harness-hosting-architecture.md Phase C.1 (phase-c-implementation.md C1.c) — declares what
// an adapter's own machinery actually supports, so callers (the extension's future executor
// dropdown, Phase D's plan-mode gating) can branch on real capability rather than string-matching
// Name. Each flag is honest about the *adapter*, not the underlying vendor CLI's theoretical
// ceiling — e.g. ClaudeCodeExecutor.SupportsPlanningMode is false because Studio has not wired a
// Plan mode through this adapter yet (Phase D), even though the `claude` binary itself has one.
public sealed record HarnessCapabilities(
    bool SupportsTurnTelemetry,
    bool SupportsResume,
    bool SupportsHooks,
    bool SupportsSubagents,
    bool SupportsMcp,
    bool SupportsPlanningMode);

public interface IHarnessExecutor
{
    // "native", "claude-code", ... — matches AgentProfile.Executor's string values so an
    // unrecognized/future executor name degrades to a resolver lookup miss, not a deserialization
    // failure (same additive-forward-compat reasoning as HarnessMode being a growable enum).
    string Name { get; }

    // Post-C refactor (plans/phase-c-implementation.md) — folds what used to be an out-of-slice
    // switch (StudioRestEndpoints' /studio/executors displayName) and a static provider map
    // (HarnessProviders) into the executor itself, so a third adapter is "one new folder + one DI
    // line" with nothing else to touch server-side.
    string DisplayName { get; }

    // The user-facing selection model is provider-driven: the extension's Model Profiles carry an
    // LLM provider ("anthropic", "openai", "vscode-lm", "claude-cli"), assigned per pipeline role
    // by the Agent Topology tab, and that provider travels the existing per-stage credential
    // channel all the way to the worker construction sites. A non-null ProviderKey means this
    // executor is selected via that provider channel rather than AgentProfile.Executor alone (see
    // IHarnessExecutorResolver.ResolveForProvider) — e.g. "claude-cli" is not an HTTP API, it
    // selects ClaudeCodeExecutor (spawn the local `claude` binary) rather than a base URL + key.
    // Native has no provider key: it's the default when no CLI provider is selected, not itself
    // selected via the provider channel.
    string? ProviderKey { get; }

    HarnessCapabilities Capabilities { get; }

    Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default);
}

// Resolves AgentProfile.Executor (null on every profile that predates this field, or an
// unrecognized value from a newer/older client) to the "native" executor — never throws on an
// unknown name, since falling back to native is always a safe degrade. Also owns provider-key
// resolution (Post-C refactor) since this is the one seam that already sees every registered
// IHarnessExecutor via DI — no separate static provider map needed.
public interface IHarnessExecutorResolver
{
    IHarnessExecutor Resolve(string? executorName);

    // True when `provider` names a registered executor's ProviderKey (case-insensitive) — i.e. a
    // CLI harness provider, not an HTTP API provider like "anthropic"/"openai".
    bool IsCliProvider(string? provider);

    // Provider wins over AgentProfile.Executor when it names a CLI harness's ProviderKey: a
    // profile with Executor unset (every profile predating the field) must still route to the CLI
    // adapter when the role's Model Profile says so, and a stale Executor value must not silently
    // send CLI credentials into the native LlmClient path. Falls back to Resolve(profileExecutor)
    // when provider doesn't match any registered executor's ProviderKey.
    IHarnessExecutor ResolveForProvider(string? provider, string? profileExecutor);
}
