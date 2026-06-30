using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ExternalRepositoryTools(IRepositoryRegistryService repositories)
{
    [McpServerTool(Name = McpServerToolNames.RepoRegister)]
    [Description("Register a local repository path so agents can work in it. Returns a repositoryId that can be passed to nms_v1_goal_run. Idempotent — safe to call again for a path already registered.")]
    public async Task<string> RegisterAsync(
        [Description("Absolute path to the local git repository.")] string path,
        [Description("Optional human-readable label for this repository.")] string? label = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return McpJson.Error(McpServerToolNames.RepoRegister, "path is required.");

            var repo = await repositories.RegisterAsync(path, label, cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                repositoryId = repo.RepositoryId,
                path = repo.Path,
                label = repo.Label
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.RepoRegister, ex.Message);
        }
    }

    [McpServerTool(Name = McpServerToolNames.RepoList)]
    [Description("List all registered repositories. Use the repositoryId values when starting a goal via nms_v1_goal_run.")]
    public async Task<string> ListAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var repos = await repositories.ListAsync(cancellationToken).ConfigureAwait(false);
            return McpJson.Ok(new
            {
                repositories = repos.Select(r => new
                {
                    repositoryId = r.RepositoryId,
                    path = r.Path,
                    label = r.Label
                })
            });
        }
        catch (Exception ex)
        {
            return McpJson.Error(McpServerToolNames.RepoList, ex.Message);
        }
    }
}
