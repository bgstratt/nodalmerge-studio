using System.Text;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge;

// Phase 10 — orchestrates the merge strategy chain for a recorded RepositoryConflict.
// Reads blob content from CAS, tries strategies in registration order (or a named strategy),
// writes the merged blob back to CAS, emits a RepositoryOperation, and marks the conflict Resolved.
public sealed class ConflictResolutionService(
    IConflictService conflicts,
    IRepositoryOpService repositoryOps,
    IEnumerable<IMergeStrategy> strategies,
    IBlobStoreProvider? blobStore = null) : IConflictResolutionService
{
    public async Task<ConflictResolutionResult> ResolveAsync(
        string conflictId, string? preferredStrategy = null,
        LlmMergeCredentials? llmCredentials = null, CancellationToken ct = default)
    {
        var conflict = await conflicts.GetAsync(conflictId, ct).ConfigureAwait(false);
        if (conflict is null)
            return new ConflictResolutionResult(false, conflictId, preferredStrategy ?? "auto",
                FailureReason: $"Conflict '{conflictId}' not found.");

        if (conflict.Status != ConflictStatus.Open)
            return new ConflictResolutionResult(false, conflictId, preferredStrategy ?? "auto",
                FailureReason: $"Conflict is already {conflict.Status}.");

        // Read blob content. Null content = file was deleted by that op (Delete kind).
        var baseContent  = await ReadBlobAsync(conflict.OldBlobId, ct).ConfigureAwait(false);
        var contentA     = await ReadBlobAsync(conflict.BlobIdA, ct).ConfigureAwait(false);
        var contentB     = await ReadBlobAsync(conflict.BlobIdB, ct).ConfigureAwait(false);

        var context = new MergeContext(
            conflictId, conflict.RepositoryId, conflict.Path,
            baseContent, contentA, contentB, llmCredentials);

        // Select and run strategies.
        var strategyList = strategies.ToList();
        if (preferredStrategy is not null)
            strategyList = strategyList.Where(s => s.Name == preferredStrategy).ToList();

        MergeStrategyResult? lastResult = null;
        foreach (var strategy in strategyList)
        {
            lastResult = await strategy.MergeAsync(context, ct).ConfigureAwait(false);
            if (lastResult.Success && lastResult.MergedContent is not null)
            {
                var opId = await CommitMergeAsync(conflict, lastResult.MergedContent, ct)
                    .ConfigureAwait(false);
                await conflicts.MarkResolvedAsync(conflictId, opId, ct).ConfigureAwait(false);
                return new ConflictResolutionResult(true, conflictId, lastResult.StrategyName, opId);
            }
        }

        var reason = lastResult?.FailureReason ?? "No strategies available.";
        // Signal the caller to re-queue the losing work unit (WorkUnitIdB) when all strategies
        // decline. The caller applies WorkspaceOptions.AllowAutoRequeue to decide whether to act.
        // Not a deletion conflict? Safe to requeue. Deletion conflicts require human judgment.
        var safeToRequeue = conflict.BlobIdB is not null; // BlobIdB = null means one side deleted
        return new ConflictResolutionResult(false, conflictId, preferredStrategy ?? "auto",
            FailureReason: reason, RequeueLosingWorkUnit: safeToRequeue);
    }

    // Write the merged content to CAS and emit a resolution RepositoryOperation.
    private async Task<string> CommitMergeAsync(
        RepositoryConflict conflict, string mergedContent, CancellationToken ct)
    {
        var bytes   = Encoding.UTF8.GetBytes(mergedContent);
        var hashHex = BlobHasher.ComputeHash(bytes);

        if (blobStore is not null)
            await blobStore.PutBlobAsync(hashHex, bytes, "text/plain", ct).ConfigureAwait(false);

        var opId = $"res-{Guid.NewGuid():N}";
        var op = new RepositoryOperation(
            OperationId:      opId,
            RepositoryId:     conflict.RepositoryId,
            ParentSnapshotId: null,
            Kind:             OperationType.Replace,
            Path:             conflict.Path,
            Timestamp:        DateTimeOffset.UtcNow,
            OldBlobId:        conflict.OldBlobId,
            NewBlobId:        hashHex,
            Reason:           $"Conflict resolution for {conflict.ConflictId}");

        await repositoryOps.EmitAsync(op, ct).ConfigureAwait(false);
        return opId;
    }

    private async Task<string?> ReadBlobAsync(string? blobId, CancellationToken ct)
    {
        if (blobId is null || blobStore is null) return null;
        var result = await blobStore.TryGetBlobAsync(blobId, ct).ConfigureAwait(false);
        return result.Found && result.Bytes is not null
            ? Encoding.UTF8.GetString(result.Bytes)
            : null;
    }
}
