using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime.Tests;

// plans/harness-hosting-architecture.md Phase B.1 — locks in HarnessExecutorResolver's
// fallback-to-native behavior, which the seam's whole "unrecognized executor degrades gracefully"
// design principle depends on.
public class HarnessExecutorResolverTests
{
    private sealed class FakeExecutor(string name, string? providerKey = null) : IHarnessExecutor
    {
        public string Name => name;
        public string DisplayName => name;
        public string? ProviderKey => providerKey;
        public HarnessCapabilities Capabilities => new(false, false, false, false, false, false);
        public Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default) =>
            Task.FromResult(new HarnessRunResult(AgentLoopCompletion.Succeeded));
    }

    [Fact]
    public void Resolve_with_null_executor_name_returns_native()
    {
        var native = new FakeExecutor("native");
        var resolver = new HarnessExecutorResolver([native, new FakeExecutor("claude-code")]);

        Assert.Same(native, resolver.Resolve(null));
    }

    [Fact]
    public void Resolve_with_unrecognized_executor_name_falls_back_to_native()
    {
        var native = new FakeExecutor("native");
        var resolver = new HarnessExecutorResolver([native, new FakeExecutor("claude-code")]);

        Assert.Same(native, resolver.Resolve("some-future-harness-nobody-registered"));
    }

    [Fact]
    public void Resolve_with_a_registered_executor_name_returns_that_executor_not_native()
    {
        var native = new FakeExecutor("native");
        var claudeCode = new FakeExecutor("claude-code");
        var resolver = new HarnessExecutorResolver([native, claudeCode]);

        Assert.Same(claudeCode, resolver.Resolve("claude-code"));
    }

    [Fact]
    public void Resolve_is_case_insensitive()
    {
        var native = new FakeExecutor("native");
        var claudeCode = new FakeExecutor("claude-code");
        var resolver = new HarnessExecutorResolver([native, claudeCode]);

        Assert.Same(claudeCode, resolver.Resolve("Claude-Code"));
    }

    // Post-C refactor (plans/phase-c-implementation.md) — provider-key resolution used to live in
    // a static HarnessProviders map; it's now derived from each registered executor's own
    // ProviderKey. These tests lock in the same behavior the old map had.
    [Fact]
    public void IsCliProvider_true_for_a_registered_executors_provider_key_case_insensitive()
    {
        var native = new FakeExecutor("native");
        var claudeCode = new FakeExecutor("claude-code", "claude-cli");
        var resolver = new HarnessExecutorResolver([native, claudeCode]);

        Assert.True(resolver.IsCliProvider("Claude-CLI"));
    }

    [Fact]
    public void IsCliProvider_false_for_an_api_provider_and_for_null()
    {
        var native = new FakeExecutor("native");
        var claudeCode = new FakeExecutor("claude-code", "claude-cli");
        var resolver = new HarnessExecutorResolver([native, claudeCode]);

        Assert.False(resolver.IsCliProvider("anthropic"));
        Assert.False(resolver.IsCliProvider(null));
    }

    [Fact]
    public void ResolveForProvider_prefers_the_provider_over_profileExecutor_for_a_cli_provider()
    {
        var native = new FakeExecutor("native");
        var claudeCode = new FakeExecutor("claude-code", "claude-cli");
        var resolver = new HarnessExecutorResolver([native, claudeCode]);

        // A stale/mismatched profileExecutor must not win over a CLI provider.
        Assert.Same(claudeCode, resolver.ResolveForProvider("claude-cli", "native"));
    }

    [Fact]
    public void ResolveForProvider_falls_back_to_profileExecutor_when_provider_is_not_a_cli_provider()
    {
        var native = new FakeExecutor("native");
        var claudeCode = new FakeExecutor("claude-code", "claude-cli");
        var resolver = new HarnessExecutorResolver([native, claudeCode]);

        Assert.Same(claudeCode, resolver.ResolveForProvider("anthropic", "claude-code"));
    }
}
