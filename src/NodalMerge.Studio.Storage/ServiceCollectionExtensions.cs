using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddNodalMergeStorage(this IServiceCollection services)
    {
        services.AddSingleton<IStudioNodeStore, NodalMergeStudioNodeStore>();
        services.AddSingleton<IBranchService, NodalMergeBranchService>();
        services.AddSingleton<IKnownGoodStateService, InMemoryKnownGoodStateService>();
        services.AddSingleton<IReplayService, StubReplayService>();
        services.AddSingleton<IAgentProfileService, AgentProfileService>();
        services.AddSingleton<IArtifactRefService, InMemoryArtifactRefService>();
        AddFileWorkspaceService(services);
        return services;
    }

    public static IServiceCollection AddInMemoryStorage(this IServiceCollection services)
    {
        services.AddSingleton<IStudioNodeStore, InMemoryStudioNodeStore>();
        services.AddSingleton<IBranchService, InMemoryBranchService>();
        services.AddSingleton<IKnownGoodStateService, InMemoryKnownGoodStateService>();
        services.AddSingleton<IReplayService, StubReplayService>();
        services.AddSingleton<IAgentProfileService, AgentProfileService>();
        services.AddSingleton<IArtifactRefService, InMemoryArtifactRefService>();
        AddFileWorkspaceService(services);
        return services;
    }

    private static void AddFileWorkspaceService(IServiceCollection services)
    {
        services.AddSingleton<IFileWorkspaceService>(sp =>
            new FileSystemWorkspaceService(sp.GetService<WorkspaceOptions>() ?? new WorkspaceOptions()));
    }
}

internal sealed class StubReplayService : IReplayService
{
    public Task<string> RangeAsync(
        string branchId,
        string? fromNode = null,
        string? toNode = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult($"{{\"branchId\":\"{branchId}\",\"note\":\"replay not yet wired to NodalMerge engine\"}}");

    public Task<string> RollbackAsync(string branchId, string knownGoodStateId, CancellationToken cancellationToken = default) =>
        Task.FromResult($"{{\"branchId\":\"{branchId}\",\"knownGoodStateId\":\"{knownGoodStateId}\",\"note\":\"rollback not yet wired to NodalMerge engine\"}}");

    public Task<string> InspectAsync(string branchId, string? nodeId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult($"{{\"branchId\":\"{branchId}\",\"note\":\"inspect not yet wired to NodalMerge engine\"}}");
}
