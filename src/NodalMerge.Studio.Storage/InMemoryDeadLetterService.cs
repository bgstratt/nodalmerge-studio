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
    IProjectionManager projections) : IDeadLetterService
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
        CancellationToken cancellationToken = default)
    {
        var unit = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Work unit '{workUnitId}' was not found.");

        var metadata = new Dictionary<string, string>(unit.Metadata ?? new Dictionary<string, string>());
        var previousCount = metadata.TryGetValue(WorkUnitMetadataKeys.FailureAttemptCount, out var rawCount) &&
                            int.TryParse(rawCount, out var parsed)
            ? parsed
            : 0;
        var attemptCount = previousCount + 1;
        metadata[WorkUnitMetadataKeys.FailureAttemptCount] = attemptCount.ToString();

        var updatedUnit = unit with { Metadata = metadata };
        await workUnits.CreateAsync(updatedUnit, cancellationToken).ConfigureAwait(false);

        var snapshot = lastProjectionSnapshot ?? await TryCaptureProjectionAsync(workUnitId, cancellationToken)
            .ConfigureAwait(false);

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
            attemptCount >= MaxFailureAttempts);

        _entries[entry.EntryId] = entry;
        await nodeStore.WriteNodeAsync(
            StudioNodeKind.DeadLetterV1,
            entry.EntryId,
            JsonSerializer.Serialize(entry),
            cancellationToken).ConfigureAwait(false);

        try
        {
            await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.DeadLettered, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }

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

    public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<DeadLetterEntry>>(
            _entries.Values.OrderByDescending(e => e.OccurredAt).ToList());

    public async Task<DeadLetterRetryResult> RetryAsync(
        string entryId,
        CancellationToken cancellationToken = default)
    {
        if (!_entries.TryGetValue(entryId, out var entry))
            return new DeadLetterRetryResult(DeadLetterRetryOutcome.NotFound, "Dead-letter entry not found.");

        if (entry.MaxAttemptsReached || entry.AttemptCount >= MaxFailureAttempts)
        {
            return new DeadLetterRetryResult(
                DeadLetterRetryOutcome.MaxAttemptsReached,
                "Max attempts reached.");
        }

        var unit = await workUnits.GetAsync(entry.WorkUnitId, cancellationToken).ConfigureAwait(false);
        if (unit is null)
            return new DeadLetterRetryResult(DeadLetterRetryOutcome.InvalidState, "Work unit not found.");

        if (unit.Status is not WorkUnitStatus.DeadLettered and not WorkUnitStatus.Retrying)
        {
            return new DeadLetterRetryResult(
                DeadLetterRetryOutcome.InvalidState,
                $"Work unit is in status {unit.Status}; expected DeadLettered.");
        }

        var orchestratorTarget = unit.ParentWorkUnitId ?? unit.WorkUnitId;
        var creds = agentControl.GetOrchestratorCredentials(orchestratorTarget);

        try
        {
            await workUnits
                .UpdateStatusAsync(entry.WorkUnitId, WorkUnitStatus.Retrying, null, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException) { }

        await scheduler.EnqueueAsync(
            entry.WorkUnitId,
            entry.ProfileId,
            taskId: entry.TaskId,
            model: creds?.Model,
            baseUrl: creds?.BaseUrl,
            apiKey: creds?.ApiKey,
            provider: creds?.Provider,
            sessionId: null,
            ct: cancellationToken).ConfigureAwait(false);

        return new DeadLetterRetryResult(DeadLetterRetryOutcome.Retried);
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
