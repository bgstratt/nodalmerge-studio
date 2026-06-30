using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

public sealed class BranchTools(IBranchService branches)
{
    [McpServerTool(Name = McpToolNames.BranchCreate), Description("Create a branch.")]
    public async Task<string> CreateAsync(string name, string? fromBranch = null, CancellationToken cancellationToken = default)
    {
        var branchId = await branches.CreateBranchAsync(name, fromBranch, cancellationToken: cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { branchId });
    }

    [McpServerTool(Name = McpToolNames.BranchCheckout), Description("Check out a branch.")]
    public async Task<string> CheckoutAsync(string branchId, CancellationToken cancellationToken = default)
    {
        await branches.CheckoutBranchAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { branchId, status = "checked_out" });
    }

    [McpServerTool(Name = McpToolNames.BranchList), Description("List branches.")]
    public async Task<string> ListAsync(CancellationToken cancellationToken = default) =>
        McpJson.Ok(await branches.ListBranchesAsync(cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = McpToolNames.BranchStatus), Description("Get branch status for agents.")]
    public async Task<string> StatusAsync(string branchId, CancellationToken cancellationToken = default)
    {
        var status = await branches.GetStatusAsync(branchId, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(status);
    }
}
