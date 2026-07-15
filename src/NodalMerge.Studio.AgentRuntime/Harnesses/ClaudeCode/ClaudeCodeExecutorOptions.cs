namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase B.2 — DI-singleton options, mirroring
// WorkspaceOptions' convention elsewhere in this codebase. Tests override ExecutablePath to a
// stub CLI (never the real `claude` — a single trivial "pong" reply during Phase B research cost
// $0.044 in a real invocation; automated tests must never shell out to the real binary).
public sealed class ClaudeCodeExecutorOptions
{
    public string ExecutablePath { get; set; } = "claude";

    public int TimeoutSeconds { get; set; } = 600;

    // Phase C.4 (phase-c-implementation.md C3) — the Studio Host's own listening address, needed
    // so the generated `.workspace/mcp.json` can point the spawned CLI back at this same process's
    // "/mcp-harness" mount. Set once, after the Kestrel server actually starts listening (see
    // StudioWebApplication.Build's IServerAddressesFeature hook) — a config-time guess would be
    // wrong whenever the port is dynamically assigned (":0"). Null until that hook fires (or in
    // BuildPeer's headless/no-HTTP mode, where it never fires) — RunAsync treats null as "no MCP
    // mount available this run" and skips mcp.json generation even when Capabilities.SupportsMcp.
    public string? HarnessMcpBaseUrl { get; set; }
}
