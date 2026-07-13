using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Core.Services;
using Xunit;

namespace NodalMerge.Studio.AgentRuntime.Tests;

/// <summary>
/// plans/harness-hosting-architecture.md Phase C.4 (phase-c-implementation.md C3) — the /mcp-harness
/// bearer-token map. Unit-level: mint/resolve/revoke behavior, no HTTP/DI involved (see
/// ClaudeCodeExecutorTests / a live-host integration test for the end-to-end wiring).
/// </summary>
public class HarnessMcpTokenServiceTests
{
    [Fact]
    public void Mint_returns_a_token_that_resolves_to_the_bound_context()
    {
        var svc = new HarnessMcpTokenService();

        var token = svc.Mint("wu-1", "session-1", "agent-1");
        var resolved = svc.Resolve(token);

        Assert.NotNull(resolved);
        Assert.Equal("wu-1", resolved!.WorkUnitId);
        Assert.Equal("session-1", resolved.SessionId);
        Assert.Equal("agent-1", resolved.AgentId);
    }

    [Fact]
    public void Mint_produces_distinct_tokens_across_calls()
    {
        var svc = new HarnessMcpTokenService();

        var a = svc.Mint("wu-1", null, "agent-1");
        var b = svc.Mint("wu-1", null, "agent-1");

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Resolve_returns_null_for_an_unknown_token()
    {
        var svc = new HarnessMcpTokenService();

        Assert.Null(svc.Resolve("not-a-real-token"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_returns_null_for_a_missing_or_empty_token(string? token)
    {
        var svc = new HarnessMcpTokenService();

        Assert.Null(svc.Resolve(token));
    }

    [Fact]
    public void Revoke_makes_a_previously_valid_token_resolve_to_null()
    {
        var svc = new HarnessMcpTokenService();
        var token = svc.Mint("wu-1", null, "agent-1");
        Assert.NotNull(svc.Resolve(token));

        svc.Revoke(token);

        Assert.Null(svc.Resolve(token));
    }

    [Fact]
    public void Revoke_of_an_unknown_token_does_not_throw()
    {
        var svc = new HarnessMcpTokenService();

        var ex = Record.Exception(() => svc.Revoke("never-minted"));

        Assert.Null(ex);
    }
}
