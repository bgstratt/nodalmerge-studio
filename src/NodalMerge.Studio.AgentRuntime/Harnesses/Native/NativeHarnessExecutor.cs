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

    public string DisplayName => "Native (API loop)";

    // Native has no provider key — it's the default when no CLI provider is selected, not itself
    // selected via the provider channel (see IHarnessExecutor.ProviderKey's doc comment).
    public string? ProviderKey => null;

    // Turn telemetry is the native loop's own per-cycle ConversationLogRecorder — the one
    // capability it has always had. Resume is ContinueService's conversation reconstruction, not a
    // harness-side session id, so SupportsResume:false is the honest answer at this seam (see
    // HarnessRunResult.HarnessSessionId's doc comment). No hooks/subagents/MCP machinery of its own
    // beyond McpToolDispatcher, which every executor can reach identically. plans/phase-d-
    // implementation.md D1.a — SupportsPlanningMode is true: PlannerAgentLoop is wired through
    // Mode==Plan below, and native is also the capability-miss fallback target for a CLI executor
    // that hasn't wired planning mode yet, so it must actually be able to run Plan mode itself.
    // SupportsReviewMode (plans/review-seam-and-clarification-sessions.md S2) is true for the same
    // reason SupportsPlanningMode is: native is the capability-miss fallback target, so it must
    // actually run every mode itself (ReviewerAgentLoop wired through Mode==Review below).
    public HarnessCapabilities Capabilities { get; } = new(
        SupportsTurnTelemetry: true, SupportsResume: false, SupportsHooks: false,
        SupportsSubagents: false, SupportsMcp: false, SupportsPlanningMode: true,
        SupportsReviewMode: true);

    public async Task<HarnessRunResult> RunAsync(HarnessRunRequest request, CancellationToken ct = default)
    {
        var dispatcher = serviceProvider.GetRequiredService<McpToolDispatcher>();
        var llm = serviceProvider.GetRequiredService<LlmClient>();
        var agentClient = new DefaultAgentToolClient(
            request.Provider ?? "anthropic", request.Model ?? string.Empty, request.BaseUrl ?? string.Empty,
            request.ApiKey ?? string.Empty, llm, dispatcher, request.LenientToolParsing);
        var conversationLog = serviceProvider.GetRequiredService<IConversationLogService>();
        var events = serviceProvider.GetService<IExecutionEventStream>();

        // plans/phase-d-implementation.md D1.a — moved from InMemoryAgentRuntimeService's own
        // Plan-stage branch (which now just resolves an executor and builds this request, same as
        // the Worker branch always did). The caller already built the combined constraints/
        // prompt-guidance/engineering-state context and threaded it through as
        // PromptGuidanceContext — identical content PlannerAgentLoop's own constraintsContext
        // parameter received before this moved. needsExecuteFallback (a Succeeded plan run with no
        // recorded Plan artifact hands off to Execute) stays the caller's job — that's scheduler
        // behavior, not executor behavior.
        if (request.Mode == HarnessMode.Plan)
        {
            var plannerLoop = new PlannerAgentLoop(
                request.AgentId, request.WorkUnitId, agentClient,
                request.Profile, request.SessionId, request.OnActivity,
                request.RuleFileContext, request.PromptGuidanceContext,
                conversationLog: conversationLog, events: events, logger: logger,
                goal: request.Goal);
            var planCompletion = await plannerLoop.RunAsync(ct).ConfigureAwait(false);
            return new HarnessRunResult(planCompletion);
        }

        // plans/review-seam-and-clarification-sessions.md S2 — moved from the two Reviewer
        // construction sites (InMemoryAgentRuntimeService's scheduled Review branch and
        // InlineReviewerService), which now resolve an executor and build this request instead.
        // TaskId carries the proposalId — the same convention both sites always used. The proposal
        // fetch feeds ReviewerAgentLoop the filesTouched/justification context up front, exactly
        // what InlineReviewerService used to do at its own call site.
        if (request.Mode == HarnessMode.Review)
        {
            var merge = serviceProvider.GetRequiredService<IMergeService>();
            var proposalForReview = string.IsNullOrWhiteSpace(request.TaskId)
                ? null
                : await merge.GetAsync(request.TaskId, ct).ConfigureAwait(false);

            var reviewerLoop = new ReviewerAgentLoop(
                request.AgentId, request.WorkUnitId, request.TaskId, agentClient,
                request.Profile, request.SessionId, request.OnActivity,
                filesTouched: proposalForReview?.FilesTouched,
                noFileChangesJustification: proposalForReview?.NoFileChangesJustification,
                conversationLog: conversationLog, events: events, logger: logger,
                // S6 — the review branch previously dropped PromptGuidanceContext; carry it through
                // so an injected peer contract reaches the reviewer.
                promptGuidanceContext: request.PromptGuidanceContext);
            var reviewCompletion = await reviewerLoop.RunAsync(ct).ConfigureAwait(false);
            return new HarnessRunResult(reviewCompletion);
        }

        var loop = new WorkerAgentLoop(
            request.AgentId, request.WorkUnitId, request.TaskId, agentClient, request.Profile,
            request.SessionId, request.OnActivity, request.IsResume, request.RuleFileContext,
            request.SelfVerifyBuild, request.SelfVerifyTest, request.PromptGuidanceContext,
            conversationLog: conversationLog, events: events, logger: logger,
            goal: request.Goal);

        var completion = await loop.RunAsync(ct).ConfigureAwait(false);
        return new HarnessRunResult(completion);
    }
}

// Resolves AgentProfile.Executor to the matching registered IHarnessExecutor, falling back to
// "native" for null/unrecognized values — never throws, since falling back is always a safe
// degrade (see IHarnessExecutorResolver's doc comment). Post-C refactor: also owns provider-key
// resolution, derived from each registered executor's own ProviderKey rather than a separate
// static HarnessProviders map — this is what makes a third adapter "one new folder + one DI line".
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

    public bool IsCliProvider(string? provider) =>
        provider is not null && executors.Any(
            e => e.ProviderKey is not null && string.Equals(e.ProviderKey, provider, StringComparison.OrdinalIgnoreCase));

    public IHarnessExecutor ResolveForProvider(string? provider, string? profileExecutor)
    {
        if (provider is not null)
        {
            var match = executors.FirstOrDefault(
                e => e.ProviderKey is not null && string.Equals(e.ProviderKey, provider, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
                return match;
        }

        return Resolve(profileExecutor);
    }
}
