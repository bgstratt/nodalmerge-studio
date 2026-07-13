using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime.Tests;

// plans/harness-hosting-architecture.md Phase B.1 — locks in HarnessExecutorResolver's
// fallback-to-native behavior, which the seam's whole "unrecognized executor degrades gracefully"
// design principle depends on.
public class HarnessExecutorResolverTests
{
    private sealed class FakeExecutor(string name) : IHarnessExecutor
    {
        public string Name => name;
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
}
