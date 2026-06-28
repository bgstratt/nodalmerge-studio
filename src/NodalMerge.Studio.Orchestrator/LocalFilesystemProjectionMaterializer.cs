using System.Diagnostics;
using Microsoft.Extensions.Logging;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Orchestrator;

/// <summary>
/// Writes branch files to the local filesystem target, captures a ProjectionSnapshot, and
/// publishes ProjectionMaterializedEvent. Auto-triggers when a work unit reaches Proposed status
/// by subscribing to WorkUnitStatusChanged on the IParticipantEventBus.
/// </summary>
public sealed class LocalFilesystemProjectionMaterializer : IProjectionMaterializer, IDisposable
{
    private const string TargetKind = "LocalFilesystem";

    private readonly IWorkUnitService _workUnits;
    private readonly IFileWorkspaceService _fileWorkspace;
    private readonly IProjectionSnapshotService _snapshots;
    private readonly IParticipantEventBus _eventBus;
    private readonly WorkspaceOptions _options;
    private readonly ILogger<LocalFilesystemProjectionMaterializer> _logger;
    private readonly IDisposable _subscription;

    public LocalFilesystemProjectionMaterializer(
        IWorkUnitService workUnits,
        IFileWorkspaceService fileWorkspace,
        IProjectionSnapshotService snapshots,
        IParticipantEventBus eventBus,
        WorkspaceOptions options,
        ILogger<LocalFilesystemProjectionMaterializer> logger)
    {
        _workUnits    = workUnits;
        _fileWorkspace = fileWorkspace;
        _snapshots    = snapshots;
        _eventBus     = eventBus;
        _options      = options;
        _logger       = logger;

        // Reactively materialize whenever a work unit is proposed — no direct dependency on
        // InMemoryWorkUnitService, so no circular constructor graph.
        _subscription = eventBus.Subscribe("WorkUnitStatusChanged", HandleStatusChangedAsync);
    }

    private async Task HandleStatusChangedAsync(IDomainEvent domainEvent)
    {
        if (domainEvent is not WorkUnitStatusChangedEvent { NewStatus: WorkUnitStatus.Proposed } ev)
            return;
        if (string.IsNullOrWhiteSpace(_options.SeedRepositoryPath))
            return; // no configured local target — skip auto-materialization

        try
        {
            await MaterializeAsync(ev.WorkUnitId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ProjectionMaterializer] Auto-materialization failed for work unit {WorkUnitId}", ev.WorkUnitId);
        }
    }

    public async Task<MaterializationResult> MaterializeAsync(
        string workUnitId,
        string? targetPath = null,
        CancellationToken ct = default)
    {
        var workUnit = await _workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Work unit '{workUnitId}' not found.");

        var effectivePath = targetPath ?? _options.SeedRepositoryPath;
        if (string.IsNullOrWhiteSpace(effectivePath))
            throw new InvalidOperationException(
                $"No target path configured for materialization of work unit '{workUnitId}'. " +
                "Pass an explicit targetPath or configure WorkspaceOptions.SeedRepositoryPath.");

        var sw = Stopwatch.StartNew();
        string? error = null;
        var fileCount = 0;

        try
        {
            var files = await _fileWorkspace.ListAsync(workUnit.BranchId, ct: ct).ConfigureAwait(false);
            foreach (var relativePath in files)
            {
                var content = await _fileWorkspace.ReadAsync(workUnit.BranchId, relativePath, ct).ConfigureAwait(false);
                if (content is null) continue;

                var dest = Path.Combine(effectivePath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var destDir = Path.GetDirectoryName(dest)!;
                if (!Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);

                await File.WriteAllTextAsync(dest, content, ct).ConfigureAwait(false);
                fileCount++;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "[ProjectionMaterializer] Failed writing files for work unit {WorkUnitId} to {TargetPath}", workUnitId, effectivePath);
        }

        var snapshot = await _snapshots.CaptureAsync(workUnitId, ct).ConfigureAwait(false);
        sw.Stop();

        _eventBus.Publish(new ProjectionMaterializedEvent(
            snapshot.SnapshotId, TargetKind, fileCount, sw.ElapsedMilliseconds, workUnitId, DateTimeOffset.UtcNow));

        return new MaterializationResult(
            workUnitId, snapshot.SnapshotId, TargetKind, effectivePath,
            fileCount, sw.ElapsedMilliseconds, Succeeded: error is null, Error: error);
    }

    public void Dispose() => _subscription.Dispose();
}
