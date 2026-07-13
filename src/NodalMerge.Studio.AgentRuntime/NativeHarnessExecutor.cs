using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// plans/harness-hosting-architecture.md Phase B.1 — the current native loop, wrapped behind
// IHarnessExecutor. Resolves its own collaborators from IServiceProvider (same
// _serviceProvider.GetRequiredService<T>() pattern InMemoryAgentRuntimeService already uses
// throughout) rather than taking them as constructor dependencies, so this executor can be
// constructed once at DI-registration time yet still see whatever's registered for
// McpToolDispatcher/LlmClient/IConversationLogService at request time.
internal sealed class NativeHarnessExecutor(
    IServiceProvider serviceProvider, ILogger<NativeHarnessExecutor> logger) : IHarnessExecutor
{
    public string Name => "native";

    // Turn telemetry is the native loop's own per-cycle ConversationLogRecorder — the one
    // capability it has always had. Resume is ContinueService's conversation reconstruction, not a
    // harness-side session id, so SupportsResume:false is the honest answer at this seam (see
    // HarnessRunResult.HarnessSessionId's doc comment). No hooks/subagents/MCP/planning-mode
    // machinery of its own beyond McpToolDispatcher, which every executor can reach identically.
    public HarnessCapabilities Capabilities { get; } = new(
        SupportsTurnTelemetry: true, SupportsResume: false, SupportsHooks: false,
        SupportsSubagents: false, SupportsMcp: false, SupportsPlanningMode: false);

    public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default)
    {
        var dispatcher = serviceProvider.GetRequiredService<McpToolDispatcher>();
        var llm = serviceProvider.GetRequiredService<LlmClient>();
        var agentClient = new DefaultAgentToolClient(
            request.Provider ?? "anthropic", request.Model ?? string.Empty, request.BaseUrl ?? string.Empty,
            request.ApiKey ?? string.Empty, llm, dispatcher);
        var conversationLog = serviceProvider.GetRequiredService<IConversationLogService>();
        var events = serviceProvider.GetService<IExecutionEventStream>();

        var loop = new WorkerAgentLoop(
            request.AgentId, request.WorkUnitId, request.TaskId, agentClient, request.Profile,
            request.SessionId, request.OnActivity, request.IsResume, request.RuleFileContext,
            request.SelfVerifyBuild, request.SelfVerifyTest, request.PromptGuidanceContext,
            conversationLog: conversationLog, events: events, logger: logger);

        var completion = await loop.RunAsync(ct).ConfigureAwait(false);
        return new HarnessRunResult(completion);
    }
}

// Resolves AgentProfile.Executor to the matching registered IHarnessExecutor, falling back to
// "native" for null/unrecognized values — never throws, since falling back is always a safe
// degrade (see IHarnessExecutorResolver's doc comment).
internal sealed class HarnessExecutorResolver(IEnumerable<IHarnessExecutor> executors) : IHarnessExecutorResolver
{
    public IHarnessExecutor Resolve(string? executorName)
    {
        if (executorName is not null)
        {
            var match = executors.FirstOrDefault(
                e => string.Equals(e.Name, executorName, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return executors.First(e => e.Name == "native");
    }
}
