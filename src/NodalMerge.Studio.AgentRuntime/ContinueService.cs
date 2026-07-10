using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

// Phase 1.4 Continue-track (see plans/orchestrator-reliability-and-observability.md): resumes a
// dead-lettered work unit's own prior attempt with a fresh iteration budget, using
// ConversationLogEntry to reconstruct exactly what it was doing rather than starting over. Was
// blocked on Phase 3 (context compaction) landing first — reconstructing full prior history makes
// the next call's context strictly larger than the run that already hit the cost problem, and
// WorkerAgentLoop's own ConversationCompactor (elision + rolling summary) is what keeps that
// bounded instead of compounding it. Only meaningful for FailureKind.MaxIterationsExceeded —
// nothing about hitting the ceiling implies the approach was wrong, only that the budget was too
// small, unlike Retry-track's Exception/Stalled/etc. cases where resuming the same conversation
// wouldn't make sense.
public sealed class ContinueService(
    IDeadLetterService deadLetter,
    IWorkUnitService workUnits,
    IAgentControlService agentControl,
    IServiceProvider serviceProvider,
    IRuntimeCredentialCache? credentialCache = null) : IContinueService
{
    public async Task<ContinueResult> ContinueWithPriorContextAsync(
        string entryId,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideCredentialRef = null,
        CancellationToken cancellationToken = default)
    {
        var entry = await deadLetter.GetAsync(entryId, cancellationToken).ConfigureAwait(false);
        if (entry is null)
            return new ContinueResult(ContinueOutcome.NotFound, "Dead-letter entry not found.");

        if (entry.Kind != FailureKind.MaxIterationsExceeded)
        {
            return new ContinueResult(
                ContinueOutcome.NotApplicable,
                $"Continue only applies to MaxIterationsExceeded failures (this entry's kind: " +
                $"{entry.Kind}) — use Retry (steer) or Re-plan instead.");
        }

        var workUnit = await workUnits.GetAsync(entry.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (workUnit is null)
            return new ContinueResult(ContinueOutcome.NotFound, "Work unit not found.");

        // Resolved in the same tiered order as InMemoryDeadLetterService.ResolveRetryCredentials:
        // an explicit override (a caller resupplying live credentials, e.g. after a Host restart)
        // takes top priority; then the dead-lettered run's own captured credentials (this resumes
        // the exact same run, not a fresh spawn); then IRuntimeCredentialCache via the entry's own
        // CredentialRef, which is the only tier that can still be warm post-restart; then the live
        // orchestrator registry, for legacy entries recorded before credential capture existed.
        // Each tier is checked via "apiKey is null", not IsNullOrWhiteSpace — a vscode-lm profile
        // legitimately captures apiKey as "" (no real secret, see LlmApiProxy), which must count as
        // present; only null (never captured, or wiped by DeadLetterEntry.ApiKey's [JsonIgnore]
        // surviving a restart) means "keep looking." A baseUrl-only check would make the same
        // mistake in the other direction — Model/BaseUrl/Provider are still persisted plaintext, so
        // after a restart wipes just the key, baseUrl alone looks present and the resolution would
        // stop too early, proceeding into a real LLM call with an empty ApiKey.
        string? model = overrideModel, baseUrl = overrideBaseUrl, apiKey = overrideApiKey, provider = overrideProvider;
        if (apiKey is null)
        {
            model = entry.Model;
            baseUrl = entry.BaseUrl;
            apiKey = entry.ApiKey;
            provider = entry.Provider;
        }
        if (apiKey is null)
        {
            var cached = credentialCache?.TryGet(overrideCredentialRef ?? entry.CredentialRef);
            if (cached is not null)
            {
                model = cached.Model;
                baseUrl = cached.BaseUrl;
                apiKey = cached.ApiKey;
                provider = cached.Provider;
            }
        }
        if (apiKey is null)
        {
            var creds = agentControl.GetCredentialsForStage(entry.WorkUnitId, entry.Stage)
                ?? agentControl.GetOrchestratorCredentials(entry.WorkUnitId);
            model = creds?.Model;
            baseUrl = creds?.BaseUrl;
            apiKey = creds?.ApiKey;
            provider = creds?.Provider;
        }

        if (string.IsNullOrWhiteSpace(baseUrl) || apiKey is null)
            return new ContinueResult(ContinueOutcome.NotCompleted, "No LLM credentials resolvable for this work unit.");

        var resolvedCredentialRef = overrideCredentialRef ?? entry.CredentialRef;
        credentialCache?.Capture(resolvedCredentialRef, provider, model, baseUrl, apiKey);

        var conversationLog = serviceProvider.GetRequiredService<IConversationLogService>();
        var priorEntries = (await conversationLog.GetEntriesAsync(entry.WorkUnitId, cancellationToken).ConfigureAwait(false))
            .Where(e => e.AgentId == entry.AgentId)
            .OrderBy(e => e.CycleNumber)
            .ToList();
        var priorTurns = ReconstructTurns(priorEntries);

        await workUnits.UpdateStatusAsync(entry.WorkUnitId, WorkUnitStatus.Retrying, sessionId: null, cancellationToken)
            .ConfigureAwait(false);

        var agentId = $"worker-continue-{Guid.NewGuid():N}";
        var dispatcher = serviceProvider.GetRequiredService<McpToolDispatcher>();
        var llm = serviceProvider.GetRequiredService<LlmClient>();
        var events = serviceProvider.GetService<IExecutionEventStream>();
        var logger = serviceProvider.GetService<ILogger<ContinueService>>();
        var agentClient = new DefaultAgentToolClient(provider ?? "anthropic", model ?? string.Empty, baseUrl, apiKey ?? string.Empty, llm, dispatcher);

        var loop = new WorkerAgentLoop(
            agentId, entry.WorkUnitId, entry.TaskId ?? string.Empty, agentClient,
            isResume: true, conversationLog: conversationLog, events: events,
            priorTurns: priorTurns, logger: logger);

        var completion = await loop.RunAsync(cancellationToken).ConfigureAwait(false);

        if (completion == AgentLoopCompletion.Succeeded)
        {
            try
            {
                await workUnits.UpdateStatusAsync(entry.WorkUnitId, WorkUnitStatus.Executing, sessionId: null, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException) { /* the agent's own tool calls (task.update, merge.propose) may have already moved it further */ }

            return new ContinueResult(ContinueOutcome.Continued, Completion: completion);
        }

        // Neither of these is the agent's own fault — a lease held by another active sibling, or
        // a human clarification request — so unlike the generic NotCompleted path below, don't
        // burn one of the work unit's limited MaxFailureAttempts recording a fresh dead-letter
        // entry for pure infrastructure contention. This loop ran outside the scheduler entirely
        // (RunAsync above, not scheduler.TryAcquireAsync), so there's no existing queue entry for
        // MarkAwaitingFileLeaseAsync to park — enqueue first to create one, then park it. Once
        // IFileLeaseService's release-and-advance hook (or a human clarification response) clears
        // it, the normal scheduler poll picks it up as a fresh scheduled run (isResume:true from
        // AttemptCount > 0, not this call's reconstructed prior-turns context — the conversation
        // was already resumed once to get here).
        if (completion is AgentLoopCompletion.AwaitingFileLease or AgentLoopCompletion.AwaitingClarification)
        {
            var scheduler = serviceProvider.GetRequiredService<IWorkScheduler>();
            await scheduler.EnqueueAsync(
                entry.WorkUnitId, entry.ProfileId, taskId: entry.TaskId,
                model: model, baseUrl: baseUrl, apiKey: apiKey, provider: provider,
                sessionId: null, credentialRef: resolvedCredentialRef, ct: cancellationToken).ConfigureAwait(false);

            if (completion == AgentLoopCompletion.AwaitingFileLease)
                await scheduler.MarkAwaitingFileLeaseAsync(entry.WorkUnitId, cancellationToken).ConfigureAwait(false);

            return new ContinueResult(
                ContinueOutcome.Parked,
                completion == AgentLoopCompletion.AwaitingFileLease
                    ? "Continue hit a file lease held by another active work unit — parked, will resume automatically once it's released."
                    : "Continue requested a human clarification — parked, will resume once it's answered.",
                completion);
        }

        // Didn't finish again — record a fresh dead-letter entry with the same Kind so Continue
        // can be reached for again, or the human can switch to Re-plan instead.
        await deadLetter.RecordFailureAsync(
            entry.WorkUnitId,
            agentId,
            entry.Stage,
            entry.ProfileId,
            completion == AgentLoopCompletion.MaxIterationsExceeded
                ? "Max iterations reached again after continuing with prior context."
                : $"Continue attempt ended without completing (completion: {completion}).",
            entry.TaskId,
            sessionId: null,
            model: model,
            baseUrl: baseUrl,
            apiKey: apiKey,
            provider: provider,
            kind: FailureKind.MaxIterationsExceeded,
            credentialRef: resolvedCredentialRef,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ContinueResult(
            ContinueOutcome.NotCompleted,
            $"Continue attempt did not complete successfully (completion: {completion}).",
            completion);
    }

    // Converts each cycle's ConversationLogEntry back into the (assistant, user-tool-results) pair
    // WorkerAgentLoop's own messages list would have held at that point. A tool call's InputJson is
    // parsed and cloned (JsonElement.Clone()) so it's safe to use after the JsonDocument backing it
    // would otherwise be reclaimed.
    internal static List<NmMessage> ReconstructTurns(IReadOnlyList<ConversationLogEntry> entries)
    {
        var turns = new List<NmMessage>();

        foreach (var entry in entries)
        {
            var assistantContent = new List<NmContent>();
            if (!string.IsNullOrEmpty(entry.AssistantText))
                assistantContent.Add(new NmText(entry.AssistantText));
            foreach (var toolCall in entry.ToolCalls)
            {
                var input = JsonDocument.Parse(toolCall.InputJson).RootElement.Clone();
                assistantContent.Add(new NmToolUse(toolCall.ToolUseId, toolCall.Name, input));
            }

            if (assistantContent.Count > 0)
                turns.Add(new NmMessage("assistant", assistantContent));

            if (entry.ToolResults.Count > 0)
            {
                var toolResultContent = entry.ToolResults
                    .Select(r => (NmContent)new NmToolResult(r.ToolUseId, r.Result))
                    .ToList();
                turns.Add(new NmMessage("user", toolResultContent));
            }
        }

        return turns;
    }
}
