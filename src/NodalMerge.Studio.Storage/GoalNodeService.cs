using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public interface IGoalNodeService
{
    Task<GoalNode> RecordAsync(GoalNode goal, CancellationToken ct = default);
    Task<GoalNode?> GetAsync(string goalId, CancellationToken ct = default);
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
        // #1 goal replication (plans/repo-identity-convergence.md) — route via the repo-scoped
        // overload using the goal's denormalized RepositoryId, so a goal bound to a repo lands in
        // repo/{repoId}'s room and replicates to peers on the same repo. A null RepositoryId falls
        // back to the "studio" room (the overload's own default), the pre-#1 behavior.
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.GoalV1, goal.GoalId, JsonSerializer.Serialize(goal), goal.RepositoryId, ct).ConfigureAwait(false);
        return goal;
    }

    // #1 — GoalV1 is now repo-scoped (StudioNodeStore.RepoScopedKinds), so an inbound pack on a
    // repo room must re-read this cache (RehydratableRefreshCoordinator gates the refresh on this).
    public IReadOnlyCollection<string> RehydratedKinds => [StudioNodeKind.GoalV1];

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.GoalV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var goal = JsonSerializer.Deserialize<GoalNode>(payloadJson);
            if (goal is not null) _goals[goal.GoalId] = goal;
        }
    }

    public Task<GoalNode?> GetAsync(string goalId, CancellationToken ct = default) =>
        Task.FromResult(_goals.TryGetValue(goalId, out var goal) ? goal : null);

    public Task<IReadOnlyList<GoalNode>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<GoalNode>>(_goals.Values.OrderByDescending(g => g.CreatedAt).ToList());
}