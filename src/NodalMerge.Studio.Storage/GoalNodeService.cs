using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IGoalNodeService
{
    Task<GoalNode> RecordAsync(GoalNode goal, CancellationToken ct = default);
    Task<IReadOnlyList<GoalNode>> ListAsync(CancellationToken ct = default);
}

public sealed class GoalNodeService : IGoalNodeService, IRehydratable
{
    private readonly ConcurrentDictionary<string, GoalNode> _goals = new();
    private readonly IStudioNodeStore _nodeStore;

    public GoalNodeService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
    }

    public async Task<GoalNode> RecordAsync(GoalNode goal, CancellationToken ct = default)
    {
        _goals[goal.GoalId] = goal;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.GoalV1, goal.GoalId, JsonSerializer.Serialize(goal), ct).ConfigureAwait(false);
        return goal;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.GoalV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var goal = JsonSerializer.Deserialize<GoalNode>(payloadJson);
            if (goal is not null) _goals[goal.GoalId] = goal;
        }
    }

    public Task<IReadOnlyList<GoalNode>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GoalNode>>(_goals.Values.OrderByDescending(g => g.CreatedAt).ToList());
}