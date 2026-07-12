namespace NodalMerge.Studio.Contracts.Domain;

public enum PipelineStage
{
    Orchestrate,
    Plan,
    Execute,
    Review,
    Merge,
    Reconcile,
}

// Slice 14c — glob patterns (e.g. "src/**/*.tsx") declaring this profile's file-scope specialty.
// Empty list = no declared specialty, same as today: routing falls through to
// IProfileSelectionService (heuristic default or LLM, depending on UseLlmProfileSelection).
public sealed record AgentProfile(
    string AgentProfileId,
    string Name,
    PipelineStage Stage,
    string SystemPrompt,
    IReadOnlyList<string> AllowedTools,
    int MaxIterations,
    IReadOnlyList<string> FileScopePatterns,
    // plans/harness-hosting-architecture.md Phase B.1 — which IHarnessExecutor runs this
    // profile's Worker-stage spawns ("native" | "claude-code" | ...). Null (the default for
    // every profile that predates this field) resolves to the native executor — see
    // IHarnessExecutorResolver.
    string? Executor = null,
    // Phase B.2 — resolved decision "ambient auth, key opt-in": ClaudeCodeExecutor defaults to
    // the machine's existing CLI auth and holds no secret. Set true only for headless/CI profiles
    // that need ClaudeCodeExecutor to inject the caller-supplied credential (HarnessRunRequest
    // .ApiKey) as ANTHROPIC_API_KEY on the spawned process's environment instead.
    bool InjectApiKeyEnv = false);
