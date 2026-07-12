using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using StudioArtifactStatus = NodalMerge.Studio.Contracts.Domain.ArtifactStatus;

namespace NodalMerge.Studio.Storage;

// Slice 15f — the one implementation of artifact commands shared by the MCP tool, the REST
// endpoint, and the agent-loop's in-process dispatcher. The CollectChainWithAncestorsAsync walk
// was previously copy-pasted verbatim between ArtifactTools.cs and McpToolDispatcher.cs.
public sealed class ArtifactCommandService(
    IArtifactLineageService artifacts,
    IWorkUnitService workUnits,
    IDomainAgentTriggerService? domainAgentTrigger = null) : IArtifactCommandService
{
    public async Task<ArtifactRef> RecordAsync(
        string workUnitId,
        string type,
        string title,
        string body,
        string? parentArtifactId = null,
        IReadOnlyList<string>? supersedes = null,
        CancellationToken ct = default)
    {
        if (!Enum.TryParse<ArtifactType>(type, ignoreCase: true, out var artifactType) ||
            artifactType is not (ArtifactType.Research or ArtifactType.Decision or ArtifactType.Constraint or ArtifactType.Supersession))
            throw new ArgumentException("type must be one of: Research, Decision, Constraint, Supersession.", nameof(type));

        if (artifactType == ArtifactType.Supersession && (supersedes is null or { Count: 0 }))
            throw new ArgumentException("supersedes must be non-empty for a Supersession artifact.", nameof(supersedes));

        var artifact = new ArtifactRef(
            $"KA-{Guid.NewGuid():N}",
            artifactType,
            parentArtifactId ?? workUnitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            workUnitId,
            null,
            title,
            body,
            InvalidatedByArtifactId: null,
            Supersedes: supersedes);

        var recorded = await artifacts.RecordAsync(artifact, ct).ConfigureAwait(false);

        if (domainAgentTrigger is not null)
            await domainAgentTrigger.NotifyArtifactRecordedAsync(recorded, ct).ConfigureAwait(false);

        return recorded;
    }

    public async Task<ArtifactRef> RecordPlanAsync(string workUnitId, string planContent, CancellationToken ct = default)
    {
        var artifact = new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}",
            ArtifactType.Plan,
            workUnitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            workUnitId,
            null,
            "Plan",
            planContent);

        return await artifacts.RecordAsync(artifact, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArtifactRef>> QueryAsync(
        string workUnitId,
        string? type = null,
        string? keywords = null,
        CancellationToken ct = default)
    {
        ArtifactType? filterType = null;
        if (type is not null)
        {
            if (!Enum.TryParse<ArtifactType>(type, ignoreCase: true, out var parsed))
                throw new ArgumentException($"Unknown artifact type '{type}'.", nameof(type));
            filterType = parsed;
        }

        var chain = await CollectChainWithAncestorsAsync(workUnitId, ct).ConfigureAwait(false);
        var filtered = chain
            .Where(a => filterType is null || a.Type == filterType)
            .Where(a => keywords is null || MatchesKeywords(a, keywords))
            .ToList();

        return filtered;
    }

    public async Task<IReadOnlyList<ArtifactRef>> ListAsync(
        string workUnitId,
        bool includeAncestors = true,
        CancellationToken ct = default)
    {
        return includeAncestors
            ? await CollectChainWithAncestorsAsync(workUnitId, ct).ConfigureAwait(false)
            : await artifacts.GetChainAsync(workUnitId, ct).ConfigureAwait(false);
    }

    public Task<ArtifactRef> InvalidateAsync(
        string artifactId, string reason, string? sessionId = null, CancellationToken ct = default) =>
        artifacts.InvalidateAsync(artifactId, reason, sessionId, ct);

    // Walks ParentWorkUnitId root-first, folding in every ancestor's own chain ahead of this
    // work unit's — same inheritance model as ProjectionManager.BuildAgentWorkspaceAsync (10g).
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