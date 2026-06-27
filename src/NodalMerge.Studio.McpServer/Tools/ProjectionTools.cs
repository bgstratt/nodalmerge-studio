using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ProjectionTools(IProjectionManager projections, IProjectionSnapshotService snapshots)
{
    [McpServerTool(Name = McpToolNames.ProjectionGet), Description("Get a projection by type and compression level.")]
    public async Task<string> GetAsync(
        string projectionType,
        string projectionLevel = "Normal",
        string? workUnitId = null,
        string? branchId = null,
        string? agentId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ProjectionType>(projectionType, ignoreCase: true, out var type) ||
            !Enum.TryParse<ProjectionLevel>(projectionLevel, ignoreCase: true, out var level))
        {
            return McpJson.Error(McpToolNames.ProjectionGet, "Invalid projectionType or projectionLevel.");
        }

        var result = await projections.GetAsync(
            new ProjectionRequest(type, level, workUnitId, branchId, agentId),
            cancellationToken).ConfigureAwait(false);

        return JsonSerializer.Serialize(new
        {
            contractVersion = McpContract.Version,
            projectionType = result.Type.ToString(),
            level = result.Level.ToString(),
            data = JsonSerializer.Deserialize<object>(result.DataJson)
        });
    }

    [McpServerTool(Name = McpToolNames.ProjectionList), Description("List available projection types and levels.")]
    public Task<string> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(McpJson.Ok(new
        {
            types = ProjectionCatalog.Types,
            levels = ProjectionCatalog.Levels
        }));

    [McpServerTool(Name = McpToolNames.ProjectionSnapshotCapture), Description("Persist an immutable, versioned capture of a work unit's current AgentWorkspace projection.")]
    public async Task<string> SnapshotCaptureAsync(string workUnitId, CancellationToken cancellationToken = default)
    {
        var snapshot = await snapshots.CaptureAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { snapshotId = snapshot.SnapshotId, workUnitId = snapshot.WorkUnitId, createdAt = snapshot.CreatedAt });
    }

    [McpServerTool(Name = McpToolNames.ProjectionSnapshotGet), Description("Get a persisted projection snapshot by id.")]
    public async Task<string> SnapshotGetAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        var snapshot = await snapshots.GetAsync(snapshotId, cancellationToken).ConfigureAwait(false);
        return snapshot is null
            ? McpJson.Error(McpToolNames.ProjectionSnapshotGet, $"Projection snapshot '{snapshotId}' was not found.")
            : McpJson.Ok(snapshot);
    }

    [McpServerTool(Name = McpToolNames.ProjectionSnapshotList), Description("List persisted projection snapshots, optionally filtered by work unit.")]
    public async Task<string> SnapshotListAsync(string? workUnitId = null, CancellationToken cancellationToken = default)
    {
        var list = await snapshots.ListAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(list);
    }

    [McpServerTool(Name = McpToolNames.ProjectionStaleCheck), Description("Check whether a projection snapshot is stale because an artifact it referenced has since been invalidated.")]
    public async Task<string> StaleCheckAsync(string snapshotId, CancellationToken cancellationToken = default)
    {
        try
        {
            var staleness = await snapshots.CheckStaleAsync(snapshotId, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(staleness);
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpToolNames.ProjectionStaleCheck, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.ProjectionCompare), Description("Compare two projection snapshots (e.g. sibling forks) as a symmetric set difference over their artifacts — distinct from sequential delta, which only handles the same work unit over time.")]
    public async Task<string> CompareAsync(string snapshotIdA, string snapshotIdB, CancellationToken cancellationToken = default)
    {
        try
        {
            var comparison = await snapshots.CompareAsync(snapshotIdA, snapshotIdB, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(comparison);
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpToolNames.ProjectionCompare, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.ProjectionMaterialize), Description("Run a build/test/lint execution against a work unit's branch and immediately capture a projection snapshot of the result — one atomic 'run it and freeze the result' call.")]
    public async Task<string> MaterializeAsync(
        string workUnitId,
        bool build = true,
        bool test = true,
        bool lint = false,
        string? buildCommand = null,
        string? testCommand = null,
        string? lintCommand = null,
        int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new WorkspaceExecutionRequest(
                Build: build,
                Test: test,
                Lint: lint,
                BuildCommand: buildCommand,
                TestCommand: testCommand,
                LintCommand: lintCommand,
                TimeoutSeconds: timeoutSeconds);
            var materialization = await snapshots.MaterializeAsync(workUnitId, request, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(materialization);
        }
        catch (KeyNotFoundException ex)
        {
            return McpJson.Error(McpToolNames.ProjectionMaterialize, ex.Message);
        }
    }
}
