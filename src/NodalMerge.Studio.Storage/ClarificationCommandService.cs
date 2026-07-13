using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Slice 15d — single clarification workflow implementation shared by MCP, REST, and
// agent-loop dispatcher so pause/resume/event behavior cannot drift across transports.
public sealed class ClarificationCommandService(
    IWorkScheduler scheduler,
    IWorkUnitService workUnits,
    IExecutionEventStream events,
    IFileWorkspaceService fileWorkspace,
    WorkspaceOptions? workspaceOptions = null,
    IGoalNodeService? goalNodes = null) : IClarificationCommandService
{
    private static readonly HashSet<WorkUnitStatus> AbandonedStatuses =
    [
        WorkUnitStatus.Cancelled,
        WorkUnitStatus.Completed,
        WorkUnitStatus.Failed,
        WorkUnitStatus.DeadLettered,
        WorkUnitStatus.Merged,
    ];

    public async Task<ClarificationRequestResult> RequestAsync(
        string workUnitId,
        string question,
        string? context = null,
        bool blocking = true,
        IReadOnlyList<string>? options = null,
        string? requestedByAgentId = null,
        string? sessionId = null,
        int? timeoutSeconds = null,
        string? timeoutBehavior = null,
        string? defaultResponse = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workUnitId))
            throw new ArgumentException("workUnitId is required.", nameof(workUnitId));
        if (string.IsNullOrWhiteSpace(question))
            throw new ArgumentException("question is required.", nameof(question));

        var requestId = $"CLR-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var resolvedSessionId = await ResolveSessionIdAsync(workUnitId, sessionId, ct).ConfigureAwait(false);
        var optionsList = options?.Where(o => !string.IsNullOrWhiteSpace(o)).ToList() ?? [];

        // Apply workspace-level timeout defaults when the agent doesn't specify one.
        if (timeoutSeconds is null && workspaceOptions?.DefaultClarificationTimeoutSeconds > 0)
        {
            timeoutSeconds = workspaceOptions.DefaultClarificationTimeoutSeconds;
            timeoutBehavior ??= workspaceOptions.DefaultClarificationTimeoutBehavior;
        }

        if (blocking)
        {
            await scheduler.MarkAwaitingResumeAsync(workUnitId, ct).ConfigureAwait(false);
            await TryUpdateStatusAsync(workUnitId, WorkUnitStatus.Waiting, resolvedSessionId, ct).ConfigureAwait(false);
        }

        // Unconditional — this event is the SOLE source ListActiveRequestsAsync / RespondAsync /
        // ClarificationTimerService read from. Before 2026-07-13 it was gated on a resolvable
        // sessionId, so a session-less blocking request parked the work unit (above) while being
        // invisible in the inbox and unanswerable: a stuck goal with no visible cause (found by the
        // C3 real-CLI smoke — see plans/review-seam-and-clarification-sessions.md S1).
        // ResolveSessionIdAsync now never returns null (synthetic wu-{workUnitId} last resort).
        {
            await events.AppendAsync(
                resolvedSessionId,
                workUnitId,
                ExecutionEventKind.ClarificationRequested,
                new ClarificationRequestedPayload(
                    requestId,
                    workUnitId,
                    question,
                    context,
                    blocking,
                    optionsList,
                    requestedByAgentId,
                    now,
                    timeoutSeconds,
                    timeoutBehavior,
                    defaultResponse),
                ct: ct).ConfigureAwait(false);
        }

        return new ClarificationRequestResult(
            requestId,
            workUnitId,
            blocking,
            ParkedAwaitingResponse: blocking,
            Status: blocking ? "awaiting_clarification" : "recorded");
    }

    public async Task<IReadOnlyList<ScheduledItem>> ListAwaitingAsync(CancellationToken ct = default)
    {
        var awaiting = await scheduler.ListAwaitingResumeAsync(ct).ConfigureAwait(false);
        if (awaiting.Count == 0)
            return awaiting;

        var filtered = new List<ScheduledItem>();
        foreach (var item in awaiting)
        {
            var unit = await workUnits.GetAsync(item.WorkUnitId, ct).ConfigureAwait(false);
            if (unit?.Status == WorkUnitStatus.Waiting)
                filtered.Add(item);
        }

        return filtered;
    }

    public async Task<IReadOnlyList<ClarificationInboxItem>> ListActiveRequestsAsync(CancellationToken ct = default)
    {
        var requests = await BuildRequestMapAsync(ct).ConfigureAwait(false);
        if (requests.Count == 0)
            return [];

        var awaiting = await scheduler.ListAwaitingResumeAsync(ct).ConfigureAwait(false);
        var awaitingSet = awaiting.Select(i => i.WorkUnitId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var units = await workUnits.ListAsync(cancellationToken: ct).ConfigureAwait(false);
        var unitById = units.ToDictionary(u => u.WorkUnitId, StringComparer.OrdinalIgnoreCase);

        var items = requests.Values
            .Where(r => r.Response is null)
            .Select(r =>
            {
                var unit = unitById.GetValueOrDefault(r.WorkUnitId);
                var status = unit is null
                    ? "open"
                    : AbandonedStatuses.Contains(unit.Status)
                        ? "abandoned"
                        : awaitingSet.Contains(r.WorkUnitId)
                            ? "awaiting_response"
                            : "open";

                return new ClarificationInboxItem(
                    RequestId: r.RequestId,
                    SessionId: r.SessionId,
                    WorkUnitId: r.WorkUnitId,
                    Goal: unit?.Goal ?? r.WorkUnitId,
                    Question: r.Question,
                    Context: r.Context,
                    Blocking: r.Blocking,
                    Options: r.Options,
                    RequestedByAgentId: r.RequestedByAgentId,
                    RequestedAt: r.RequestedAt,
                    Status: status,
                    Response: null,
                    ResponseNote: null,
                    RespondedBy: null,
                    RespondedAt: null,
                    AwaitingResume: awaitingSet.Contains(r.WorkUnitId),
                    TimeoutSeconds: r.TimeoutSeconds,
                    TimeoutAt: r.TimeoutSeconds.HasValue ? r.RequestedAt.AddSeconds(r.TimeoutSeconds.Value) : null,
                    TimeoutBehavior: r.TimeoutBehavior);
            })
            .OrderByDescending(i => i.RequestedAt)
            .ToList();

        return items;
    }

    public async Task<ClarificationMetrics> GetMetricsAsync(CancellationToken ct = default)
    {
        var requests = await BuildRequestMapAsync(ct).ConfigureAwait(false);
        if (requests.Count == 0)
            return new ClarificationMetrics(0, 0, 0, []);

        var units = await workUnits.ListAsync(cancellationToken: ct).ConfigureAwait(false);
        var unitById = units.ToDictionary(u => u.WorkUnitId, StringComparer.OrdinalIgnoreCase);

        var answered = 0;
        var abandoned = 0;
        var perGoal = new Dictionary<string, ClarificationGoalMetric>(StringComparer.OrdinalIgnoreCase);

        foreach (var req in requests.Values)
        {
            var unit = unitById.GetValueOrDefault(req.WorkUnitId);
            var goal = unit?.Goal ?? req.WorkUnitId;

            if (!perGoal.TryGetValue(req.WorkUnitId, out var metric))
                metric = new ClarificationGoalMetric(req.WorkUnitId, goal, 0, 0, 0);

            var isAnswered = req.Response is not null;
            var isAbandoned = !isAnswered && unit is not null && AbandonedStatuses.Contains(unit.Status);

            if (isAnswered) answered++;
            if (isAbandoned) abandoned++;

            perGoal[req.WorkUnitId] = metric with
            {
                Requests = metric.Requests + 1,
                Answered = metric.Answered + (isAnswered ? 1 : 0),
                Abandoned = metric.Abandoned + (isAbandoned ? 1 : 0),
            };
        }

        return new ClarificationMetrics(
            Requests: requests.Count,
            Answered: answered,
            Abandoned: abandoned,
            PerGoal: perGoal.Values.OrderByDescending(v => v.Requests).ThenBy(v => v.Goal).ToList());
    }

    public async Task<ClarificationResponseResult> RespondAsync(
        string workUnitId,
        string response,
        string? note = null,
        string? respondedBy = null,
        string? requestId = null,
        bool resume = true,
        string? sessionId = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(workUnitId))
            throw new ArgumentException("workUnitId is required.", nameof(workUnitId));
        if (string.IsNullOrWhiteSpace(response))
            throw new ArgumentException("response is required.", nameof(response));

        var now = DateTimeOffset.UtcNow;
        var requested = await ResolveRequestAsync(workUnitId, requestId, sessionId, ct).ConfigureAwait(false);

        if (resume)
        {
            await scheduler.ApproveResumeAsync(workUnitId, ct).ConfigureAwait(false);
            await TryUpdateStatusAsync(workUnitId, WorkUnitStatus.Queued, requested.SessionId, ct).ConfigureAwait(false);
            // plans/harness-hosting-architecture.md Phase B3 — the outbox half of the pause/resume
            // loop. Harmless for a native worker (it never reads .workspace/outbox); a respawned
            // ClaudeCodeExecutor's kickoff prompt tells it to check here for the answer before
            // asking again. Unconditional (not executor-specific) — the same posture as inbox
            // harvesting, which also runs regardless of which executor a work unit uses.
            await WriteOutboxAnswerAsync(workUnitId, response, ct).ConfigureAwait(false);
        }

        if (requested.SessionId is not null)
        {
            await events.AppendAsync(
                requested.SessionId,
                workUnitId,
                ExecutionEventKind.ClarificationResponded,
                new ClarificationRespondedPayload(
                    requested.RequestId,
                    workUnitId,
                    response,
                    note,
                    respondedBy,
                    now,
                    Resumed: resume),
                ct: ct).ConfigureAwait(false);
        }

        return new ClarificationResponseResult(
            RequestId: requested.RequestId,
            WorkUnitId: workUnitId,
            Resumed: resume,
            Status: resume ? "resumed" : "response_recorded");
    }

    // Numbered files, same convention as .workspace/inbox and .workspace/decisions
    // (WorkspaceContractService) — no frontmatter needed, the whole file content is the answer.
    private async Task WriteOutboxAnswerAsync(string workUnitId, string response, CancellationToken ct)
    {
        var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
        if (wu is null)
            return;

        var existing = await fileWorkspace
            .ListIncludingDotfilesAsync(wu.BranchId, ".workspace/outbox", ct)
            .ConfigureAwait(false);
        var next = existing
            .Select(f => int.TryParse(Path.GetFileNameWithoutExtension(f), out var n) ? n : (int?)null)
            .Where(n => n.HasValue)
            .Select(n => n!.Value)
            .DefaultIfEmpty(0)
            .Max() + 1;

        await fileWorkspace.WriteAsync(wu.BranchId, $".workspace/outbox/{next:0000}.md", response, ct)
            .ConfigureAwait(false);
    }

    private async Task<string> ResolveSessionIdAsync(string workUnitId, string? explicitSessionId, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(explicitSessionId))
            return explicitSessionId;

        var pending = await scheduler.ListPendingAsync(ct).ConfigureAwait(false);
        var pendingSessionId = pending.FirstOrDefault(i => i.WorkUnitId == workUnitId)?.SessionId;
        if (!string.IsNullOrWhiteSpace(pendingSessionId))
            return pendingSessionId;

        // Fall back to the owning goal's session: walk ParentWorkUnitId to the root (fanned-out
        // children carry no goal node of their own), then match the goal node on WorkUnitId.
        if (goalNodes is not null)
        {
            var rootId = workUnitId;
            for (var depth = 0; depth < 32; depth++)
            {
                var unit = await workUnits.GetAsync(rootId, ct).ConfigureAwait(false);
                if (unit?.ParentWorkUnitId is not { } parentId)
                    break;
                rootId = parentId;
            }

            var goal = (await goalNodes.ListAsync(ct).ConfigureAwait(false))
                .FirstOrDefault(g => string.Equals(g.WorkUnitId, rootId, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(goal?.SessionId))
                return goal.SessionId;
        }

        // Never-null last resort: a synthetic per-work-unit session. A valid ExecutionEventStream
        // key like any other — everything that consumes ClarificationRequested/Responded events
        // queries by kind across sessions, so the request stays fully listable/answerable even
        // when nothing upstream carried a real session (see this file's RequestAsync comment).
        return $"wu-{workUnitId}";
    }

    private async Task TryUpdateStatusAsync(
        string workUnitId,
        WorkUnitStatus status,
        string? sessionId,
        CancellationToken ct)
    {
        try
        {
            var current = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false);
            if (current is not null && current.Status != status)
                await workUnits.UpdateStatusAsync(workUnitId, status, sessionId, ct).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Best-effort status signal; transition guardrail remains authoritative.
        }
    }

    private async Task<(string RequestId, string? SessionId)> ResolveRequestAsync(
        string workUnitId,
        string? requestId,
        string? sessionId,
        CancellationToken ct)
    {
        var requests = await BuildRequestMapAsync(ct).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(requestId))
        {
            if (!requests.TryGetValue(requestId, out var request) || request.WorkUnitId != workUnitId)
                throw new KeyNotFoundException($"Clarification request '{requestId}' was not found for work unit '{workUnitId}'.");

            if (request.Response is not null)
                throw new InvalidOperationException($"Clarification request '{requestId}' has already been answered.");

            return (request.RequestId, request.SessionId ?? sessionId);
        }

        var latest = requests.Values
            .Where(r => r.WorkUnitId == workUnitId && r.Response is null)
            .OrderByDescending(r => r.RequestedAt)
            .FirstOrDefault();

        if (latest is null)
            throw new KeyNotFoundException($"No active clarification request found for work unit '{workUnitId}'.");

        return (latest.RequestId, latest.SessionId ?? sessionId);
    }

    private async Task<Dictionary<string, ClarificationInboxItem>> BuildRequestMapAsync(CancellationToken ct)
    {
        var all = await events.GetEventsByKindAsync(
            [ExecutionEventKind.ClarificationRequested, ExecutionEventKind.ClarificationResponded],
            ct: ct).ConfigureAwait(false);

        var map = new Dictionary<string, ClarificationInboxItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var ev in all.OrderBy(e => e.OccurredAt))
        {
            if (ev.Kind == ExecutionEventKind.ClarificationRequested)
            {
                ClarificationRequestedPayload? payload;
                try { payload = JsonSerializer.Deserialize<ClarificationRequestedPayload>(ev.PayloadJson); }
                catch (JsonException) { continue; }
                if (payload is null)
                    continue;

                map[payload.RequestId] = new ClarificationInboxItem(
                    RequestId: payload.RequestId,
                    SessionId: ev.SessionId,
                    WorkUnitId: payload.WorkUnitId,
                    Goal: payload.WorkUnitId,
                    Question: payload.Question,
                    Context: payload.Context,
                    Blocking: payload.Blocking,
                    Options: payload.Options,
                    RequestedByAgentId: payload.RequestedByAgentId,
                    RequestedAt: payload.RequestedAt,
                    Status: "open",
                    Response: null,
                    ResponseNote: null,
                    RespondedBy: null,
                    RespondedAt: null,
                    AwaitingResume: false,
                    TimeoutSeconds: payload.TimeoutSeconds,
                    TimeoutAt: payload.TimeoutSeconds.HasValue ? payload.RequestedAt.AddSeconds(payload.TimeoutSeconds.Value) : null,
                    TimeoutBehavior: payload.TimeoutBehavior);
                continue;
            }

            ClarificationRespondedPayload? response;
            try { response = JsonSerializer.Deserialize<ClarificationRespondedPayload>(ev.PayloadJson); }
            catch (JsonException) { continue; }
            if (response is null)
                continue;

            if (!map.TryGetValue(response.RequestId, out var existing))
                continue;

            map[response.RequestId] = existing with
            {
                Status = response.Resumed ? "answered_resumed" : "answered",
                Response = response.Response,
                ResponseNote = response.Note,
                RespondedBy = response.RespondedBy,
                RespondedAt = response.RespondedAt,
            };
        }

        return map;
    }

    private async Task<string?> GetLatestRequestIdAsync(string sessionId, string workUnitId, CancellationToken ct)
    {
        var eventsForSession = await events.GetSessionEventsAsync(sessionId, ct: ct).ConfigureAwait(false);
        var latest = eventsForSession
            .Where(e => e.WorkUnitId == workUnitId && e.Kind == ExecutionEventKind.ClarificationRequested)
            .OrderByDescending(e => e.OccurredAt)
            .FirstOrDefault();

        if (latest is null)
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<ClarificationRequestedPayload>(latest.PayloadJson);
            return payload?.RequestId;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
