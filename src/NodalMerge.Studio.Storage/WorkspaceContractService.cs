using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;

namespace NodalMerge.Studio.Storage;

// plans/harness-hosting-architecture.md Phase A.4 — assembles the Workspace Contract
// (docs/contracts/workspace-contract-v1.md) from the EngineeringState projection plus work-unit
// state, and materializes it into a branch's `.workspace/` directory via the existing
// branch-scoped IFileWorkspaceService.WriteAsync primitive (no raw filesystem path resolution).
public sealed class WorkspaceContractService(
    IProjectionManager projections,
    IWorkUnitService workUnits,
    IFileWorkspaceService fileWorkspace,
    IArtifactLineageService artifactLineage,
    WorkspaceOptions? workspaceOptions = null) : IWorkspaceContractService
{
    private const string ContractVersion = "1.0";

    // WriteIndented for human-legibility (a harness or a human may open these files directly);
    // JsonSerializerOptions.Web's camelCase/case-insensitive settings otherwise carried through
    // unchanged, matching the rest of the codebase's projection-serialization convention.
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerOptions.Web) { WriteIndented = true };

    public async Task<WorkspaceContractBundle> AssembleAsync(string workUnitId, CancellationToken ct = default)
    {
        var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Work unit '{workUnitId}' was not found.");
        var root = await ResolveRootAsync(wu, ct).ConfigureAwait(false);

        var stateResult = await projections.GetAsync(
            new ProjectionRequest(ProjectionType.EngineeringState, ProjectionLevel.Normal, WorkUnitId: workUnitId), ct)
            .ConfigureAwait(false);
        var state = JsonSerializer.Deserialize<EngineeringStateProjectionPayload>(stateResult.DataJson, JsonSerializerOptions.Web)
            ?? new EngineeringStateProjectionPayload([], DateTimeOffset.UtcNow);

        var manifest = new WorkspaceContractManifest(
            ContractVersion, RuntimeVersion(), root.WorkUnitId, wu.WorkUnitId, WorkspaceContractCapabilities.All);
        var goal = new WorkspaceContractGoal(root.WorkUnitId, root.Goal, root.SuccessCriteria, ParentGoalId: null);
        var workUnit = new WorkspaceContractWorkUnit(
            wu.WorkUnitId, wu.BranchId, wu.FileScope, wu.DependsOn, wu.ParentWorkUnitId);
        var reviewPolicy = new WorkspaceContractReviewPolicy(
            wu.TaskReviewPolicy.ToString(), wu.WorkspaceReviewPolicy.ToString(),
            SelfVerifyBuildRequired: workspaceOptions?.RequireBuildBeforeProposal ?? false,
            SelfVerifyTestRequired: workspaceOptions?.RequireTestBeforeProposal ?? false);

        return new WorkspaceContractBundle(manifest, goal, workUnit, state, reviewPolicy);
    }

    public async Task MaterializeAsync(string workUnitId, CancellationToken ct = default)
    {
        var bundle = await AssembleAsync(workUnitId, ct).ConfigureAwait(false);
        var branchId = bundle.WorkUnit.BranchId;

        await WriteAsync(branchId, "manifest", bundle.Manifest, RenderManifestMarkdown, ct).ConfigureAwait(false);
        await WriteAsync(branchId, "goal", bundle.Goal, RenderGoalMarkdown, ct).ConfigureAwait(false);
        await WriteAsync(branchId, "workunit", bundle.WorkUnit, RenderWorkUnitMarkdown, ct).ConfigureAwait(false);
        await WriteAsync(branchId, "state", bundle.EngineeringState, RenderEngineeringStateMarkdown, ct).ConfigureAwait(false);

        // constraints.json is the same facts filtered to Type == Constraint && IsCurrent — a thin,
        // harness-convenience view over state.json, not a second source of truth.
        var constraints = new EngineeringStateProjectionPayload(
            [.. bundle.EngineeringState.Facts.Where(f => f.Type == ArtifactType.Constraint && f.IsCurrent)],
            bundle.EngineeringState.GeneratedAt);
        await WriteAsync(branchId, "constraints", constraints, RenderEngineeringStateMarkdown, ct).ConfigureAwait(false);

        await WriteAsync(branchId, "review-policy", bundle.ReviewPolicy, RenderReviewPolicyMarkdown, ct).ConfigureAwait(false);
    }

    public async Task<string> RenderEngineeringStateMarkdownAsync(string workUnitId, CancellationToken ct = default)
    {
        var bundle = await AssembleAsync(workUnitId, ct).ConfigureAwait(false);
        return RenderEngineeringStateMarkdown(bundle.EngineeringState);
    }

    private static readonly ArtifactType[] RecordableTypes =
        [ArtifactType.Research, ArtifactType.Decision, ArtifactType.Constraint, ArtifactType.Supersession];

    public async Task<IReadOnlyList<ArtifactRef>> HarvestDecisionsAsync(string workUnitId, CancellationToken ct = default)
    {
        var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Work unit '{workUnitId}' was not found.");

        var files = await fileWorkspace
            .ListIncludingDotfilesAsync(wu.BranchId, ".workspace/decisions", ct)
            .ConfigureAwait(false);

        var recorded = new List<ArtifactRef>();
        foreach (var relativePath in files)
        {
            var fileNumber = ExtractFileNumber(relativePath);
            if (fileNumber is null)
                continue;

            var content = await fileWorkspace.ReadAsync(wu.BranchId, relativePath, ct).ConfigureAwait(false);
            if (content is null)
                continue;

            var entry = ParseDecisionEntry(content);
            if (entry is null)
                continue;
            if (!Enum.TryParse<ArtifactType>(entry.Type, ignoreCase: true, out var artifactType) ||
                !RecordableTypes.Contains(artifactType))
                continue;
            if (artifactType == ArtifactType.Supersession && (entry.Supersedes is null or { Count: 0 }))
                continue;

            // Deterministic ArtifactId (not a fresh Guid, unlike ArtifactCommandService.RecordAsync)
            // — the harvest's idempotency property depends entirely on this being stable across
            // re-harvests of the same numbered file.
            var artifactId = $"WSD-{workUnitId}-{fileNumber.Value:0000}";
            var artifact = new ArtifactRef(
                artifactId, artifactType, ParentArtifactId: workUnitId, ArtifactStatus.Active,
                DateTimeOffset.UtcNow, OwnedByWorkUnitId: workUnitId, OwnedByAgentId: null,
                entry.Title, entry.Body, InvalidatedByArtifactId: null, Supersedes: entry.Supersedes);

            recorded.Add(await artifactLineage.RecordAsync(artifact, ct).ConfigureAwait(false));
        }

        return recorded;
    }

    public async Task<IReadOnlyList<WorkspaceContractInboxEntry>> HarvestInboxAsync(string workUnitId, CancellationToken ct = default)
    {
        var wu = await workUnits.GetAsync(workUnitId, ct).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Work unit '{workUnitId}' was not found.");

        var files = await fileWorkspace
            .ListIncludingDotfilesAsync(wu.BranchId, ".workspace/inbox", ct)
            .ConfigureAwait(false);

        var entries = new List<WorkspaceContractInboxEntry>();
        foreach (var relativePath in files)
        {
            var fileNumber = ExtractFileNumber(relativePath);
            if (fileNumber is null)
                continue;

            var content = await fileWorkspace.ReadAsync(wu.BranchId, relativePath, ct).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(content))
                continue;

            // No frontmatter needed — an inbox entry is just a question, so the whole file content
            // (trimmed) is the question text, unlike decisions/ which needs a type field.
            entries.Add(new WorkspaceContractInboxEntry(fileNumber.Value, content.Trim()));
        }

        return entries.OrderBy(e => e.Number).ToList();
    }

    private static int? ExtractFileNumber(string relativePath)
    {
        var stem = Path.GetFileNameWithoutExtension(relativePath);
        return int.TryParse(stem, out var number) ? number : null;
    }

    // Accepts either a JSON WorkspaceContractDecisionEntry or a markdown file with a small
    // frontmatter block (type/title/supersedes keys, body = everything after the closing "---") —
    // an LLM harness told to "append a decision entry" produces markdown far more reliably than
    // JSON, per contract principle WC-8.
    private static WorkspaceContractDecisionEntry? ParseDecisionEntry(string content)
    {
        var trimmed = content.TrimStart();
        if (trimmed.StartsWith('{'))
        {
            try
            {
                return JsonSerializer.Deserialize<WorkspaceContractDecisionEntry>(content, JsonSerializerOptions.Web);
            }
            catch (JsonException)
            {
                return null;
            }
        }

        return ParseFrontmatterMarkdown(content);
    }

    private static WorkspaceContractDecisionEntry? ParseFrontmatterMarkdown(string content)
    {
        const string delimiter = "---";
        var lines = content.Replace("\r\n", "\n").Split('\n');
        var startIndex = Array.FindIndex(lines, l => l.Trim() == delimiter);
        if (startIndex < 0)
            return null;
        var endIndex = Array.FindIndex(lines, startIndex + 1, l => l.Trim() == delimiter);
        if (endIndex < 0)
            return null;

        string? type = null;
        string? title = null;
        IReadOnlyList<string>? supersedes = null;
        foreach (var line in lines[(startIndex + 1)..endIndex])
        {
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex < 0)
                continue;
            var key = line[..separatorIndex].Trim().ToLowerInvariant();
            var value = line[(separatorIndex + 1)..].Trim();
            switch (key)
            {
                case "type": type = value; break;
                case "title": title = value; break;
                case "supersedes":
                    supersedes = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                    break;
            }
        }

        if (type is null)
            return null;

        var body = string.Join('\n', lines[(endIndex + 1)..]).Trim();
        return new WorkspaceContractDecisionEntry(type, title ?? "(untitled)", body, supersedes);
    }

    private async Task WriteAsync<T>(
        string branchId, string fileStem, T value, Func<T, string> renderMarkdown, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await fileWorkspace.WriteAsync(branchId, $".workspace/{fileStem}.json", json, ct).ConfigureAwait(false);
        await fileWorkspace.WriteAsync(branchId, $".workspace/{fileStem}.md", renderMarkdown(value), ct).ConfigureAwait(false);
    }

    private async Task<WorkUnit> ResolveRootAsync(WorkUnit wu, CancellationToken ct)
    {
        var current = wu;
        while (current.ParentWorkUnitId is { } parentId)
        {
            var parent = await workUnits.GetAsync(parentId, ct).ConfigureAwait(false);
            if (parent is null) break;
            current = parent;
        }
        return current;
    }

    private static string RuntimeVersion() =>
        typeof(WorkspaceContractService).Assembly.GetName().Version?.ToString() ?? "0.0.0";

    private static string RenderManifestMarkdown(WorkspaceContractManifest m) =>
        $"""
        # Workspace manifest

        - Contract version: {m.ContractVersion}
        - Runtime version: {m.RuntimeVersion}
        - Goal ID: {m.GoalId}
        - Work unit ID: {m.WorkUnitId}
        - Capabilities: {string.Join(", ", m.Capabilities)}
        """;

    private static string RenderGoalMarkdown(WorkspaceContractGoal g) =>
        $"""
        # Goal

        {g.Goal}

        - Goal ID: {g.GoalId}
        - Success criteria: {g.SuccessCriteria ?? "(none)"}
        - Parent goal ID: {g.ParentGoalId ?? "(none — root goal)"}
        """;

    private static string RenderWorkUnitMarkdown(WorkspaceContractWorkUnit w) =>
        $"""
        # Work unit {w.WorkUnitId}

        - Branch: {w.BranchId}
        - Parent work unit: {w.ParentWorkUnitId ?? "(none)"}
        - File scope: {(w.FileScope.Count == 0 ? "(unscoped)" : string.Join(", ", w.FileScope))}
        - Depends on: {(w.DependsOn.Count == 0 ? "(none)" : string.Join(", ", w.DependsOn))}
        """;

    private static string RenderReviewPolicyMarkdown(WorkspaceContractReviewPolicy p) =>
        $"""
        # Review policy

        - Task review policy: {p.TaskReviewPolicy}
        - Workspace review policy: {p.WorkspaceReviewPolicy}
        - Self-verify build required: {p.SelfVerifyBuildRequired}
        - Self-verify test required: {p.SelfVerifyTestRequired}
        """;

    private static string RenderEngineeringStateMarkdown(EngineeringStateProjectionPayload state)
    {
        if (state.Facts.Count == 0)
            return "# Engineering state\n\nNo promoted facts yet.\n";

        var lines = state.Facts.Select(f =>
        {
            var supersededNote = f.IsCurrent ? "" : $" — superseded by {string.Join(", ", f.SupersededBy)}";
            return $"- [{f.Type}] {f.Title ?? f.ArtifactId} ({f.ArtifactId}){supersededNote}\n  {f.Body}";
        });
        return "# Engineering state\n\n" + string.Join("\n\n", lines) + "\n";
    }
}
