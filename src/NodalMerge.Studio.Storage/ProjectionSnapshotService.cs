using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class ProjectionSnapshotService : IProjectionSnapshotService, IRehydratable
{
    private readonly ConcurrentDictionary<string, ProjectionSnapshot> _snapshots = new();
    private readonly IStudioNodeStore _nodeStore;
    private readonly IProjectionManager _projections;
    private readonly IArtifactLineageService _artifacts;
    private readonly IWorkUnitService _workUnits;
    private readonly IWorkspaceExecutionCommandService _execution;

    public ProjectionSnapshotService(
        IStudioNodeStore nodeStore,
        IProjectionManager projections,
        IArtifactLineageService artifacts,
        IWorkUnitService workUnits,
        IWorkspaceExecutionCommandService execution)
    {
        _nodeStore = nodeStore;
        _projections = projections;
        _artifacts = artifacts;
        _workUnits = workUnits;
        _execution = execution;
    }

    public async Task<ProjectionSnapshot> CaptureAsync(string workUnitId, CancellationToken ct = default)
    {
        var result = await _projections.GetAsync(
            new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Full, WorkUnitId: workUnitId),
            ct).ConfigureAwait(false);
        // ProjectionManager serializes DataJson with JsonSerializerOptions.Web (camelCase) — match
        // that here, since the default Deserialize<T> options are case-sensitive PascalCase and
        // would silently leave every property null instead of throwing.
        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(result.DataJson, JsonSerializerOptions.Web)
            ?? throw new InvalidOperationException($"Projection for work unit '{workUnitId}' resolved to an empty payload.");

        var snapshot = new ProjectionSnapshot(
            SnapshotId: $"PROJ-{Guid.NewGuid():N}",
            WorkUnitId: workUnitId,
            Payload: payload,
            CreatedAt: DateTimeOffset.UtcNow);

        _snapshots[snapshot.SnapshotId] = snapshot;
        await _nodeStore.WriteNodeAsync(
            StudioNodeKind.ProjectionSnapshotV1, snapshot.SnapshotId, JsonSerializer.Serialize(snapshot), ct)
            .ConfigureAwait(false);

        return snapshot;
    }

    public async Task<ProjectionMaterializationResult> MaterializeAsync(
        string workUnitId, WorkspaceExecutionRequest? request = null, CancellationToken ct = default)
    {
        var workUnit = await _workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Work unit '{workUnitId}' was not found.");

        var result = await _execution.ExecAsync(
            workUnit.BranchId, request ?? new WorkspaceExecutionRequest(Build: true, Test: true), ct)
            .ConfigureAwait(false);

        var snapshot = await CaptureAsync(workUnitId, ct).ConfigureAwait(false);
        return new ProjectionMaterializationResult(result, snapshot);
    }

    public Task<ProjectionSnapshot?> GetAsync(string snapshotId, CancellationToken ct = default)
    {
        _snapshots.TryGetValue(snapshotId, out var snapshot);
        return Task.FromResult(snapshot);
    }

    public Task<IReadOnlyList<ProjectionSnapshot>> ListAsync(string? workUnitId = null, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ProjectionSnapshot>>(
            _snapshots.Values
                .Where(s => workUnitId is null || s.WorkUnitId == workUnitId)
                .OrderBy(s => s.CreatedAt)
                .ToList());

    public async Task<ProjectionStaleness> CheckStaleAsync(string snapshotId, CancellationToken ct = default)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var snapshot))
            throw new KeyNotFoundException($"Projection snapshot '{snapshotId}' was not found.");

        var referencedIds = snapshot.Payload.Artifacts.Artifacts
            .Concat(snapshot.Payload.InheritedConstraints)
            .Select(a => a.ArtifactId)
            .Distinct()
            .ToList();

        var staleIds = new List<string>();
        foreach (var artifactId in referencedIds)
        {
            var current = await _artifacts.GetAsync(artifactId, ct).ConfigureAwait(false);
            if (current is { } a && (a.Status == ArtifactStatus.Invalidated || a.InvalidatedByArtifactId is not null))
                staleIds.Add(artifactId);
        }

        return new ProjectionStaleness(snapshotId, staleIds.Count > 0, staleIds);
    }

    public async Task<ProjectionComparison> CompareAsync(
        string snapshotIdA, string snapshotIdB, CancellationToken ct = default)
    {
        if (!_snapshots.TryGetValue(snapshotIdA, out var a))
            throw new KeyNotFoundException($"Projection snapshot '{snapshotIdA}' was not found.");
        if (!_snapshots.TryGetValue(snapshotIdB, out var b))
            throw new KeyNotFoundException($"Projection snapshot '{snapshotIdB}' was not found.");

        return ProjectionComparison.Compute(snapshotIdA, snapshotIdB, a.Payload, b.Payload);
    }

    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore
            .ReadAllNodesAsync(StudioNodeKind.ProjectionSnapshotV1, cancellationToken).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var snapshot = JsonSerializer.Deserialize<ProjectionSnapshot>(payloadJson);
            if (snapshot is not null)
                _snapshots[snapshot.SnapshotId] = snapshot;
        }
    }
}
