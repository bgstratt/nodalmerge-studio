using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class StateTools(IKnownGoodStateService states)
{
    [McpServerTool(Name = McpToolNames.StateMarkKnownGood), Description("Mark a branch state as known good.")]
    public async Task<string> MarkKnownGoodAsync(
        string branchId,
        string description,
        string? verificationResults = null,
        string createdBy = "studio",
        CancellationToken cancellationToken = default)
    {
        var state = new KnownGoodState(
            $"KGS-{Guid.NewGuid():N}",
            branchId,
            description,
            verificationResults,
            DateTimeOffset.UtcNow,
            createdBy);

        var created = await states.MarkKnownGoodAsync(state, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { stateId = created.StateId });
    }

    [McpServerTool(Name = McpToolNames.StateFindKnownGood), Description("Find known good states for a branch.")]
    public async Task<string> FindKnownGoodAsync(string branchId, CancellationToken cancellationToken = default) =>
        McpJson.Ok(await states.FindKnownGoodAsync(branchId, cancellationToken).ConfigureAwait(false));

    [McpServerTool(Name = McpToolNames.StateCheckoutKnownGood), Description("Check out a known good state.")]
    public async Task<string> CheckoutKnownGoodAsync(string knownGoodStateId, CancellationToken cancellationToken = default)
    {
        var state = await states.CheckoutKnownGoodAsync(knownGoodStateId, cancellationToken).ConfigureAwait(false);
        return state is null
            ? McpJson.Error(McpToolNames.StateCheckoutKnownGood, $"Known good state '{knownGoodStateId}' was not found.")
            : McpJson.Ok(state);
    }
}
