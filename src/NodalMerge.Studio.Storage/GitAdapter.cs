using System.Diagnostics;
using LibGit2Sharp;
using NodalMerge.Host.Abstractions.Providers;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// Phase 14 — Git as Import/Export Adapter.
// Import walks a git commit tree, writes each blob to CAS, emits RepositoryOperation(Import),
// and creates a RepositorySnapshot from the resulting path→blobId map.
// Export materializes a snapshot into the target working tree. Whether a git commit is created
// depends on WorkspaceOptions.AllowAgentGitCommits (default: false — files written, no commit).
// Push is gated behind AllowAgentGitPush (shells out to `git push` to use the host credential store).
public sealed class GitAdapter(
    IRepositorySnapshotService snapshots,
    IRepositoryOpService repoOps,
    WorkspaceOptions? options = null,
    IBlobStoreProvider? blobStore = null,
    IMaterializationEngine? materializer = null) : IGitAdapter
{
    public async Task<string> ImportAsync(
        string gitRepoPath, string? commitSha, string repositoryId, CancellationToken ct = default)
    {
        if (blobStore is null)
            throw new InvalidOperationException("Blob store is not configured — cannot import into CAS.");

        using var repo = new Repository(gitRepoPath);
        var commit = commitSha is not null
            ? repo.Lookup<Commit>(commitSha)
              ?? throw new ArgumentException($"Commit '{commitSha}' not found in '{gitRepoPath}'.")
            : repo.Head.Tip
              ?? throw new InvalidOperationException("Repository HEAD has no commits.");

        var treeEntries = new Dictionary<string, string>(StringComparer.Ordinal);
        var ops = new List<RepositoryOperation>();

        foreach (var (entryPath, blob) in FlattenTree(commit.Tree))
        {
            ct.ThrowIfCancellationRequested();

            using var stream = blob.GetContentStream();
            var bytes = ReadAllBytes(stream);
            var blobId = BlobHasher.ComputeHash(bytes);

            await blobStore.PutBlobAsync(blobId, bytes, "text/plain", ct).ConfigureAwait(false);
            treeEntries[entryPath] = blobId;

            ops.Add(new RepositoryOperation(
                OperationId:      $"git-{Guid.NewGuid():N}",
                RepositoryId:     repositoryId,
                ParentSnapshotId: null,
                Kind:             OperationType.Import,
                Path:             entryPath,
                Timestamp:        DateTimeOffset.UtcNow,
                NewBlobId:        blobId,
                Reason:           $"Imported from git commit {commit.Sha[..8]}"));
        }

        foreach (var op in ops)
            await repoOps.EmitAsync(op, ct).ConfigureAwait(false);

        var snapshot = await snapshots.CreateAsync(
            repositoryId,
            treeEntries,
            baseSnapshotId: null,
            gitCommit: commit.Sha,
            source: "ImportedFromGit",
            ct: ct).ConfigureAwait(false);

        return snapshot.SnapshotId;
    }

    public async Task<GitExportResult> ExportAsync(
        string repositoryId, string? snapshotId, string targetGitRepoPath,
        string branchName, CancellationToken ct = default)
    {
        if (materializer is null)
            throw new InvalidOperationException("Materialization engine is not configured — cannot export.");

        RepositorySnapshot? snapshot;
        if (snapshotId is not null)
        {
            snapshot = await snapshots.GetAsync(snapshotId, ct).ConfigureAwait(false)
                       ?? throw new ArgumentException($"Snapshot '{snapshotId}' not found.");
        }
        else
        {
            snapshot = await snapshots.GetLatestAsync(repositoryId, ct).ConfigureAwait(false)
                       ?? throw new InvalidOperationException($"No snapshot found for repository '{repositoryId}'.");
        }

        // Always materialize files into the target working tree.
        await materializer.MaterializeAsync(snapshot, targetGitRepoPath, fileScope: null, ct)
            .ConfigureAwait(false);

        // Only create a git commit if explicitly opted in.
        if (options?.AllowAgentGitCommits != true)
        {
            return new GitExportResult(
                RepositoryId: repositoryId,
                SnapshotId:   snapshot.SnapshotId,
                TargetPath:   targetGitRepoPath,
                BranchName:   branchName,
                Committed:    false,
                CommitSha:    null,
                Message:      "Files written to working tree. No commit created — set AllowAgentGitCommits = true to enable agent commits.");
        }

        using var repo = new Repository(targetGitRepoPath);
        var branch = repo.Branches[branchName] ?? repo.CreateBranch(branchName);
        Commands.Checkout(repo, branch);
        Commands.Stage(repo, "*");

        var author = new Signature("NodalMerge Studio", "studio@nodalmerge", DateTimeOffset.UtcNow);
        var commit = repo.Commit(
            $"Export from snapshot {snapshot.SnapshotId[..8]} (repository {repositoryId})",
            author, author);

        // Push via shell-out — respects the host's existing credential store (SSH agent,
        // credential manager, etc.) without requiring explicit credential handling in code.
        bool pushed = false;
        string? pushOutput = null;
        if (options?.AllowAgentGitPush == true)
        {
            (pushed, pushOutput) = await GitPushAsync(targetGitRepoPath, branchName, ct)
                .ConfigureAwait(false);
        }

        return new GitExportResult(
            RepositoryId: repositoryId,
            SnapshotId:   snapshot.SnapshotId,
            TargetPath:   targetGitRepoPath,
            BranchName:   branchName,
            Committed:    true,
            CommitSha:    commit.Sha,
            Pushed:       pushed,
            PushOutput:   pushOutput);
    }

    public Task<string> CreateGitBranchAsync(
        string gitRepoPath, string branchName, string? fromRef = null,
        bool checkout = false, CancellationToken ct = default)
    {
        using var repo = new Repository(gitRepoPath);

        Commit targetCommit;
        if (fromRef is not null)
        {
            targetCommit = repo.Lookup<Commit>(fromRef)
                ?? (repo.Branches[fromRef]?.Tip)
                ?? throw new ArgumentException($"Ref '{fromRef}' not found in '{gitRepoPath}'.");
        }
        else
        {
            targetCommit = repo.Head.Tip
                ?? throw new InvalidOperationException("Repository HEAD has no commits.");
        }

        var branch = repo.Branches[branchName]
            ?? repo.CreateBranch(branchName, targetCommit);

        if (checkout)
            Commands.Checkout(repo, branch);

        return Task.FromResult(branch.Tip.Sha);
    }

    // Shell out to `git push origin {branchName}` in the target working directory.
    // Using the git CLI respects the user's existing credential store without any credential
    // plumbing in this code (SSH agent, Windows Credential Manager, macOS keychain, etc.).
    private static async Task<(bool pushed, string? output)> GitPushAsync(
        string workingDir, string branchName, CancellationToken ct)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git", $"push origin {branchName}")
                {
                    WorkingDirectory       = workingDir,
                    RedirectStandardOutput = true,
                    RedirectStandardError  = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true,
                }
            };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);

            var combined = (stdout + stderr).Trim();
            return process.ExitCode == 0
                ? (true, combined.Length > 0 ? combined : null)
                : (false, $"git push exited with code {process.ExitCode}: {combined}");
        }
        catch (Exception ex)
        {
            return (false, $"git push failed: {ex.Message}");
        }
    }

    // Recursively walk the git tree, yielding (relativePath, blob) pairs for all blob entries.
    // LibGit2Sharp's TreeEntry only exposes the entry name, not the full path, so we build it.
    private static IEnumerable<(string Path, LibGit2Sharp.Blob Blob)> FlattenTree(
        Tree tree, string prefix = "")
    {
        foreach (var entry in tree)
        {
            var fullPath = prefix.Length == 0 ? entry.Name : $"{prefix}/{entry.Name}";
            if (entry.TargetType == TreeEntryTargetType.Tree)
            {
                foreach (var child in FlattenTree((Tree)entry.Target, fullPath))
                    yield return child;
            }
            else if (entry.TargetType == TreeEntryTargetType.Blob)
            {
                yield return (fullPath, (LibGit2Sharp.Blob)entry.Target);
            }
        }
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }
}
