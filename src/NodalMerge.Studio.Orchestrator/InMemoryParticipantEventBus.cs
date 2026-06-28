using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Orchestrator;

public sealed class InMemoryParticipantEventBus(ILogger<InMemoryParticipantEventBus> logger) : IParticipantEventBus
{
    private const int MaxHistory = 200;

    // eventType → list of (token, handler) pairs; list mutations are done under _handlersLock
    private readonly Dictionary<string, List<(Guid Token, Func<IDomainEvent, Task> Handler)>> _handlers = new();
    private readonly Lock _handlersLock = new();

    // Bounded ring buffer — oldest entry at index 0
    private readonly LinkedList<IDomainEvent> _history = new();
    private readonly Lock _historyLock = new();

    private static readonly IReadOnlyList<string> KnownEventTypes =
    [
        "WorkUnitStatusChanged",
        "ProposalCreated",
        "ReviewCompleted",
        "MergeAccepted",
        "ProjectionMaterialized",
    ];

    public void Publish(IDomainEvent domainEvent)
    {
        // Append to history
        lock (_historyLock)
        {
            _history.AddLast(domainEvent);
            while (_history.Count > MaxHistory)
                _history.RemoveFirst();
        }

        // Dispatch to subscribers — fire and forget, isolated per handler
        List<Func<IDomainEvent, Task>>? targets;
        lock (_handlersLock)
        {
            if (!_handlers.TryGetValue(domainEvent.EventType, out var entries))
                return;
            targets = entries.Select(e => e.Handler).ToList();
        }

        foreach (var handler in targets)
        {
            _ = Task.Run(async () =>
            {
                try { await handler(domainEvent).ConfigureAwait(false); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[ParticipantEventBus] Handler for {EventType} threw an exception", domainEvent.EventType);
                }
            });
        }
    }

    public IDisposable Subscribe(string eventType, Func<IDomainEvent, Task> handler)
    {
        var token = Guid.NewGuid();
        lock (_handlersLock)
        {
            if (!_handlers.TryGetValue(eventType, out var list))
                _handlers[eventType] = list = [];
            list.Add((token, handler));
        }
        return new Subscription(() =>
        {
            lock (_handlersLock)
            {
                if (_handlers.TryGetValue(eventType, out var list))
                    list.RemoveAll(e => e.Token == token);
            }
        });
    }

    public IReadOnlyList<IDomainEvent> GetRecentEvents(int limit = 50)
    {
        lock (_historyLock)
        {
            return _history.TakeLast(Math.Min(limit, MaxHistory)).ToList();
        }
    }

    public IReadOnlyList<string> GetRegisteredEventTypes() => KnownEventTypes;

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}
