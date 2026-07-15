using Microsoft.Extensions.Logging.Abstractions;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.AgentRuntime.Tests;

public class DomainAgentTriggerServiceTests
{
    private static ArtifactRef MakeArtifact(
        ArtifactType type = ArtifactType.Constraint,
        string title = "JWT handling",
        string body = "Adds OAuth token validation",
        string? ownedByWorkUnitId = "wu-1") =>
        new("KA-1", type, "wu-1", ArtifactStatus.Active, DateTimeOffset.UtcNow, ownedByWorkUnitId, null, title, body);

    private sealed class FakeAgentControl : IAgentControlService
    {
        public IReadOnlyList<string>? EnabledDomainAgentsOverride;
        public GoalDefaultCredentials? Credentials;
        public int CredentialsRequested;

        public IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId) => EnabledDomainAgentsOverride;
        public GoalDefaultCredentials? GetGoalDefaultCredentials(string workUnitId)
        {
            CredentialsRequested++;
            return Credentials;
        }

        public Task<string> SpawnAsync(string agentType, string workUnitId, string? taskId = null, string? model = null,
            string? baseUrl = null, string? apiKey = null, string? provider = null, string? profileId = null,
            string? autoReviewProfileId = null, IReadOnlyDictionary<PipelineStage, GoalDefaultCredentials>? stageCredentials = null,
            IReadOnlyList<string>? enabledDomainAgents = null, string? credentialRef = null, bool lenientToolParsing = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, string? overrideModel = null, string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null, string? overrideCredentialRef = null, bool ensurePlanner = false, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task<bool> ResupplyCredentialsAsync(string workUnitId, string? overrideModel = null, string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null, string? overrideCredentialRef = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public GoalDefaultCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage) => null;
        public string? GetAutoReviewProfileId(string workUnitId) => null;
        public string? GetGoalDefaultProfileId(string workUnitId) => null;
        public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default) => Task.FromResult("unknown");
        public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentInfo>>([]);
        public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentInfo>>([]);
        public Task<TResult> TrackInlineAgentAsync<TResult>(string agentId, string workUnitId, string? taskId, Func<Action<string?>, Task<TResult>> run, CancellationToken cancellationToken = default) =>
            run(_ => { });
    }

    private sealed class NoopServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }

    private static DomainAgentTriggerService Build(FakeAgentControl agentControl, WorkspaceOptions? options = null) =>
        new(agentControl, new NoopServiceProvider(), options ?? new WorkspaceOptions(),
            NullLogger<DomainAgentTriggerService>.Instance);

    [Fact]
    public async Task NotifyArtifactRecordedAsync_does_not_request_credentials_when_disabled_by_default()
    {
        var agentControl = new FakeAgentControl(); // no override -> falls back to WorkspaceOptions default ([])
        var svc = Build(agentControl, new WorkspaceOptions { EnabledDomainAgents = [] });

        await svc.NotifyArtifactRecordedAsync(MakeArtifact());

        Assert.Equal(0, agentControl.CredentialsRequested);
    }

    [Fact]
    public async Task NotifyArtifactRecordedAsync_does_not_request_credentials_when_artifact_not_relevant_to_any_enabled_agent()
    {
        var agentControl = new FakeAgentControl { EnabledDomainAgentsOverride = ["Security", "Architecture"] };
        var svc = Build(agentControl);

        await svc.NotifyArtifactRecordedAsync(MakeArtifact(title: "Add caching layer", body: "in-memory dictionary cache"));

        Assert.Equal(0, agentControl.CredentialsRequested);
    }

    [Fact]
    public async Task NotifyArtifactRecordedAsync_does_not_request_credentials_for_any_domain_agents_own_prior_output()
    {
        var agentControl = new FakeAgentControl { EnabledDomainAgentsOverride = ["Security", "Architecture"] };
        var svc = Build(agentControl);

        await svc.NotifyArtifactRecordedAsync(MakeArtifact(
            title: DomainAgentRegistry.Security.TitlePrefix + "Missing threat model for token rotation"));

        Assert.Equal(0, agentControl.CredentialsRequested);
    }

    [Fact]
    public async Task NotifyArtifactRecordedAsync_cross_agent_guard_blocks_other_agents_prefixed_titles_too()
    {
        // An Architecture-keyword body shouldn't matter — the title carries Security's prefix, so
        // no domain agent (not even Architecture) should react to it.
        var agentControl = new FakeAgentControl { EnabledDomainAgentsOverride = ["Security", "Architecture"] };
        var svc = Build(agentControl);

        await svc.NotifyArtifactRecordedAsync(MakeArtifact(
            title: DomainAgentRegistry.Security.TitlePrefix + "Some finding",
            body: "Introduces a new microservice module boundary"));

        Assert.Equal(0, agentControl.CredentialsRequested);
    }

    [Fact]
    public async Task NotifyArtifactRecordedAsync_does_not_request_credentials_for_non_knowledge_artifact_types()
    {
        var agentControl = new FakeAgentControl { EnabledDomainAgentsOverride = ["Security"] };
        var svc = Build(agentControl);

        await svc.NotifyArtifactRecordedAsync(MakeArtifact(type: ArtifactType.Plan));

        Assert.Equal(0, agentControl.CredentialsRequested);
    }

    [Fact]
    public async Task NotifyArtifactRecordedAsync_requests_credentials_when_enabled_and_relevant()
    {
        var agentControl = new FakeAgentControl { EnabledDomainAgentsOverride = ["Security"], Credentials = null };
        var svc = Build(agentControl);

        await svc.NotifyArtifactRecordedAsync(MakeArtifact());

        // Credentials resolve to null here, so the loop never spawns (no-credentials no-op) — but
        // the gate itself (enabled + relevant + not self-triggered) must reach that check.
        Assert.Equal(1, agentControl.CredentialsRequested);
    }

    [Fact]
    public async Task NotifyArtifactRecordedAsync_only_spawns_the_definitions_whose_keywords_actually_match()
    {
        // Both agents enabled, but the artifact is only relevant to Security — Architecture must
        // not request credentials at all.
        var agentControl = new FakeAgentControl { EnabledDomainAgentsOverride = ["Security", "Architecture"] };
        var svc = Build(agentControl);

        await svc.NotifyArtifactRecordedAsync(MakeArtifact(title: "JWT handling", body: "Adds OAuth token validation"));

        Assert.Equal(1, agentControl.CredentialsRequested);
    }

    [Fact]
    public async Task NotifyArtifactRecordedAsync_per_work_unit_override_takes_priority_over_global_default()
    {
        // Global default enables Security, but this work unit explicitly opts out via an empty override.
        var agentControl = new FakeAgentControl { EnabledDomainAgentsOverride = [] };
        var svc = Build(agentControl, new WorkspaceOptions { EnabledDomainAgents = ["Security"] });

        await svc.NotifyArtifactRecordedAsync(MakeArtifact());

        Assert.Equal(0, agentControl.CredentialsRequested);
    }

    [Fact]
    public async Task NotifyArtifactRecordedAsync_never_throws_when_no_owning_work_unit()
    {
        var agentControl = new FakeAgentControl { EnabledDomainAgentsOverride = ["Security"] };
        var svc = Build(agentControl);

        await svc.NotifyArtifactRecordedAsync(MakeArtifact(ownedByWorkUnitId: null));

        Assert.Equal(0, agentControl.CredentialsRequested);
    }
}
