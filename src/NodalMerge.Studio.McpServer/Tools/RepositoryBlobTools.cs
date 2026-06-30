using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

// Phase 12 — CAS-backed repository tools for the HTTP MCP server.
// Mirrors the McpToolDispatcher blob handlers so external harnesses (Copilot, Cline, etc.)
// have parity with internally spawned agents.
public sealed class RepositoryBlobTools(
    IRepositorySnapshotService repoSnapshots,
    IBlobStoreProvider? repoBlobStore = null,
    IRepositoryOpService? repoOpEmitter = null)
{
    [McpServerTool(Name = McpToolNames.RepositoryBlobList), Description("List files in the latest CAS snapshot for a repository. Returns path→blobId entries from the snapshot's tree.")]
    public async Task<string> BlobListAsync(string repositoryId, CancellationToken cancellationToken = default)
    {
        var snapshot = await repoSnapshots.GetLatestAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
            return McpJson.Error(McpToolNames.RepositoryBlobList, $"No snapshot found for repository '{repositoryId}'.");

        var entries = snapshot.TreeEntries ?? new Dictionary<string, string>();
        return McpJson.Ok(new { repositoryId, snapshotId = snapshot.SnapshotId, files = entries });
    }

    [McpServerTool(Name = McpToolNames.RepositoryBlobRead), Description("Read a file's content from CAS by path, resolved against the latest snapshot for the repository.")]
    public async Task<string> BlobReadAsync(string repositoryId, string path, CancellationToken cancellationToken = default)
    {
        if (repoBlobStore is null)
            return McpJson.Error(McpToolNames.RepositoryBlobRead, "Blob store is not configured.");

        var snapshot = await repoSnapshots.GetLatestAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        if (snapshot?.TreeEntries is null || !snapshot.TreeEntries.TryGetValue(path, out var blobId))
            return McpJson.Error(McpToolNames.RepositoryBlobRead, $"Path '{path}' not found in latest snapshot for repository '{repositoryId}'.");

        var result = await repoBlobStore.TryGetBlobAsync(blobId, cancellationToken).ConfigureAwait(false);
        if (!result.Found || result.Bytes is null)
            return McpJson.Error(McpToolNames.RepositoryBlobRead, $"Blob '{blobId}' not found in CAS.");

        var content = Encoding.UTF8.GetString(result.Bytes);
        return McpJson.Ok(new { repositoryId, path, blobId, content });
    }

    [McpServerTool(Name = McpToolNames.RepositoryBlobWrite), Description("Write content to a path in the CAS-backed repository. Computes a Blake3 hash, stores the blob, emits a Replace/Add RepositoryOperation, and returns the new blobId.")]
    public async Task<string> BlobWriteAsync(string repositoryId, string path, string content, CancellationToken cancellationToken = default)
    {
        if (repoBlobStore is null)
            return McpJson.Error(McpToolNames.RepositoryBlobWrite, "Blob store is not configured.");
        if (repoOpEmitter is null)
            return McpJson.Error(McpToolNames.RepositoryBlobWrite, "Repository op service is not configured.");

        var bytes  = Encoding.UTF8.GetBytes(content);
        var blobId = BlobHasher.ComputeHash(bytes);
        await repoBlobStore.PutBlobAsync(blobId, bytes, "text/plain", cancellationToken).ConfigureAwait(false);

        var snapshot = await repoSnapshots.GetLatestAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        var oldBlobId = snapshot?.TreeEntries?.TryGetValue(path, out var ob) == true ? ob : null;
        var kind = oldBlobId is not null
            ? NodalMerge.Studio.Contracts.Domain.OperationType.Replace
            : NodalMerge.Studio.Contracts.Domain.OperationType.Add;

        await repoOpEmitter.EmitAsync(new NodalMerge.Studio.Contracts.Domain.RepositoryOperation(
            OperationId:      $"op-{Guid.NewGuid():N}",
            RepositoryId:     repositoryId,
            ParentSnapshotId: snapshot?.SnapshotId,
            Kind:             kind,
            Path:             path,
            Timestamp:        DateTimeOffset.UtcNow,
            OldBlobId:        oldBlobId,
            NewBlobId:        blobId), cancellationToken).ConfigureAwait(false);

        return McpJson.Ok(new { repositoryId, path, blobId, kind = kind.ToString() });
    }

    [McpServerTool(Name = McpToolNames.RepositoryBlobSearch), Description("Search file contents in the latest CAS snapshot for a repository. Returns matching lines with path, lineNumber, and snippet. Case-insensitive.")]
    public async Task<string> BlobSearchAsync(string repositoryId, string query, CancellationToken cancellationToken = default)
    {
        if (repoBlobStore is null)
            return McpJson.Error(McpToolNames.RepositoryBlobSearch, "Blob store is not configured.");

        var snapshot = await repoSnapshots.GetLatestAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        if (snapshot?.TreeEntries is null)
            return McpJson.Error(McpToolNames.RepositoryBlobSearch, $"No snapshot found for repository '{repositoryId}'.");

        var matches = new List<object>();
        foreach (var (path, blobId) in snapshot.TreeEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var blobResult = await repoBlobStore.TryGetBlobAsync(blobId, cancellationToken).ConfigureAwait(false);
            if (!blobResult.Found || blobResult.Bytes is null) continue;

            var lines = Encoding.UTF8.GetString(blobResult.Bytes).Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                    matches.Add(new { path, lineNumber = i + 1, snippet = lines[i].Trim() });
            }
        }

        return McpJson.Ok(new { repositoryId, query, matches, count = matches.Count });
    }
}
