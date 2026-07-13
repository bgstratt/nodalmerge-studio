using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.Storage;

public sealed class AgentProfileService : IAgentProfileService, IRehydratable
{
    // Discovery tools a profile for this stage can't function without, regardless of how its
    // AllowedTools list was customized — a worker that can't list/profile the workspace is stuck
    // guessing file paths (see the Program.cs dead-letter this was added to fix). Enforced on
    // every load/save below so neither a persisted profile from before this list existed, nor a
    // future hand-edit via the profile editor, can silently drop them.
    //
    // WorkspaceSearch is required everywhere ListAsync/ProfileGet are: content search is now the
    // primary discovery primitive (filename matching alone reproduces the same guessing problem).
    // ArtifactQuery is required for Plan/Review because recorded Constraints/Decisions are a
    // governance mechanism — a Planner or Reviewer profile that drops it can silently re-derive or
    // ignore a constraint an earlier work unit already established.
    private static readonly IReadOnlyDictionary<PipelineStage, string[]> RequiredToolsByStage = new Dictionary<PipelineStage, string[]>
    {
        [PipelineStage.Execute] = [McpToolNames.WorkspaceList, McpToolNames.WorkspaceProfileGet, McpToolNames.WorkspaceSearch],
        [PipelineStage.Plan]    = [McpToolNames.WorkspaceList, McpToolNames.WorkspaceProfileGet, McpToolNames.WorkspaceSearch, McpToolNames.ArtifactQuery],
        [PipelineStage.Review]  = [McpToolNames.WorkspaceSearch, McpToolNames.ArtifactQuery],
    };

    private static AgentProfile EnsureRequiredTools(AgentProfile profile)
    {
        if (!RequiredToolsByStage.TryGetValue(profile.Stage, out var required))
            return profile;

        var missing = required.Where(t => !profile.AllowedTools.Contains(t)).ToArray();
        return missing.Length == 0
            ? profile
            : profile with { AllowedTools = [.. profile.AllowedTools, .. missing] };
    }

    private readonly ConcurrentDictionary<string, AgentProfile> _profiles = new();
    private readonly IStudioNodeStore _nodeStore;

    public AgentProfileService(IStudioNodeStore nodeStore)
    {
        _nodeStore = nodeStore;
        SeedDefaults();
    }

    private void SeedDefaults()
    {
        var defaults = new AgentProfile[]
        {
            // plans/orchestrator-pure-service.md M2 — goal coordination is the deterministic
            // GoalCoordinator now, so this profile runs no LLM loop and has no system prompt. It
            // survives (same id, relabeled "Default") because it's the goal's Default-profile
            // credential anchor: Agent Topology's mandatory slot and the profile every unset role
            // inherits.
            new(
                "orchestrator",
                "Default",
                PipelineStage.Orchestrate,
                string.Empty,
                [],
                25,
                []),
            new(
                "planner",
                "Planner",
                PipelineStage.Plan,
                AgentLoopPrompts.Planner,
                [
                    McpToolNames.WorkUnitGet,
                    McpToolNames.WorkspaceSummary,
                    McpToolNames.WorkspaceStatus,
                    McpToolNames.WorkspaceProfileGet,
                    McpToolNames.WorkspaceRead,
                    McpToolNames.WorkspaceReadMany,
                    McpToolNames.WorkspaceWrite,
                    McpToolNames.WorkspaceList,
                    McpToolNames.WorkspaceSearch,
                    McpToolNames.WorkspaceSymbolDefinition,
                    McpToolNames.WorkspaceSymbolReferences,
                    McpToolNames.WorkspaceSymbolImplementation,
                    McpToolNames.ClarificationRequest,
                    McpToolNames.ArtifactQuery,
                    McpToolNames.ArtifactRecordPlan,
                ],
                15,
                []),
            new(
                "worker",
                "Worker",
                PipelineStage.Execute,
                AgentLoopPrompts.Worker,
                [
                    McpToolNames.WorkUnitGet,
                    McpToolNames.TaskUpdate,
                    McpToolNames.WorkspaceSummary,
                    McpToolNames.WorkspaceStatus,
                    McpToolNames.WorkspaceRead,
                    McpToolNames.WorkspaceReadMany,
                    McpToolNames.WorkspaceWrite,
                    McpToolNames.WorkspaceReplace,
                    McpToolNames.WorkspaceDelete,
                    McpToolNames.WorkspaceExists,
                    McpToolNames.WorkspaceList,
                    McpToolNames.WorkspaceSearch,
                    McpToolNames.WorkspaceSymbolDefinition,
                    McpToolNames.WorkspaceSymbolReferences,
                    McpToolNames.WorkspaceSymbolImplementation,
                    McpToolNames.ClarificationRequest,
                    McpToolNames.WorkspaceProfileGet,
                    McpToolNames.WorkspaceBuild,
                    McpToolNames.WorkspaceTest,
                    McpToolNames.WorkspaceExec,
                    McpToolNames.WorkspaceDiff,
                    McpToolNames.MergePropose,
                    McpToolNames.MergeValidate,
                    McpToolNames.ArtifactRecord,
                    McpToolNames.ArtifactQuery,
                    McpToolNames.ArtifactList,
                ],
                20,
                []),
            new(
                "merger",
                "Merger",
                PipelineStage.Merge,
                AgentLoopPrompts.Merger,
                [
                    McpToolNames.WorkUnitGet,
                    McpToolNames.ProjectionGet,
                    McpToolNames.WorkspaceRead,
                    McpToolNames.WorkspaceReadMany,
                    McpToolNames.WorkspaceWrite,
                    McpToolNames.WorkspaceList,
                    McpToolNames.WorkspaceDiff,
                    McpToolNames.MergePropose,
                    McpToolNames.MergeValidate,
                    McpToolNames.ClarificationRequest,
                ],
                15,
                []),
            new(
                "reconciler",
                "Reconciler",
                PipelineStage.Reconcile,
                AgentLoopPrompts.Reconciler,
                [
                    McpToolNames.WorkUnitGet,
                    McpToolNames.ProjectionGet,
                    McpToolNames.TaskUpdate,
                    McpToolNames.WorkspaceRead,
                    McpToolNames.WorkspaceReadMany,
                    McpToolNames.WorkspaceWrite,
                    McpToolNames.WorkspaceList,
                    McpToolNames.WorkspaceSearch,
                    McpToolNames.WorkspaceDiff,
                    McpToolNames.MergePropose,
                    McpToolNames.MergeValidate,
                    McpToolNames.ArtifactQuery,
                    McpToolNames.ArtifactList,
                    McpToolNames.ArtifactRecord,
                    McpToolNames.ClarificationRequest,
                ],
                15,
                []),
            new(
                "reviewer",
                "Reviewer",
                PipelineStage.Review,
                AgentLoopPrompts.Reviewer,
                [
                    McpToolNames.WorkUnitGet,
                    McpToolNames.MergeValidate,
                    McpToolNames.MergeReview,
                    McpToolNames.ProjectionGet,
                    McpToolNames.WorkspaceRead,
                    McpToolNames.WorkspaceReadMany,
                    McpToolNames.WorkspaceList,
                    McpToolNames.WorkspaceSearch,
                    McpToolNames.WorkspaceSymbolDefinition,
                    McpToolNames.WorkspaceSymbolReferences,
                    McpToolNames.WorkspaceSymbolImplementation,
                    McpToolNames.ClarificationRequest,
                    McpToolNames.WorkspaceDiff,
                    McpToolNames.WorkspaceProfileGet,
                    McpToolNames.WorkspaceBuild,
                    McpToolNames.WorkspaceTest,
                    McpToolNames.ArtifactQuery,
                ],
                // 10 -> 14: the build/test verification step (workspace_profile_get + scoped build +
                // scoped test) adds 2-3 tool calls on top of the original projection/artifact/diff/
                // review sequence.
                14,
                []),
        };

        foreach (var profile in defaults)
            _profiles.TryAdd(profile.AgentProfileId, profile);
    }

