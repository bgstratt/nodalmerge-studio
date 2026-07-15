using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class InMemoryDeadLetterService(
    IStudioNodeStore nodeStore,
    IWorkUnitService workUnits,
    IWorkScheduler scheduler,
    IAgentControlService agentControl,
    IOrchestrationDecisionLogService decisionLog,
    IProjectionManager projections,
    ISteeringDecisionService steeringDecisions,
    IFileLeaseService fileLease,
    IRuntimeCredentialCache? credentialCache = null,
    // plans/phase-d-implementation.md D3 — cheapest correct hook for the "dead-lettered slices"
    // staleness signal: every terminal dead-letter (this method) passes through here. Optional/
    // nullable, same convention as credentialCache above.
    IPlanStalenessService? planStaleness = null) : IDeadLetterService, IRehydratable
{
    public const int MaxFailureAttempts = 3;

    private readonly ConcurrentDictionary<string, DeadLetterEntry> _entries = new();

    public async Task<DeadLetterEntry> RecordFailureAsync(
        string workUnitId,
        string agentId,
        PipelineStage stage,
        string profileId,
        string reason,
        string? taskId = null,
        string? lastProjectionSnapshot = null,
        string? sessionId = null,
        string? model = null,
        string? baseUrl = null,
        string? apiKey = null,
        string? provider = null,
        FailureKind kind = FailureKind.Exception,
        string? credentialRef = null,
        CancellationToken cancellationToken = default)
    {
        var updatedUnit = await workUnits.IncrementFailureAttemptCountAsync(workUnitId, cancellationToken)
            .ConfigureAwait(false);
        var attemptCount = updatedUnit.ExecutionInfo!.FailureAttemptCount;

        var snapshot = lastProjectionSnapshot ?? await TryCaptureProjectionAsync(workUnitId, cancellationToken)
            .ConfigureAwait(false);

        // Shared capture point, same as WorkSchedulerService.EnqueueAsync — a dead letter carries
        // whatever credentials the failed run actually used, so warm the cache from them too.
        credentialCache?.Capture(credentialRef, provider, model, baseUrl, apiKey);

        var entry = new DeadLetterEntry(
            $"DL-{Guid.NewGuid():N}",
            workUnitId,
            agentId,
            stage,
            profileId,
            reason,
            snapshot,
            attemptCount,
            DateTimeOffset.UtcNow,
            taskId,
            attemptCount >= MaxFailureAttempts,
            model,
            baseUrl,
            apiKey,
            provider,
            kind,
            credentialRef);

        _entries[entry.EntryId] = entry;
        await nodeStore.WriteNodeAsync(
            StudioNodeKind.DeadLetterV1,
            entry.EntryId,
            JsonSerializer.Serialize(entry),
            cancellationToken).ConfigureAwait(false);

        // Phase 12 — only on the final, no-more-retries failure (RetryAsync itself refuses once
        // MaxAttemptsReached is true, so this is genuinely terminal): release every file lease
        // this work unit held and drop it from any queue it was waiting in, so a holder that will
        // never merge doesn't strand its waiters forever. Not done on earlier attempts — a unit
        // that still has retries left may yet succeed and merge, and releasing its lease early
        // would let a waiter jump in and create exactly the conflict the lease exists to prevent.
        if (entry.MaxAttemptsReached)
        {
            var promoted = await fileLease.ForceReleaseAllForWorkUnitAsync(workUnitId, cancellationToken)
                .ConfigureAwait(false);
            foreach (var promotedWorkUnitId in promoted)
                await scheduler.ClearAwaitingFileLeaseAsync(promotedWorkUnitId, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.DeadLettered, sessionId, cancellationToken)
                .ConfigureAwait(false);
            await workUnits.SetCurrentStageAsync(workUnitId, stage, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }

        if (planStaleness is not null)
            await planStaleness.NotifySliceDeadLetteredAsync(workUnitId, cancellationToken).ConfigureAwait(false);

        await RecordEscalationAsync(workUnitId, agentId, stage, snapshot, reason, sessionId, cancellationToken)
            .ConfigureAwait(false);

        return entry;
    }

    public Task<DeadLetterEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default)
    {
        _entries.TryGetValue(entryId, out var entry);
        return Task.FromResult(entry);
    }

    public Task<DeadLetterEntry?> GetLatestForWorkUnitAsync(
        string workUnitId,
        CancellationToken cancellationToken = default)
    {
        var latest = _entries.Values
            .Where(e => e.WorkUnitId == workUnitId)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<IReadOnlyList<DeadLetterEntry>> GetHistoryForWorkUnitAsync(
        string workUnitId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterEntry>>(
            _entries.Values
                .Where(e => e.WorkUnitId == workUnitId)
                .OrderBy(e => e.OccurredAt)
                .ToList());

    public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterEntry>>(
            _entries.Values.OrderByDescending(e => e.OccurredAt).ToList());

    public async Task<DeadLetterRetryResult> RetryAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(entryId, out var entry))
            return new DeadLetterRetryResult(DeadLetterRetryOutcome.NotFound, "Dead-letter entry not found.");

        // No max-attempts block here. RetryAsync is only ever reached by an explicit retry request
        // (REST endpoint or MCP dead-letter tool) — the automated pipeline's own spin-prevention
        // happens upstream, in the rejection/failure counters that decide whether to dead-letter in
        // the first place. Someone explicitly asking to retry a dead-lettered unit has already seen
        // the attempt count and decided to spend more; the system's job is to warn (the count is on
        // the entry), not to block. Previously this returned MaxAttemptsReached and stranded the
        // work with no recovery at all once AttemptCount hit the cap.

        var unit = await workUnits.GetAsync(entry.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
            return new DeadLetterRetryResult(DeadLetterRetryOutcome.InvalidState, "Work unit not found.");

        if (unit.Status is not WorkUnitStatus.DeadLettered and not WorkUnitStatus.Retrying)
        {
            return new DeadLetterRetryResult(
                DeadLetterRetryOutcome.InvalidState,
                $"Work unit is in status {unit.Status}; expected DeadLettered.");
        }

        var creds = ResolveRetryCredentials(entry, unit);

        try
        {
            await workUnits
                .UpdateStatusAsync(entry.WorkUnitId, WorkUnitStatus.Retrying, null, cancellationToken)
                .ConfigureAwait(false);
            await workUnits.SetCurrentStageAsync(entry.WorkUnitId, entry.Stage, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }

        await scheduler.EnqueueAsync(
            entry.WorkUnitId,
            entry.ProfileId,
            taskId: entry.TaskId,
            model: creds.Model,
            baseUrl: creds.BaseUrl,
            apiKey: creds.ApiKey,
            provider: creds.Provider,
            sessionId: null,
            credentialRef: creds.CredentialRef,
            ct: cancellationToken).ConfigureAwait(false);

        return new DeadLetterRetryResult(DeadLetterRetryOutcome.Retried);
    }

    public async Task<DeadLetterRetryResult> RetryWithCredentialOverrideAsync(
        string entryId,
        string? overrideModel,
        string? overrideBaseUrl,
        string? overrideApiKey,
        string? overrideProvider,
        string? overrideProfileId,
        string? overrideCredentialRef = null,
        CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(entryId, out var entry))
            return new DeadLetterRetryResult(DeadLetterRetryOutcome.NotFound, "Dead-letter entry not found.");

        // Same reasoning as RetryAsync above — an explicit human retry (here with different
        // credentials/model, i.e. even more clearly a deliberate new decision) is never blocked by
        // the attempt cap, only informed by it.

        var unit = await workUnits.GetAsync(entry.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
            return new DeadLetterRetryResult(DeadLetterRetryOutcome.InvalidState, "Work unit not found.");

        if (unit.Status is not WorkUnitStatus.DeadLettered and not WorkUnitStatus.Retrying)
        {
            return new DeadLetterRetryResult(
                DeadLetterRetryOutcome.InvalidState,
                $"Work unit is in status {unit.Status}; expected DeadLettered.");
        }

        var profileId = overrideProfileId ?? entry.ProfileId;
        var creds = ResolveRetryCredentials(entry, unit, overrideModel, overrideBaseUrl, overrideApiKey, overrideProvider, overrideCredentialRef);

        try
        {
            await workUnits
                .UpdateStatusAsync(entry.WorkUnitId, WorkUnitStatus.Retrying, null, cancellationToken)
                .ConfigureAwait(false);
            await workUnits.SetCurrentStageAsync(entry.WorkUnitId, entry.Stage, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }

        await scheduler.EnqueueAsync(
            entry.WorkUnitId,
            profileId,
            taskId: entry.TaskId,
            model: creds.Model,
            baseUrl: creds.BaseUrl,
            apiKey: creds.ApiKey,
            provider: creds.Provider,
            sessionId: null,
            credentialRef: creds.CredentialRef,
            ct: cancellationToken).ConfigureAwait(false);

        return new DeadLetterRetryResult(DeadLetterRetryOutcome.Retried);
    }

    // Prefer whatever credentials were captured directly on the entry at failure time — those are
    // exactly what the failed run used, regardless of stage. Only entries recorded before this
    // capture existed (or a run that genuinely had no credentials) fall through to the live
    // in-memory orchestrator registry, which is best-effort: it doesn't survive a Host restart or
    // the orchestrator's own loop completing, so by retry time it may simply have nothing for this
    // work unit anymore.
    // When credential overrides are supplied, they take top priority — this lets a human retry
    // with a different model/profile (e.g. switching from vscode-lm to deepseek) without spawning
    // a new work unit.
    // ApiKey itself is resolved in three tiers, cheapest/freshest first: entry.ApiKey (same-process,
    // never survives a restart since it's [JsonIgnore]d), then IRuntimeCredentialCache by
    // CredentialRef (survives a restart iff someone has resupplied it since), then whatever the live
    // orchestrator registry still has. If all three miss, ApiKey comes back null and the caller
    // parks AwaitingCredentials instead of dispatching with nothing.
    private (string? Model, string? BaseUrl, string? ApiKey, string? Provider, string? CredentialRef) ResolveRetryCredentials(
        DeadLetterEntry entry, WorkUnit unit,
        string? overrideModel = null,
        string? overrideBaseUrl = null,
        string? overrideApiKey = null,
        string? overrideProvider = null,
        string? overrideCredentialRef = null)
    {
        // Phase Y — human-chosen credential override takes absolute priority.
        if (!string.IsNullOrWhiteSpace(overrideModel) || !string.IsNullOrWhiteSpace(overrideBaseUrl))
            return (overrideModel, overrideBaseUrl, overrideApiKey, overrideProvider, overrideCredentialRef);

        if (!string.IsNullOrWhiteSpace(entry.BaseUrl) || !string.IsNullOrWhiteSpace(entry.Model))
        {
            var apiKey = entry.ApiKey ?? credentialCache?.TryGet(entry.CredentialRef)?.ApiKey;
            return (entry.Model, entry.BaseUrl, apiKey, entry.Provider, entry.CredentialRef);
        }

        var creds = agentControl.GetCredentialsForStage(unit.WorkUnitId, entry.Stage)
            ?? agentControl.GetGoalDefaultCredentials(unit.WorkUnitId)
            ?? (unit.ParentWorkUnitId is { } parentId
                ? agentControl.GetCredentialsForStage(parentId, entry.Stage) ?? agentControl.GetGoalDefaultCredentials(parentId)
                : null);
        return (creds?.Model, creds?.BaseUrl, creds?.ApiKey, creds?.Provider, creds?.CredentialRef);
    }

    public async Task<DeadLetterRetryResult> RetryWithContextAsync(
        string entryId,
        string steeringContext,
        string? overrideModel,
        string? overrideBaseUrl,
        string? overrideApiKey,
        string? overrideProvider,
        string? overrideProfileId,
        string? overrideCredentialRef,
        CancellationToken cancellationToken)
    {
        if (!_entries.TryGetValue(entryId, out var entry))
            return new DeadLetterRetryResult(DeadLetterRetryOutcome.NotFound, "Dead-letter entry not found.");

        var unit = await workUnits.GetAsync(entry.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
            return new DeadLetterRetryResult(DeadLetterRetryOutcome.InvalidState, "Work unit not found.");

        if (unit.Status is not WorkUnitStatus.DeadLettered and not WorkUnitStatus.Retrying)
        {
            return new DeadLetterRetryResult(
                DeadLetterRetryOutcome.InvalidState,
                $"Work unit is in status {unit.Status}; expected DeadLettered.");
        }

        // Fold the correction into Goal — projections only ever surface Goal/SuccessCriteria to
        // the agent, never Metadata, so a constraint stashed only in Metadata would never be seen.
        var amendedGoal = $"{unit.Goal}\n\n[Correction after dead-letter retry]: {steeringContext}";
        await workUnits.AmendGoalForSteeredRetryAsync(
            entry.WorkUnitId, amendedGoal, steeringContext, entryId, cancellationToken).ConfigureAwait(false);

        var profileId = overrideProfileId ?? entry.ProfileId;
        var creds = ResolveRetryCredentials(entry, unit, overrideModel, overrideBaseUrl, overrideApiKey, overrideProvider, overrideCredentialRef);

        try
        {
            await workUnits
                .UpdateStatusAsync(entry.WorkUnitId, WorkUnitStatus.Retrying, null, cancellationToken)
                .ConfigureAwait(false);
            await workUnits.SetCurrentStageAsync(entry.WorkUnitId, entry.Stage, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }

        await scheduler.EnqueueAsync(
            entry.WorkUnitId,
            profileId,
            taskId: entry.TaskId,
            model: creds.Model,
            baseUrl: creds.BaseUrl,
            apiKey: creds.ApiKey,
            provider: creds.Provider,
            sessionId: null,
            credentialRef: creds.CredentialRef,
            ct: cancellationToken).ConfigureAwait(false);

        await steeringDecisions.RecordAsync(
            new SteeringDecision(
                SteeringDecisionId: $"steer-dl-{Guid.NewGuid():N}",
                WorkUnitId: entry.WorkUnitId,
                AgentId: null,
                InjectedConstraint: steeringContext,
                NewChildWorkUnitId: null,
                SteeredAt: DateTimeOffset.UtcNow,
                SessionId: null),
            cancellationToken).ConfigureAwait(false);

        return new DeadLetterRetryResult(DeadLetterRetryOutcome.Retried);
    }

    // Phase Y — obsolete overload kept for backward compat with existing callers that use the
    // old 3-parameter signature; delegates to the full override-capable overload with null overrides.
    public Task<DeadLetterRetryResult> RetryWithContextAsync(
        string entryId,
        string steeringContext,
        CancellationToken cancellationToken = default)
        => RetryWithContextAsync(entryId, steeringContext, null, null, null, null, null, null, cancellationToken);

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await nodeStore.ReadAllNodesAsync(StudioNodeKind.DeadLetterV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var entry = JsonSerializer.Deserialize<DeadLetterEntry>(payloadJson);
            if (entry is not null)
                _entries[entityId] = entry;
        }
    }

    private async Task<string?> TryCaptureProjectionAsync(string workUnitId, CancellationToken ct)
    {
        try
        {
            var result = await projections
                .GetAsync(
                    new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Compact, WorkUnitId: workUnitId),
                    ct)
                .ConfigureAwait(false);
            return result.DataJson;
        }
        catch
        {
            return null;
        }
    }

    private async Task RecordEscalationAsync(
        string workUnitId,
        string agentId,
        PipelineStage stage,
        string? snapshot,
        string reason,
        string? sessionId,
        CancellationToken ct)
    {
        var unit = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        var orchestratorTarget = unit?.ParentWorkUnitId ?? workUnitId;

        try
        {
            await decisionLog.RecordAsync(
                orchestratorTarget,
                agentId,
                stage,
                snapshot ?? "{}",
                OrchestrationAction.Escalate,
                [workUnitId],
                reason,
                sessionId,
                ct).ConfigureAwait(false);
        }
        catch
        {
            // Escalation logging is best-effort when no orchestrator session exists.
        }
    }
}
