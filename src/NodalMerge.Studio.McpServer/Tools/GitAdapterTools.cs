using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

// Phase 14 — Git import/export adapter tools for the HTTP MCP server.
// Allows external harnesses (Copilot, Cline, Claude Desktop) to trigger git-backed
// import and export operations against the Studio CAS.
public sealed class GitAdapterTools(IGitAdapter gitAdapter)
{
    [McpServerTool(Name = McpToolNames.RepositoryGitImport), Description(
        "Import a git repository commit into the Studio CAS, creating a RepositorySnapshot. " +
        "Walks the git tree at the given commit (or HEAD if commitSha is omitted), writes each " +
        "blob to the content-addressable store, and returns the new snapshotId. " +
        "The git repository must be accessible on the local filesystem of the Studio host.")]
    public async Task<string> GitImportAsync(
        string repositoryId,
        string gitRepoPath,
        string? commitSha = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var snapshotId = await gitAdapter.ImportAsync(gitRepoPath, commitSha, repositoryId, cancellationToken)
                .ConfigureAwait(false);
            return McpJson.Ok(new { repositoryId, snapshotId, gitRepoPath, commitSha });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpToolNames.RepositoryGitImport, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.RepositoryGitBranchCreate), Description(
        "Create a git branch in an existing local repository. fromRef defaults to HEAD. " +
        "Returns the commit SHA the new branch points to. " +
        "This creates a git branch — for Studio workspace branches use nm_v1_branch_create.")]
    public async Task<string> GitBranchCreateAsync(
        string gitRepoPath,
        string branchName,
        string? fromRef = null,
        bool checkout = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var sha = await gitAdapter.CreateGitBranchAsync(gitRepoPath, branchName, fromRef, checkout, cancellationToken)
                .ConfigureAwait(false);
            return McpJson.Ok(new { gitRepoPath, branchName, fromRef, checkout, commitSha = sha });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpToolNames.RepositoryGitBranchCreate, ex.Message);
        }
    }

    [McpServerTool(Name = McpToolNames.RepositoryGitExport), Description(
        "Export a repository snapshot to a git working tree. Files are always written to the " +
        "target path. Whether a git commit is created depends on the AllowAgentGitCommits " +
        "workspace option (default: false — files written, no commit). " +
        "The target git repository must exist on the Studio host filesystem.")]
    public async Task<string> GitExportAsync(
        string repositoryId,
        string targetGitRepoPath,
        string branchName,
        string? snapshotId = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await gitAdapter.ExportAsync(
                repositoryId, snapshotId, targetGitRepoPath, branchName, cancellationToken)
                .ConfigureAwait(false);
            return McpJson.Ok(new
            {
                repositoryId = result.RepositoryId,
                snapshotId   = result.SnapshotId,
                targetPath   = result.TargetPath,
                branchName   = result.BranchName,
                committed    = result.Committed,
                commitSha    = result.CommitSha,
                pushed       = result.Pushed,
                pushOutput   = result.PushOutput,
                message      = result.Message,
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpToolNames.RepositoryGitExport, ex.Message);
        }
    }
}
