using System.Collections.Concurrent;
using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Contracts.Versioning;

namespace NodalMerge.Studio.Storage;

public sealed class AgentProfileService : IAgentProfileService, IRehydratable
{
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
            new(
                "orchestrator",
                "Orchestrator",
                PipelineStage.Orchestrate,
                string.Empty,
                [],
                25,
                []),
            new(
                "planner",
                "Planner",
                PipelineStage.Plan,
                string.Empty,
                [
                    McpToolNames.WorkUnitGet,
                    McpToolNames.WorkspaceSummary,
                    McpToolNames.WorkspaceRead,
                    McpToolNames.WorkspaceWrite,
                    McpToolNames.WorkspaceList,
                ],
                15,
                []),
            new(
                "worker",
                "Worker",
                PipelineStage.Execute,
                string.Empty,
                [
                    McpToolNames.TaskUpdate,
                    McpToolNames.TaskList,
                    McpToolNames.TaskAssign,
                    McpToolNames.WorkspaceRead,
                    McpToolNames.WorkspaceWrite,
                    McpToolNames.WorkspaceDelete,
                    McpToolNames.MergePropose,
                    McpToolNames.MergeValidate,
                    McpToolNames.BranchCreate,
                    McpToolNames.BranchStatus,
                    McpToolNames.SnapshotGet,
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
                string.Empty,
                [
                    McpToolNames.MergePropose,
                    McpToolNames.MergeValidate,
                    McpToolNames.WorkspaceRead,
                    McpToolNames.WorkspaceWrite,
                    McpToolNames.WorkspaceDiff,
                    McpToolNames.ProjectionGet,
                ],
                15,
                []),
            new(
                "reviewer",
                "Reviewer",
                PipelineStage.Review,
                string.Empty,
                [
                    McpToolNames.WorkUnitGet,
                    McpToolNames.MergeValidate,
                    McpToolNames.MergeReview,
                    McpToolNames.ProjectionGet,
                    McpToolNames.WorkspaceRead,
                ],
                10,
                []),
        };

        foreach (var profile in defaults)
            _profiles.TryAdd(profile.AgentProfileId, profile);
    }

    public async Task<AgentProfile> CreateAsync(AgentProfile profile, CancellationToken cancellationToken = default)
    {
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
            if (profile is not null)
                _profiles[entityId] = profile;
        }
    }

    private Task Persist(AgentProfile profile, CancellationToken ct) =>
        _nodeStore.WriteNodeAsync(
            StudioNodeKind.AgentProfileV1,
            profile.AgentProfileId,
            JsonSerializer.Serialize(profile),
            ct);
}
