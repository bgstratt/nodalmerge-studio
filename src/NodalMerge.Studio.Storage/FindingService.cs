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

    public Task<IReadOnlyList<Finding>> ListPromotedPromptGuidanceAsync(PipelineStage stage, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<Finding>>(_findings.Values
            .Where(f => f.Kind == FindingKind.PromptImprovement && f.Status == FindingStatus.Promoted && f.TargetStage == stage)
            .OrderBy(f => f.CreatedAt)
            .ToList());

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

    // The part that "does something". KnowledgeGuideline becomes a durable, workspace-wide
    // Constraint artifact (no owning work unit), which BuildAgentWorkspaceAsync folds into
    // InheritedConstraints for every future work unit, any stage. PromptImprovement creates no
    // artifact at all — it's stage-scoped, not universal, so the durable effect is just this
    // Finding's own persisted Status=Promoted + TargetStage, read directly by the matching stage's
    // agent loop via ListPromotedPromptGuidanceAsync. Never mutates AgentProfile.SystemPrompt: a
    // profile with an empty SystemPrompt falls back to a hardcoded per-loop default that isn't
    // visible anywhere in the UI, so appending to it would silently replace an agent's entire
    // built-in instructions rather than improve them.
    private Task<string?> PromoteAsync(Finding finding, CancellationToken ct) =>
        finding.Kind switch
        {
            FindingKind.KnowledgeGuideline => PromoteKnowledgeGuidelineAsync(finding, ct),
            FindingKind.PromptImprovement => PromotePromptImprovementAsync(finding),
            _ => throw new NotSupportedException($"Promotion for FindingKind.{finding.Kind} is not implemented yet."),
        };

    private async Task<string?> PromoteKnowledgeGuidelineAsync(Finding finding, CancellationToken ct)
    {
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

    private static Task<string?> PromotePromptImprovementAsync(Finding finding)
    {
        if (finding.TargetStage is null)
            throw new InvalidOperationException(
                $"Finding '{finding.FindingId}' is a PromptImprovement with no TargetStage set — cannot promote.");
        return Task.FromResult<string?>(null);
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
