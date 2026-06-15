using System.Text.Json;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Projections;

// Kept for use in tests that need a zero-dependency IProjectionManager.
internal sealed class StubProjectionManager : IProjectionManager
{
    public Task<ProjectionResult> GetAsync(ProjectionRequest request, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            projectionType = request.Type.ToString(),
            level = request.Level.ToString(),
            data = new { status = "stub" }
        });

        return Task.FromResult(new ProjectionResult(request.Type, request.Level, payload, DateTimeOffset.UtcNow));
    }

    public Task<ProjectionResult> CompactAsync(
        ProjectionType type,
        ProjectionLevel targetLevel,
        CancellationToken cancellationToken = default) =>
        GetAsync(new ProjectionRequest(type, targetLevel), cancellationToken);
}
