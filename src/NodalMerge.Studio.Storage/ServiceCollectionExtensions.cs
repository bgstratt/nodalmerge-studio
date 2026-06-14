using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioStorage(this IServiceCollection services)
    {
        services.AddSingleton<IStudioNodeStore, InMemoryStudioNodeStore>();
        services.AddSingleton<IBranchService, StubBranchService>();
        services.AddSingleton<IKnownGoodStateService, StubKnownGoodStateService>();
        services.AddSingleton<IReplayService, StubReplayService>();
        return services;
    }
}

internal sealed class StubBranchService : IBranchService
{
    public Task<string> CreateBranchAsync(string name, string? fromBranchId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(name);

    public Task CheckoutBranchAsync(string branchId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<string>>([]);

    public Task<BranchStatus> GetStatusAsync(string branchId, CancellationToken cancellationToken = default) =>
        Task.FromResult(new BranchStatus(branchId, "active", 0));
}

internal sealed class StubKnownGoodStateService : IKnownGoodStateService
{
    public Task<KnownGoodState> MarkKnownGoodAsync(
        KnownGoodState state,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(state);

    public Task<IReadOnlyList<KnownGoodState>> FindKnownGoodAsync(
        string branchId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<KnownGoodState>>([]);

    public Task<KnownGoodState?> CheckoutKnownGoodAsync(
        string stateId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<KnownGoodState?>(null);
}

internal sealed class StubReplayService : IReplayService
{
    public Task<string> RangeAsync(
        string branchId,
        string? fromNode = null,
        string? toNode = null,
        CancellationToken cancellationToken = default) =>
        Task.FromResult("{}");

    public Task<string> RollbackAsync(string branchId, string knownGoodStateId, CancellationToken cancellationToken = default) =>
        Task.FromResult("{}");

    public Task<string> InspectAsync(string branchId, string? nodeId = null, CancellationToken cancellationToken = default) =>
        Task.FromResult("{}");
}
