using System.ComponentModel;
using ModelContextProtocol.Server;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Versioning;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.McpServer.Tools;

[McpServerToolType]
public sealed class ArtifactTools(IArtifactLineageService artifacts, IWorkUnitService workUnits)
{
    [McpServerTool(Name = McpToolNames.ArtifactRecord), Description("Record a durable knowledge note (Research, Decision, or Constraint) so future work units don't have to rediscover it.")]
    public async Task<string> RecordAsync(
        string workUnitId,
        string type,
        string title,
        string body,
        string? parentArtifactId = null,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.TryParse<ArtifactType>(type, ignoreCase: true, out var artifactType) ||
            artifactType is not (ArtifactType.Research or ArtifactType.Decision or ArtifactType.Constraint))
        {
            return McpJson.Error(McpToolNames.ArtifactRecord, "type must be one of: Research, Decision, Constraint.");
        }

        var artifact = new ArtifactRef(
            $"KA-{Guid.NewGuid():N}",
            artifactType,
            parentArtifactId ?? workUnitId,
            ArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            workUnitId,
            null,
            title,
            body);

        var recorded = await artifacts.RecordAsync(artifact, cancellationToken).ConfigureAwait(false);
        return McpJson.Ok(new { artifactId = recorded.ArtifactId });
    }

    [McpServerTool(Name = McpToolNames.ArtifactQuery), Description("Search knowledge artifacts for a work unit and its ancestors by type and/or keyword.")]
    public async Task<string> QueryAsync(
        string workUnitId,
        string? type = null,
        string? keywords = null,
        CancellationToken cancellationToken = default)
    {
        ArtifactType? filterType = null;
        if (type is not null)
        {
            if (!Enum.TryParse<ArtifactType>(type, ignoreCase: true, out var parsed))
                return McpJson.Error(McpToolNames.ArtifactQuery, $"Unknown artifact type '{type}'.");
            filterType = parsed;
        }

        var chain = await CollectChainWithAncestorsAsync(workUnitId, cancellationToken).ConfigureAwait(false);
        var filtered = chain
            .Where(a => filterType is null || a.Type == filterType)
            .Where(a => keywords is null || MatchesKeywords(a, keywords))
            .ToList();

        return McpJson.Ok(filtered);
    }

    [McpServerTool(Name = McpToolNames.ArtifactList), Description("List the full artifact chain for a work unit, including ancestors' artifacts by default.")]
    public async Task<string> ListAsync(
        string workUnitId,
        bool includeAncestors = true,
        CancellationToken cancellationToken = default)
    {
        var list = includeAncestors
            ? await CollectChainWithAncestorsAsync(workUnitId, cancellationToken).ConfigureAwait(false)
            : await artifacts.GetChainAsync(workUnitId, cancellationToken).ConfigureAwait(false);

        return McpJson.Ok(list);
    }

    private async Task<IReadOnlyList<ArtifactRef>> CollectChainWithAncestorsAsync(string workUnitId, CancellationToken ct)
    {
        var ancestorIds = new List<string>();
        var currentId = workUnitId;
        while (currentId is not null)
        {
            ancestorIds.Add(currentId);
            var unit = await workUnits.GetAsync(currentId, ct).ConfigureAwait(false);
            currentId = unit?.ParentWorkUnitId;
        }

        var seen = new HashSet<string>();
        var combined = new List<ArtifactRef>();
        foreach (var id in Enumerable.Reverse(ancestorIds))
        {
            var chain = await artifacts.GetChainAsync(id, ct).ConfigureAwait(false);
            foreach (var artifact in chain)
                if (seen.Add(artifact.ArtifactId))
                    combined.Add(artifact);
        }

        return combined;
    }

    private static bool MatchesKeywords(ArtifactRef artifact, string keywords) =>
        keywords.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .All(k => $"{artifact.Title} {artifact.Body}".Contains(k, StringComparison.OrdinalIgnoreCase));
}
