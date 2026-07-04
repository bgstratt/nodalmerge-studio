using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

// Phase 2 item 2 follow-up — user-initiated dead-letter recovery, mirroring the REST endpoints
// in StudioRestEndpoints.MapDeadLetterEndpoints one-for-one, so an external MCP client (e.g. a
// human's own coding assistant) has the same recovery actions the VS Code dashboard's dead-letter
// card already exposes: discover a failure (list/get/by-work-unit/history), then act on it
// (retry, retry-with-context, re-plan, continue). Deliberately separate from McpToolDispatcher
// (src/NodalMerge.Studio.AgentRuntime), the internal in-process dispatch table spawned agent
// loops use on themselves mid-run — these are human-initiated actions on an already-failed,
// already-exited work unit, not something a running agent would ever call on itself.
public sealed class DeadLetterTools(
    IDeadLetterService deadLetter,
    IReplanService replan,
    IContinueService continueService)
{
    // Matches StudioRestEndpoints.RedactApiKey/RedactForRest exactly — an API key must never
    // leave this process unredacted over any transport, REST or MCP alike.
    private static string? RedactApiKey(string? apiKey) =>
        string.IsNullOrEmpty(apiKey) ? apiKey
        : apiKey.Length <= 8 ? "***"
        : $"{apiKey[..3]}...{apiKey[^4..]}";

    private static DeadLetterEntry Redact(DeadLetterEntry entry) =>
        entry with { ApiKey = RedactApiKey(entry.ApiKey) };

    [McpServerTool(Name = McpToolNames.DeadLetterList), Description("List every dead-letter entry across all work units.")]
    public async Task<string> ListAsync(CancellationToken cancellationToken = default)
    {
        var list = await deadLetter.ListAsync(cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(list.Select(Redact));
    }

    [McpServerTool(Name = McpToolNames.DeadLetterGet), Description("Get a single dead-letter entry by its own entry ID.")]
    public async Task<string> GetAsync(string entryId, CancellationToken cancellationToken = default)
    {
        var entry = await deadLetter.GetAsync(entryId, cancellationToken).ConfigureAwait(false);
        return entry is null
            ? McpJson.Error(McpToolNames.DeadLetterGet, $"Dead-letter entry '{entryId}' not found.")
            : McpJson.Ok(Redact(entry));
    }

    [McpServerTool(Name = McpToolNames.DeadLetterByWorkUnit), Description("Get the latest dead-letter entry for a work unit, if any.")]
    public async Task<string> ByWorkUnitAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        var entry = await deadLetter.GetLatestForWorkUnitAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        return entry is null
            ? McpJson.Error(McpToolNames.DeadLetterByWorkUnit, $"No dead-letter entry found for work unit '{workUnitId}'.")
            : McpJson.Ok(Redact(entry));
    }

    [McpServerTool(Name = McpToolNames.DeadLetterHistory), Description("Get the full failure history for a work unit, oldest first (e.g. \"max iterations\" -> steered retry -> \"transient 529\") in one call.")]
    public async Task<string> HistoryAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        var history = await deadLetter.GetHistoryForWorkUnitAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(history.Select(Redact));
    }

    [McpServerTool(Name = McpToolNames.DeadLetterRetry), Description("Retry a dead-lettered work unit, resuming the same work with its captured credentials. Optionally override the model/provider/credentials for this retry only.")]
    public async Task<string> RetryAsync(
        string entryId,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        CancellationToken cancellationToken = default)
    {
        var hasOverride = overrideModel is not null || overrideBaseUrl is not null || overrideApiKey is not null
            || overrideProvider is not null || overrideProfileId is not null;
        var result = hasOverride
            ? await deadLetter.RetryWithCredentialOverrideAsync(
                entryId, overrideModel, overrideBaseUrl, overrideApiKey, overrideProvider, overrideProfileId,
                cancellationToken).ConfigureAwait(false)
            : await deadLetter.RetryAsync(entryId, cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            DeadLetterRetryOutcome.Retried => McpJson.Ok(result),
            _ => McpJson.Error(McpToolNames.DeadLetterRetry, result.Message ?? $"Retry failed: {result.Outcome}."),
        };
    }

    [McpServerTool(Name = McpToolNames.DeadLetterRetryWithContext), Description("Retry a dead-lettered work unit with a human-supplied correction folded into its goal — bypasses the normal max-attempts cap since the correction addresses a different root cause than what produced the prior failures.")]
    public async Task<string> RetryWithContextAsync(
        string entryId,
        string steeringContext,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideProfileId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(steeringContext))
            return McpJson.Error(McpToolNames.DeadLetterRetryWithContext, "steeringContext is required.");

        var result = await deadLetter.RetryWithContextAsync(
            entryId, steeringContext, overrideModel, overrideBaseUrl, overrideApiKey, overrideProvider,
            overrideProfileId, cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            DeadLetterRetryOutcome.Retried => McpJson.Ok(result),
            _ => McpJson.Error(McpToolNames.DeadLetterRetryWithContext, result.Message ?? $"Retry failed: {result.Outcome}."),
        };
    }

    [McpServerTool(Name = McpToolNames.DeadLetterReplan), Description("Re-plan a dead-lettered fan-out slice: spawns a bounded planner scoped to just the failed slice's goal + failure reason, fans out fresh independently-budgeted sub-slices, and marks the original Cancelled. Never resumes the failed work unit itself — only applies to a slice with a parent, not a top-level goal.")]
    public async Task<string> ReplanAsync(string entryId, CancellationToken cancellationToken = default)
    {
        var result = await replan.ReplanFailedSliceAsync(entryId, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            ReplanOutcome.Replanned => McpJson.Ok(result),
            _ => McpJson.Error(McpToolNames.DeadLetterReplan, result.Message ?? $"Re-plan failed: {result.Outcome}."),
        };
    }

    [McpServerTool(Name = McpToolNames.DeadLetterContinue), Description("Continue a dead-lettered work unit that hit its iteration limit: resumes the SAME work unit with its own prior conversation reconstructed and a fresh iteration budget. Only valid for MaxIterationsExceeded failures — use retry or re-plan for anything else.")]
    public async Task<string> ContinueAsync(string entryId, CancellationToken cancellationToken = default)
    {
        var result = await continueService.ContinueWithPriorContextAsync(entryId, cancellationToken).ConfigureAwait(false);
        return result.Outcome switch
        {
            ContinueOutcome.Continued => McpJson.Ok(result),
            _ => McpJson.Error(McpToolNames.DeadLetterContinue, result.Message ?? $"Continue failed: {result.Outcome}."),
        };
    }
}
