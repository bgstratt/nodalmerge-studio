using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IDecisionNodeService
{
    Task<DecisionNode> RecordAsync(DecisionNode decision, CancellationToken ct = default);
    Task<IReadOnlyList<DecisionNode>> ListByWorkUnitAsync(string workUnitId, CancellationToken ct = default);
}

public sealed class DecisionNodeService : IDecisionNodeService, IRehydratable
{
    private readonly ConcurrentDictionary<string, DecisionNode> _decisions = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IWorkUnitService? _workUnits;
    private readonly IReasoningPublisher? _reasoningPublisher;

    // workUnits is optional/constructor-injected directly (not via lazy IServiceProvider) — safe
    // because, unlike IArtifactLineageService/IMergeService/IKnownGoodStateService,
    // InMemoryWorkUnitService does not itself depend on IDecisionNodeService, so no DI cycle exists.
    //
    // L2.3 — reasoningPublisher is an optional collaborator (same convention as the codebase's other
    // optional collaborators): when wired, RecordAsync publishes the work unit's reasoning transcript
    // to the CAS + a repo-scoped ConversationRef and links it via DecisionNode.ReasoningRefId, so a
    // same-repo peer can trace this decision → the reasoning behind it. Null → decisions record as
    // before (no ref).
    public DecisionNodeService(
        IStudioNodeStore nodeStore, IWorkUnitService? workUnits = null, IReasoningPublisher? reasoningPublisher = null)
    {
        _nodeStore = nodeStore;
        _workUnits = workUnits;
        _reasoningPublisher = reasoningPublisher;
    }

    public async Task<DecisionNode> RecordAsync(DecisionNode decision, CancellationToken ct = default)
    {
        // Slice 6.3a — denormalize from WorkUnitId's own RepositoryId when not already supplied.
        var stored = decision;
        if (stored.RepositoryId is null)
        {
            var repositoryId = await RepositoryIdResolution
                .ResolveFromWorkUnitAsync(_workUnits, stored.WorkUnitId, ct).ConfigureAwait(false);
            if (repositoryId is not null)
                stored = stored with { RepositoryId = repositoryId };
        }

        // L2.2b — DecisionV1 is repo-scoped (replicates), so cap the uncapped Rationale so a large
        // model-written rationale can't bloat the replication plane.
        stored = stored with { Rationale = NodePayloadLimits.Cap(stored.Rationale) };

        // L2.3 — publish the work unit's reasoning transcript (CAS blob + repo-scoped ConversationRef)
        // and link it, so a peer can trace this decision back to the reasoning behind it. Publishing
        // is best-effort: a publisher failure must not block recording the decision itself.
        if (_reasoningPublisher is not null && stored.ReasoningRefId is null)
        {
            var cref = await _reasoningPublisher.PublishAsync(
                stored.WorkUnitId, stored.SessionId, stored.RepositoryId, decisionId: stored.DecisionId,
                cancellationToken: ct)
                .ConfigureAwait(false);
            if (cref is not null)
                stored = stored with { ReasoningRefId = cref.RefId };
        }

        _decisions[stored.DecisionId] = stored;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.DecisionV1, stored.DecisionId, JsonSerializer.Serialize(stored), stored.RepositoryId, ct)
            .ConfigureAwait(false);
        return stored;
    }

    // Slice 6.5 Part 1 — see InMemoryWorkUnitService.RehydratedKinds' doc comment.
    public IReadOnlyCollection<string> RehydratedKinds => [StudioNodeKind.DecisionV1];

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.DecisionV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var decision = JsonSerializer.Deserialize<DecisionNode>(payloadJson);
            if (decision is not null) _decisions[decision.DecisionId] = decision;
        }
    }

    public Task<IReadOnlyList<DecisionNode>> ListByWorkUnitAsync(string workUnitId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DecisionNode>>(
            _decisions.Values.Where(d => d.WorkUnitId == workUnitId).OrderByDescending(d => d.DecidedAt).ToList());
}