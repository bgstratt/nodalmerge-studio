using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

public sealed class FindingService(
    IStudioNodeStore nodeStore,
    IArtifactLineageService artifactLineage,
    IRepositoryRegistryService? repositories = null,
    WorkspaceOptions? options = null)
    : IFindingService, IRehydratable
{
    private readonly ConcurrentDictionary<string, Finding> _findings = new();

    // Phase 0 — the workspace's repo id (same registry RepositoryId WorkUnit/Decision carry), resolved
    // once and cached so findings route to the SAME repo room as the constraints they relate to.
    private string? _workspaceRepositoryId;
    private bool _repositoryIdResolved;

    public async Task<Finding> ProposeAsync(Finding finding, CancellationToken ct = default)
    {
        // Attribute every finding — deterministic, LLM-scan, or imported — to the workspace's repo so
        // FindingV1 replicates to same-repo peers. A caller that already set RepositoryId wins.
        if (finding.RepositoryId is null)
        {
            var repoId = await ResolveWorkspaceRepositoryIdAsync(ct).ConfigureAwait(false);
            if (repoId is not null)
                finding = finding with { RepositoryId = repoId };
        }
        _findings[finding.FindingId] = finding;
        await Persist(finding, ct).ConfigureAwait(false);
        return finding;
    }

    // Resolve the workspace's registry RepositoryId from the seed repo path (idempotent RegisterAsync,
    // the same call goal creation makes). Best-effort and cached: if there's no seed repo or resolution
    // fails, findings keep a null RepositoryId and stay in the local "studio" room — never a hard error.
    private async Task<string?> ResolveWorkspaceRepositoryIdAsync(CancellationToken ct)
    {
        if (_repositoryIdResolved)
            return _workspaceRepositoryId;
        if (repositories is not null && !string.IsNullOrWhiteSpace(options?.SeedRepositoryPath))
        {
            try
            {
                var repo = await repositories.RegisterAsync(options.SeedRepositoryPath!, label: null, ct).ConfigureAwait(false);
                _workspaceRepositoryId = repo.RepositoryId;
            }
            catch { /* unresolvable repo → findings stay local, not fatal */ }
        }
        _repositoryIdResolved = true;
        return _workspaceRepositoryId;
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
        // Phase 1 (plans/organizational-knowledge-and-workgroup-scope.md) — promotion defaults to
        // shared + repo-specific: Reach=Workgroup so the constraint actually replicates (fixing the
        // old "global constraint stranded in the local room" bug), with the finding's own
        // RepositoryId (Phase 0) as the application scope — so it lands in that repo's room and
        // applies to that repo. A null RepositoryId (no workspace repo) falls back to the shared
        // "workgroup" room (all repos). Widening to all-repos, or restricting to a private local
        // override, are separate explicit human actions (elevate / restrict — progressive promotion).
        var artifact = new ArtifactRef(
            ArtifactId: $"constraint-{Guid.NewGuid():N}",
            Type: ArtifactType.Constraint,
            ParentArtifactId: null,
            Status: ArtifactStatus.Approved,
            CreatedAt: DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null,
            OwnedByAgentId: null,
            Title: finding.Title,
            Body: finding.Summary,
            RepositoryId: finding.RepositoryId,
            Reach: ArtifactReach.Workgroup);

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

    // Route by RepositoryId: non-null → the 4-arg overload lands FindingV1 in that repo's room (shared
    // with same-repo peers, since FindingV1 is now in RepoScopedKinds); null → the 3-arg overload keeps
    // it in the local "studio" room. ReviewAsync's re-persist carries the stored RepositoryId, so a
    // reviewed finding stays in the same room it was first written to.
    private Task Persist(Finding finding, CancellationToken ct) =>
        string.IsNullOrEmpty(finding.RepositoryId)
            ? nodeStore.WriteNodeAsync(StudioNodeKind.FindingV1, finding.FindingId, JsonSerializer.Serialize(finding), ct)
            : nodeStore.WriteNodeAsync(StudioNodeKind.FindingV1, finding.FindingId, JsonSerializer.Serialize(finding), finding.RepositoryId, ct);
}
