namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase B.2 — DI-singleton options, mirroring
// WorkspaceOptions' convention elsewhere in this codebase. Tests override ExecutablePath to a
// stub CLI (never the real `claude` — a single trivial "pong" reply during Phase B research cost
// $0.044 in a real invocation; automated tests must never shell out to the real binary).
public sealed class ClaudeCodeExecutorOptions
{
    public string ExecutablePath { get; set; } = "claude";

    public int TimeoutSeconds { get; set; } = 600;
}
