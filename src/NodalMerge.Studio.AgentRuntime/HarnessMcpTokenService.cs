using System.Collections.Concurrent;
using System.Security.Cryptography;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase C.4 (phase-c-implementation.md C3) — in-memory-only
// token map, DI singleton (mirrors McpToolDispatcher's own singleton-scoped caches). A Host
// restart orphans every live token, which is fine: it also orphans every live run, and a resume
// respawn mints a fresh token via ClaudeCodeExecutor.
public sealed class HarnessMcpTokenService : IHarnessMcpTokenService
{
    private readonly ConcurrentDictionary<string, HarnessMcpTokenContext> _tokens = new(StringComparer.Ordinal);

    public string Mint(string workUnitId, string? sessionId, string agentId)
    {
        // 32 bytes of CSPRNG output, base64url-encoded (no padding) so it's safe to embed directly
        // in a JSON header value and a CLI arg without further escaping.
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        _tokens[token] = new HarnessMcpTokenContext(workUnitId, sessionId, agentId);
        return token;
    }

    public HarnessMcpTokenContext? Resolve(string? token) =>
        !string.IsNullOrEmpty(token) && _tokens.TryGetValue(token, out var ctx) ? ctx : null;

    public void Revoke(string token) => _tokens.TryRemove(token, out _);
}
