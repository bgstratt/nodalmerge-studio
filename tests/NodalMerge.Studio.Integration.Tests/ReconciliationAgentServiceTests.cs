using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// IReconciliationAgentService is deliberately source-agnostic — it knows nothing about
/// CandidateConflictRecord/CandidateBranchId (that's the candidate-branch adapter's job, see
/// CandidateBranchConflictTests). These tests exercise the core directly with a plain seed branch
/// and two ordinary proposals to confirm it doesn't implicitly depend on the candidate-branch
/// machinery existing at all.
/// </summary>
[Trait("Category", "Integration")]
public class ReconciliationAgentServiceTests
{
    [Fact]
    public async Task TriggerAsync_creates_a_work_unit_seeded_from_the_requested_branch_with_both_goals_context()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var reconciliation = app.Services.GetRequiredService<IReconciliationAgentService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\nline two\n");

        var goalA = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Rename the helper to Foo", "test", SuccessCriteria: "Foo exists", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(
            new WorkUnitCreateCommand("Rename the helper to Bar", "test", SeedFromBranchId: "main"));

        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "line one FOO\nline two\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "line one BAR\nline two\n");

        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: goalA.BranchId, targetBranch: "main", summary: "Rename to Foo", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: goalB.BranchId, targetBranch: "main", summary: "Rename to Bar", workUnitId: goalB.WorkUnitId);

        var request = new ReconciliationRequest(
            SeedBranchId: "main",
            ProposalIds: [proposalA.ProposalId, proposalB.ProposalId],
            ConflictingPaths: ["Shared.cs"],
            SourceRef: "test:no-source-subsystem");

        var workUnit = await reconciliation.TriggerAsync(request);

        Assert.Equal([proposalA.ProposalId, proposalB.ProposalId], workUnit.ReconciliationSourceProposalIds);
        Assert.Equal(["Shared.cs"], workUnit.ReconciliationTargetPaths);
        Assert.Equal("test:no-source-subsystem", workUnit.ReconciliationSourceRef);
        Assert.Contains("Rename the helper to Foo", workUnit.Goal);
        Assert.Contains("Rename the helper to Bar", workUnit.Goal);
        Assert.Contains("Shared.cs", workUnit.Goal);

        // Seeded from the requested branch ("main"), untouched by either goal's own edits.
        Assert.Equal("line one\nline two\n", await fileWorkspace.ReadAsync(workUnit.BranchId, "Shared.cs"));
    }

    [Fact]
    public async Task TriggerAsync_throws_with_fewer_than_two_proposals()
    {
        var app = StudioWebApplication.Build([], configureServices: services => services.AddInMemoryStorage());
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var reconciliation = app.Services.GetRequiredService<IReconciliationAgentService>();

        var goal = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Solo goal", "test"));
        await fileWorkspace.WriteAsync(goal.BranchId, "A.cs", "content");
        var proposal = await mergeCommands.ProposeAsync(
            sourceBranch: goal.BranchId, targetBranch: "main", summary: "A", workUnitId: goal.WorkUnitId);

        var request = new ReconciliationRequest("main", [proposal.ProposalId], ["A.cs"], "test:solo");

        await Assert.ThrowsAsync<InvalidOperationException>(() => reconciliation.TriggerAsync(request));
    }

    /// <summary>
    /// One-click Reconcile: TriggerAsync should spawn the orchestrator immediately using whichever
    /// source goal's own credentials it can find, instead of leaving the work unit at Created for a
    /// human to notice and Spawn manually.
    /// </summary>
    [Fact]
    public async Task TriggerAsync_spawns_the_orchestrator_using_a_source_goals_credentials()
    {
        var spawnCalls = new List<(string AgentType, string WorkUnitId, string? Model)>();
        var fakeAgentControl = new FakeAgentControlService(spawnCalls);

        var app = StudioWebApplication.Build([], configureServices: services =>
        {
            services.AddInMemoryStorage();
            services.AddSingleton<IAgentControlService>(fakeAgentControl);
        });

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var reconciliation = app.Services.GetRequiredService<IReconciliationAgentService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));
        fakeAgentControl.CredentialsByWorkUnitId[goalA.WorkUnitId] =
            new OrchestratorCredentials("anthropic", "claude-x", "https://example.test", "sk-test", "worker");

        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "B\n");
        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: goalA.BranchId, targetBranch: "main", summary: "A", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: goalB.BranchId, targetBranch: "main", summary: "B", workUnitId: goalB.WorkUnitId);

        var request = new ReconciliationRequest(
            SeedBranchId: "main",
            ProposalIds: [proposalA.ProposalId, proposalB.ProposalId],
            ConflictingPaths: ["Shared.cs"],
            SourceRef: "test:spawn");

        var workUnit = await reconciliation.TriggerAsync(request);

        var call = Assert.Single(spawnCalls);
        Assert.Equal("orchestrator", call.AgentType);
        Assert.Equal(workUnit.WorkUnitId, call.WorkUnitId);
        Assert.Equal("claude-x", call.Model);
    }

    [Fact]
    public async Task TriggerAsync_does_not_spawn_when_no_source_goal_has_resolvable_credentials()
    {
        var spawnCalls = new List<(string AgentType, string WorkUnitId, string? Model)>();
        var fakeAgentControl = new FakeAgentControlService(spawnCalls);

        var app = StudioWebApplication.Build([], configureServices: services =>
        {
            services.AddInMemoryStorage();
            services.AddSingleton<IAgentControlService>(fakeAgentControl);
        });

        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var mergeCommands = app.Services.GetRequiredService<IMergeCommandService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var reconciliation = app.Services.GetRequiredService<IReconciliationAgentService>();

        await fileWorkspace.WriteAsync("main", "Shared.cs", "line one\n");
        var goalA = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal A", "test", SeedFromBranchId: "main"));
        var goalB = await workUnitCommands.CreateAsync(new WorkUnitCreateCommand("Goal B", "test", SeedFromBranchId: "main"));

        await fileWorkspace.WriteAsync(goalA.BranchId, "Shared.cs", "A\n");
        await fileWorkspace.WriteAsync(goalB.BranchId, "Shared.cs", "B\n");
        var proposalA = await mergeCommands.ProposeAsync(
            sourceBranch: goalA.BranchId, targetBranch: "main", summary: "A", workUnitId: goalA.WorkUnitId);
        var proposalB = await mergeCommands.ProposeAsync(
            sourceBranch: goalB.BranchId, targetBranch: "main", summary: "B", workUnitId: goalB.WorkUnitId);

        var request = new ReconciliationRequest(
            SeedBranchId: "main",
            ProposalIds: [proposalA.ProposalId, proposalB.ProposalId],
            ConflictingPaths: ["Shared.cs"],
            SourceRef: "test:no-creds");

        // Must not throw even though no credentials resolve — the work unit is still created,
        // just left at Created for a human to Spawn manually.
        var workUnit = await reconciliation.TriggerAsync(request);

        Assert.Empty(spawnCalls);
        Assert.Equal("Created", workUnit.Status.ToString());
    }

    private sealed class FakeAgentControlService(List<(string AgentType, string WorkUnitId, string? Model)> spawnCalls) : IAgentControlService
    {
        public Dictionary<string, OrchestratorCredentials> CredentialsByWorkUnitId { get; } = new();

        public OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId) =>
            CredentialsByWorkUnitId.TryGetValue(workUnitId, out var creds) ? creds : null;

        public OrchestratorCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage) => null;

        public string? GetAutoReviewProfileId(string workUnitId) => null;
        public string? GetOrchestratorProfileId(string workUnitId) => null;

        public IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId) => null;

        public Task<string> SpawnAsync(
            string agentType,
            string workUnitId,
            string? taskId = null,
            string? model = null,
            string? baseUrl = null,
            string? apiKey = null,
            string? provider = null,
            string? profileId = null,
            string? autoReviewProfileId = null,
            IReadOnlyDictionary<PipelineStage, OrchestratorCredentials>? stageCredentials = null,
            IReadOnlyList<string>? enabledDomainAgents = null,
            string? credentialRef = null,
            CancellationToken cancellationToken = default)
        {
            spawnCalls.Add((agentType, workUnitId, model));
            return Task.FromResult("agent-fake");
        }

        public Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, string? overrideModel = null, string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null, string? overrideCredentialRef = null, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> ResupplyCredentialsAsync(string workUnitId, string? overrideModel = null, string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null, string? overrideCredentialRef = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StopAsync(string agentId, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default) =>
            Task.FromResult("active");

        public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentInfo>>([]);

        public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<AgentInfo>>([]);

        public Task<TResult> TrackInlineAgentAsync<TResult>(string agentId, string workUnitId, string? taskId, Func<Action<string?>, Task<TResult>> run, CancellationToken cancellationToken = default) =>
            run(_ => { });
    }
}
