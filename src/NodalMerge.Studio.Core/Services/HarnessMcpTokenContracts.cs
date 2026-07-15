namespace NodalMerge.Studio.Core.Services;

// plans/harness-hosting-architecture.md Phase C.4 (phase-c-implementation.md C3) — work-unit
// identity over stateless HTTP for the harness-scoped MCP mount ("/mcp-harness"). Decided
// 2026-07-12: a per-run bearer token, minted at spawn (crypto-random, not derivable from
// workUnitId), carried in the generated `.mcp.json` via its `headers` support, mapped
// server-side token -> (workUnitId, sessionId, agentId). Same-machine trust model: this is
// access-scoping between cooperating local processes, not a security boundary against a hostile
// local attacker — a Host restart orphans a live run's token (a restart already orphans the run
// itself; a resume respawn mints a fresh one).
public sealed record HarnessMcpTokenContext(string WorkUnitId, string? SessionId, string AgentId);

public interface IHarnessMcpTokenService
{
    /// <summary>
    /// Mints a fresh crypto-random bearer token bound to the given work unit/session/agent and
    /// returns it. Callers store the token nowhere but the generated `.mcp.json` — this service is
    /// the only place the token -> context mapping lives (in-memory only).
    /// </summary>
    string Mint(string workUnitId, string? sessionId, string agentId);

    /// <summary>
    /// Resolves a bearer token to its work-unit context, or null when the token is unknown/expired/
    /// revoked. Callers must reject the request when this returns null, not fall back to any other
    /// identity signal (see McpToolNames.cs's own external/internal split doc comment — this mount
    /// exists precisely because that signal must not be forgeable).
    /// </summary>
    HarnessMcpTokenContext? Resolve(string? token);

    /// <summary>
    /// Revokes a token so it no longer resolves. Called at harvest (the run completed) and on
    /// timeout-kill (the process was force-terminated) — both are the executor's own "this run is
    /// over" moments.
    /// </summary>
    void Revoke(string token);
}