    public async Task<AgentProfile> CreateAsync(AgentProfile profile, CancellationToken cancellationToken = default)
    {
        profile = EnsureRequiredTools(profile);
        _profiles[profile.AgentProfileId] = profile;
        await Persist(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public Task<AgentProfile?> GetAsync(string profileId, CancellationToken cancellationToken = default)
    {
        _profiles.TryGetValue(profileId, out var profile);
        return Task.FromResult(profile);
    }

    public async Task<AgentProfile> UpdateAsync(AgentProfile profile, CancellationToken cancellationToken = default)
    {
        if (!_profiles.ContainsKey(profile.AgentProfileId))
            throw new KeyNotFoundException($"Agent profile '{profile.AgentProfileId}' was not found.");
        profile = EnsureRequiredTools(profile);
        _profiles[profile.AgentProfileId] = profile;
        await Persist(profile, cancellationToken).ConfigureAwait(false);
        return profile;
    }

    public Task<IReadOnlyList<AgentProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<AgentProfile>>(_profiles.Values.OrderBy(p => p.AgentProfileId).ToList());

    // Runs after the constructor's SeedDefaults(), so any persisted, user-customized profile
    // (e.g. an edited "worker" prompt) correctly overwrites the seeded default here.
    public async Task RehydrateAsync(CancellationToken cancellationToken = default)
    {
        var records = await _nodeStore.ReadAllNodesAsync(StudioNodeKind.AgentProfileV1, cancellationToken)
            .ConfigureAwait(false);
        foreach (var (entityId, payloadJson) in records)
        {
            var profile = JsonSerializer.Deserialize<AgentProfile>(payloadJson);
            if (profile is null) continue;

            // If a previously-persisted profile has a blank system prompt but the seed now carries
            // a real default, inherit the seed prompt so the user sees it in the UI rather than
            // an empty textarea. An explicitly non-empty persisted prompt always wins.
            if (string.IsNullOrWhiteSpace(profile.SystemPrompt)
                && _profiles.TryGetValue(entityId, out var seeded)
                && !string.IsNullOrWhiteSpace(seeded.SystemPrompt))
            {
                profile = profile with { SystemPrompt = seeded.SystemPrompt };
            }

            _profiles[entityId] = EnsureRequiredTools(profile);
        }
    }

    private Task Persist(AgentProfile profile, CancellationToken ct) =>
        _nodeStore.WriteNodeAsync(
            StudioNodeKind.AgentProfileV1,
            profile.AgentProfileId,
            JsonSerializer.Serialize(profile),
            ct);
}
