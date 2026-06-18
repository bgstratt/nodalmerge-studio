using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ModelTools(IWorkUnitService workUnits, IMergeService merges, IProposalReviewService proposalReview)
{
    [McpServerTool(Name = McpToolNames.ModelCompare), Description("Compare outputs from two models across merge proposals.")]
    public async Task<string> CompareAsync(
        string proposalIdA,
        string proposalIdB,
        CancellationToken cancellationToken = default)
    {
        var proposalA = await merges.GetAsync(proposalIdA, cancellationToken).ConfigureAwait(false);
        if (proposalA is null)
            return McpJson.Error(McpToolNames.ModelCompare, $"Proposal '{proposalIdA}' not found.");

        var proposalB = await merges.GetAsync(proposalIdB, cancellationToken).ConfigureAwait(false);
        if (proposalB is null)
            return McpJson.Error(McpToolNames.ModelCompare, $"Proposal '{proposalIdB}' not found.");

        var results = await Task.WhenAll(
            proposalReview.GetFileChangesAsync(proposalIdA, cancellationToken),
            proposalReview.GetFileChangesAsync(proposalIdB, cancellationToken)
        ).ConfigureAwait(false);
        var changesA = results[0];
        var changesB = results[1];

        var filesByPathA = changesA.ToDictionary(fc => fc.Path);
        var filesByPathB = changesB.ToDictionary(fc => fc.Path);
        var allPaths = filesByPathA.Keys.Union(filesByPathB.Keys).Distinct().ToList();

        var divergedFiles = allPaths.Select(path =>
        {
            filesByPathA.TryGetValue(path, out var fcA);
            filesByPathB.TryGetValue(path, out var fcB);

            var hunksA = fcA?.Hunks.SelectMany(h => h.Lines.Select(l => l.Text)).ToList() ?? [];
            var hunksB = fcB?.Hunks.SelectMany(h => h.Lines.Select(l => l.Text)).ToList() ?? [];
            var overlapping = hunksA.Intersect(hunksB).ToList();

            return new
            {
                path,
                diffA = fcA?.AfterContent ?? fcA?.BeforeContent ?? "(no output)",
                diffB = fcB?.AfterContent ?? fcB?.BeforeContent ?? "(no output)",
                overlappingLines = overlapping
            };
        }).ToList();

        return McpJson.Ok(new
        {
            modelA = proposalA.Model ?? proposalA.AgentId ?? "unknown",
            modelB = proposalB.Model ?? proposalB.AgentId ?? "unknown",
            divergedFiles,
            comparedAt = DateTimeOffset.UtcNow
        });
    }

    [McpServerTool(Name = McpToolNames.ModelReplay), Description("Replay a work unit's trajectory under a different model for comparison.")]
    public async Task<string> ReplayAsync(
        string workUnitId,
        string? compareModel = null,
        CancellationToken cancellationToken = default)
    {
        var wu = await workUnits.GetAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        if (wu is null)
            return McpJson.Error(McpToolNames.ModelReplay, $"Work unit '{workUnitId}' not found.");

        // Collect proposals associated with this work unit for model comparison
        var allProposals = await merges.ListAsync(sourceBranch: null, cancellationToken).ConfigureAwait(false);
        var relevantProposals = allProposals
            .Where(p => p.WorkUnitId == workUnitId)
            .Select(p => new
            {
                proposalId = p.ProposalId,
                model = p.Model,
                provider = p.Provider,
                agentId = p.AgentId,
                status = p.Status.ToString(),
                confidence = p.Confidence
            })
            .ToList();

        return McpJson.Ok(new
        {
            workUnitId,
            goal = wu.Goal,
            model = compareModel,
            proposals = relevantProposals,
            replayMode = "ModelComparison"
        });
    }
}