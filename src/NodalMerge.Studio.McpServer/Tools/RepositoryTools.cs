using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class RepositoryTools(IRepositoryRegistryService repositories)
{
    [McpServerTool(Name = McpToolNames.RepositoryRegister), Description("Register a known repository path (informational only — does not change the currently seeded repository; Studio manages exactly one active repository per instance). Idempotent by path.")]
    public async Task<string> RegisterAsync(string path, string? label = null, CancellationToken cancellationToken = default)
    {
        var repository = await repositories.RegisterAsync(path, label, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { repositoryId = repository.RepositoryId, path = repository.Path });
    }

    [McpServerTool(Name = McpToolNames.RepositoryList), Description("List repositories known to Studio. Call this before assuming no other repository exists.")]
    public async Task<string> ListAsync(CancellationToken cancellationToken = default) =>
        McpJson.Ok(await repositories.ListAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = McpToolNames.RepositoryCreate), Description("Create a fresh repository at the given path (creates the directory if needed, runs git init if it isn't already a git repo) and register it. Idempotent.")]
    public async Task<string> CreateAsync(string path, string? label = null, CancellationToken cancellationToken = default)
    {
        var repository = await repositories.CreateAsync(path, label, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { repositoryId = repository.RepositoryId, path = repository.Path });
    }

    [McpServerTool(Name = McpToolNames.RepositoryClone), Description("Clone a repository (local or remote URL) into the given target path via `git clone`, then register it.")]
    public async Task<string> CloneAsync(string url, string targetPath, string? label = null, CancellationToken cancellationToken = default)
    {
        var repository = await repositories.CloneAsync(url, targetPath, label, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { repositoryId = repository.RepositoryId, path = repository.Path });
    }

    [McpServerTool(Name = McpToolNames.RepositoryReadFile), Description("Read a file's content directly from a registered repository, by repositoryId — read-only, regardless of which repository is currently active. Used to resolve a cross-repo file reference (WorkUnit.ReferenceFiles).")]
    public async Task<string> ReadFileAsync(string repositoryId, string path, CancellationToken cancellationToken = default)
    {
        var content = await repositories.ReadFileAsync(repositoryId, path, cancellationToken).ConfigureAwait(false);
        return content is null
            ? McpJson.Error(McpToolNames.RepositoryReadFile, $"File '{path}' not found in repository '{repositoryId}'.")
            : McpJson.Ok(new { content });
    }

    [McpServerTool(Name = McpToolNames.RepositoryListFiles), Description("List files in a registered repository, by repositoryId — read-only, regardless of which repository is currently active. Optionally narrow to a sub-directory and/or wildcard pattern.")]
    public async Task<string> ListFilesAsync(string repositoryId, string? subPath = null, string? pattern = null, CancellationToken cancellationToken = default)
    {
        var files = await repositories.ListFilesAsync(repositoryId, subPath, pattern, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { files });
    }
}
