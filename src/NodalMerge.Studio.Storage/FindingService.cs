using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class FindingService(IStudioNodeStore nodeStore, IArtifactLineageService artifactLineage)
    : IFindingService, IRehydratable
{
    private readonly ConcurrentDictionary<string, Finding> _findings = new();

    public async Task<Finding> ProposeAsync(Finding finding, CancellationToken ct = default)
    {
        _findings[finding.FindingId] = finding;
        await Persist(finding, ct).ConfigureAwait(false);
        return finding;
    }

    public Task<Finding?> GetAsync(string findingId, CancellationToken ct = default)
    {
        _findings.TryGetValue(findingId, out var finding);
        return Task.FromResult(finding);
    }

    public Task<IReadOnlyList<Finding>> ListAsync(CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Finding>>(
            _findings.Values.OrderByDescending(f => f.CreatedAt).ToList());

    public async Task<Finding> ReviewAsync(
        string findingId, FindingStatus decision, string? notes = null, CancellationToken ct = default)
    {
        if (decision == FindingStatus.Open)
            throw new ArgumentException("Open is the initial state only, not a review decision.", nameof(decision));
        if (!_findings.TryGetValue(findingId, out var finding))
            throw new KeyNotFoundException($"Finding '{findingId}' was not found.");

        string? promotedArtifactId = null;
        if (decision == FindingStatus.Promoted)
            promotedArtifactId = await PromoteAsync(finding, ct).ConfigureAwait(false);

        var updated = finding with
        {
            Status = decision,
            ReviewNotes = notes,
            ReviewedAt = DateTimeOffset.UtcNow,
            PromotedArtifactId = promotedArtifactId,
        };
        _findings[findingId] = updated;
        await Persist(updated, ct).ConfigureAwait(false);
        return updated;
    }

    // The part that "does something": a promoted KnowledgeGuideline becomes a durable, workspace-
    // wide Constraint artifact (no owning work unit), which BuildAgentWorkspaceAsync folds into
    // InheritedConstraints for every future work unit. PromptImprovement's promotion action (editing
    // an AgentProfile's SystemPrompt) is Phase 4 — deliberately not implemented yet.
    private async Task<string> PromoteAsync(Finding finding, CancellationToken ct)
    {
        if (finding.Kind != FindingKind.KnowledgeGuideline)
            throw new NotSupportedException(
                $"Promotion for FindingKind.{finding.Kind} is not implemented yet.");

        var artifact = new ArtifactRef(
            ArtifactId: $"constraint-{Guid.NewGuid():N}",
            Type: ArtifactType.Constraint,
            ParentArtifactId: null,
            Status: ArtifactStatus.Approved,
            CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null,
            OwnedByAgentId: null,
            Title: finding.Title,
            Body: finding.Summary);

        await artifactLineage.RecordAsync(artifact, ct).ConfigureAwait(false);
        return artifact.ArtifactId;
    }

    public async Task RehydrateAsync(CancellationToken ct = default)
    {
        var records = await nodeStore.ReadAllNodesAsync(StudioNodeKind.FindingV1, ct).ConfigureAwait(false);
        foreach (var (_, payloadJson) in records)
        {
            var finding = JsonSerializer.Deserialize<Finding>(payloadJson);
            if (finding is not null) { _findings[finding.FindingId] = finding; }
        }
    }

    private Task Persist(Finding finding, CancellationToken ct) =>
        nodeStore.WriteNodeAsync(StudioNodeKind.FindingV1, finding.FindingId, JsonSerializer.Serialize(finding), ct);
}
