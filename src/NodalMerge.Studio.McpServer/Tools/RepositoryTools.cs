using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
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
}
