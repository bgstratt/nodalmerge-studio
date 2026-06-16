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
}

public interface IStudioNodeStore
{
    Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken cancellationToken = default);

    Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken cancellationToken = default);
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
}
