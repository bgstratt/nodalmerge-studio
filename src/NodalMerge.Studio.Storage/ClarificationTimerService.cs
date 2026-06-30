using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IClarificationTimerService
{
    Task ProcessExpiredAsync(CancellationToken ct = default);
}

/// <summary>
/// Polled on each scheduler tick. Finds blocking clarification requests whose timeoutSeconds
/// has elapsed and auto-responds based on timeoutBehavior: "auto_continue" (default) resumes
/// the agent with the defaultResponse or a system message; "auto_abandon" closes the request
/// without resuming; "use_default" is identical to auto_continue.
/// </summary>
public sealed class ClarificationTimerService(
    IExecutionEventStream events,
    IClarificationCommandService clarifications) : IClarificationTimerService
{
    public async Task ProcessExpiredAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;

        var allEvents = await events.GetEventsByKindAsync(
            [ExecutionEventKind.ClarificationRequested, ExecutionEventKind.ClarificationResponded],
            ct: ct).ConfigureAwait(false);

        var answered = allEvents
            .Where(e => e.Kind == ExecutionEventKind.ClarificationResponded)
            .Select(e =>
            {
                try { return JsonSerializer.Deserialize<ClarificationRespondedPayload>(e.PayloadJson)?.RequestId; }
                catch { return null; }
            })
            .Where(id => id is not null)
            .ToHashSet(StringComparer.OrdinalIgnoreCase)!;

        foreach (var ev in allEvents.Where(e => e.Kind == ExecutionEventKind.ClarificationRequested))
        {
            ClarificationRequestedPayload? payload;
            try { payload = JsonSerializer.Deserialize<ClarificationRequestedPayload>(ev.PayloadJson); }
            catch { continue; }
            if (payload is null || payload.TimeoutSeconds is null) continue;
            if (answered.Contains(payload.RequestId)) continue;

            var expiresAt = payload.RequestedAt.AddSeconds(payload.TimeoutSeconds.Value);
            if (now < expiresAt) continue;

            var behavior = payload.TimeoutBehavior ?? "auto_continue";
            var resume = !behavior.Equals("auto_abandon", StringComparison.OrdinalIgnoreCase);
            var responseText = payload.DefaultResponse
                ?? (resume
                    ? "Proceeding automatically — no human response received within the timeout window."
                    : "Request timed out. Abandoning work unit.");

            try
            {
                await clarifications.RespondAsync(
                    payload.WorkUnitId,
                    responseText,
                    note: $"Auto-{(resume ? "continued" : "abandoned")} due to {payload.TimeoutSeconds}s timeout.",
                    respondedBy: "system",
                    requestId: payload.RequestId,
                    resume: resume,
                    ct: ct).ConfigureAwait(false);
            }
            catch { /* best-effort: avoid crashing the scheduler tick */ }
        }
    }
}
