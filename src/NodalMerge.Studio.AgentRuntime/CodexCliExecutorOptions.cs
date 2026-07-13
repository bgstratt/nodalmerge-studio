namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.3 (phase-c-implementation.md C2) — DI-singleton
// options, mirroring ClaudeCodeExecutorOptions' convention. Tests override ExecutablePath to a stub
// CLI — the real `codex` binary must never run in automated tests (same posture as claude-code).
public sealed class CodexCliExecutorOptions
{
    public string ExecutablePath { get; set; } = "codex";

    public int TimeoutSeconds { get; set; } = 600;

    // Verified on this machine (2026-07-12, codex-probe capture-2/capture-4 against codex-cli
    // 0.144.1): `-s workspace-write` behaved read-only on Windows in two separate captures — one
    // with a relative -C, one with an absolute -C — every file-writing attempt was refused with the
    // CLI reporting "this workspace is currently read-only". `-s danger-full-access` worked in the
    // same session (capture-3/capture-5-resume). Studio does not treat the harness's own sandbox as
    // its isolation boundary anyway — same "the gate is the correctness mechanism" posture as
    // advisory leases in the parent plan (harness-hosting-architecture.md): the branch workdir +
    // the harvest build/test gate (WorkspaceExecutionRule) + AP-4 human review are what actually
    // keep a run's changes safe, not the CLI's own sandbox flag. Default is platform-conditional so
    // a fixed Windows sandbox (or a non-Windows host, where this finding was never reproduced) can
    // tighten it without an adapter code change — just override this option.
    public string SandboxMode { get; set; } = OperatingSystem.IsWindows() ? "danger-full-access" : "workspace-write";
}
