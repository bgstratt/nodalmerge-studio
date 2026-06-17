using System.Collections.Concurrent;

namespace NodalMerge.Studio.Storage;

public static class StudioNodeKind
{
    public const string WorkUnitV1 = "studio/work-unit/v1";
    public const string TaskV1 = "studio/task/v1";
    public const string MergeProposalV1 = "studio/merge-proposal/v1";
    public const string KnownGoodStateV1 = "studio/known-good-state/v1";
    public const string BranchV1 = "studio/branch/v1";
    public const string AgentProfileV1 = "studio/agent-profile/v1";
    public const string SchedulerV1 = "studio/scheduler/v1";
    public const string ExecutionSessionV1 = "studio/execution-session/v1";
    public const string ExecutionEventV1    = "studio/execution-event/v1";
    public const string CommandResultV1     = "studio/command-result/v1";
    public const string AgentWorkspaceV1    = "studio/agent-workspace/v1";
    public const string ArtifactRefV1       = "studio/artifact-ref/v1";
    public const string OrchestrationEventV1 = "studio/orchestration-event/v1";
    public const string ChangeIntentV1       = "studio/change-intent/v1";
    public const string DeadLetterV1         = "studio/dead-letter/v1";
    public const string RuntimeSettingsV1    = "studio/runtime-settings/v1";
}

public interface IStudioNodeStore
{
    Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default);

    Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default);

    // Slice 0a — rehydration. Returns the latest payload per entityId for every node of this
    // kind, so a service can rebuild its in-memory dictionary on startup from what was already
    // durably written via WriteNodeAsync.
    Task<IReadOnlyList<(string EntityId, string PayloadJson)>> ReadAllNodesAsync(
        string kind, CancellationToken cancellationToken = default);
}

public sealed class InMemoryStudioNodeStore : IStudioNodeStore
{
    private readonly ConcurrentDictionary<(string Kind, string EntityId), string> _nodes = new();

    public Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default)
    {
        _nodes[(kind, entityId)] = payloadJson;
        return Task.CompletedTask;
    }

    public Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default)
    {
        _nodes.TryGetValue((kind, entityId), out var payload);
        return Task.FromResult(payload);
    }

    public Task<IReadOnlyList<(string EntityId, string PayloadJson)>> ReadAllNodesAsync(
        string kind, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<(string EntityId, string PayloadJson)> results = _nodes
            .Where(n => n.Key.Kind == kind)
            .Select(n => (n.Key.EntityId, n.Value))
            .ToList();
        return Task.FromResult(results);
    }
}
