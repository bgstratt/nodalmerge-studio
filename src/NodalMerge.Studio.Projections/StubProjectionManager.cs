using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Projections;

public sealed class StubProjectionManager : IProjectionManager
{
    public Task<ProjectionResult> GetAsync(ProjectionRequest request, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new ProjectionGetResponseShape(
            request.Type.ToString(),
            request.Level.ToString(),
            new { status = "stub", workUnitId = request.WorkUnitId, branchId = request.BranchId }));

        return Task.FromResult(new ProjectionResult(
            request.Type,
            request.Level,
            payload,
            DateTimeOffset.UtcNow));
    }

    public Task<ProjectionResult> CompactAsync(
        ProjectionType type,
        ProjectionLevel targetLevel,
        CancellationToken cancellationToken = default) =>
        GetAsync(new ProjectionRequest(type, targetLevel), cancellationToken);

    private sealed record ProjectionGetResponseShape(string ProjectionType, string Level, object Data);
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddStudioProjections(this IServiceCollection services)
    {
        services.AddSingleton<IProjectionManager, StubProjectionManager>();
        return services;
    }
}
