using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.AgentRuntime;

public sealed class InMemoryAgentRuntimeService : IAgentRuntimeService, ISnapshotService, IAgentControlService
{
    private readonly ConcurrentDictionary<(string AgentId, string WorkUnitId), ExecutionSnapshot> _snapshots = new();
    private readonly ConcurrentDictionary<string, string> _agentStatus = new();

    public Task<ExecutionSnapshot> GetSnapshotAsync(
        string agentId,
        string workUnitId,
        CancellationToken cancellationToken = default)
    {
        _snapshots.TryGetValue((agentId, workUnitId), out var snapshot);
        snapshot ??= new ExecutionSnapshot(
            agentId,
            workUnitId,
            null,
            null,
            null,
            [],
            [],
            0,
            0,
            null);

        return Task.FromResult(snapshot);
    }

    public Task RecordActionAsync(
        string agentId,
        string workUnitId,
        string action,
        CancellationToken cancellationToken = default)
    {
        var key = (agentId, workUnitId);
        var current = _snapshots.GetOrAdd(
            key,
            _ => new ExecutionSnapshot(agentId, workUnitId, null, null, null, [], [], 0, 0, null));

        var actions = current.RecentActions.ToList();
        actions.Add(action);
        _snapshots[key] = current with { RecentActions = actions };
        return Task.CompletedTask;
    }

    Task<ExecutionSnapshot> ISnapshotService.GetAsync(
        string agentId,
        string workUnitId,
        CancellationToken cancellationToken) =>
        GetSnapshotAsync(agentId, workUnitId, cancellationToken);

    public Task<string> CompareAsync(
        string agentId,
        string workUnitId,
        string otherAgentId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("[]");

    public Task<string> SpawnAsync(string agentType, string workUnitId, CancellationToken cancellationToken = default)
    {
        var agentId = $"{agentType}-{Guid.NewGuid():N}";
        _agentStatus[agentId] = "active";
        return Task.FromResult(agentId);
    }

    public Task PauseAsync(string agentId, CancellationToken cancellationToken = default)
    {
        _agentStatus[agentId] = "paused";
        return Task.CompletedTask;
    }

    public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default)
    {
        _agentStatus[agentId] = "active";
        return Task.CompletedTask;
    }

    public Task StopAsync(string agentId, CancellationToken cancellationToken = default)
    {
        _agentStatus[agentId] = "stopped";
        return Task.CompletedTask;
    }

    public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default)
    {
        _agentStatus.TryGetValue(agentId, out var status);
        return Task.FromResult(status ?? "unknown");
    }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioAgentRuntime(this IServiceCollection services)
    {
        services.AddSingleton<InMemoryAgentRuntimeService>();
        services.AddSingleton<IAgentRuntimeService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<ISnapshotService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        services.AddSingleton<IAgentControlService>(sp => sp.GetRequiredService<InMemoryAgentRuntimeService>());
        return services;
    }
}
