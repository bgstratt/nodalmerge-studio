using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Orchestrator;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers Phase 4 slice 11a's WorkUnitStatus expansion: UpdateStatusAsync's new transitions,
/// the WorkUnitStatusChanged event it now emits, and that illegal transitions still throw
/// (the throw-based internal pattern is kept — see the as-built note in phase-4-fanout-merger.md).
/// </summary>
[Trait("Category", "Integration")]
public class WorkUnitLifecycleTests
{
    private static (InMemoryWorkUnitService Svc, ExecutionEventStreamService Events) Build(
        IRuntimeEventBroadcaster? broadcaster = null,
        IStudioGraphPromoter? graphPromoter = null)
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var svc = new InMemoryWorkUnitService(
            new NoopBranchService(),
            new NoopMergeService(),
            new NoopKnownGoodStateService(),
            new NoopAgentControlService(),
            store,
            new ArtifactLineageService(store),
            new WorkspaceOptions(),
            events,
            broadcaster,
            graphPromoter);
        return (svc, events);
    }

    private static WorkUnit MakeWorkUnit(string id) => new(
        id, "goal", "branch-1", WorkUnitStatus.Created, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        "owner", null, null, null, null, [], []);

    [Fact]
    public async Task UpdateStatusAsync_transitions_Created_to_Queued()
    {
        var (svc, _) = Build();
        await svc.CreateAsync(MakeWorkUnit("WU-1"));

        var updated = await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Queued);

        Assert.Equal(WorkUnitStatus.Queued, updated.Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_illegal_transition_still_throws()
    {
        var (svc, _) = Build();
        await svc.CreateAsync(MakeWorkUnit("WU-1"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Merged));
    }

    [Fact]
    public async Task UpdateStatusAsync_emits_WorkUnitStatusChanged_when_sessionId_supplied()
    {
        var (svc, events) = Build();
        await svc.CreateAsync(MakeWorkUnit("WU-1"));

        await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Queued, sessionId: "SES-1");

        var sessionEvents = await events.GetSessionEventsAsync("SES-1");
        var ev = Assert.Single(sessionEvents, e => e.Kind == ExecutionEventKind.WorkUnitStatusChanged);
        var payload = System.Text.Json.JsonSerializer.Deserialize<WorkUnitStatusChangedPayload>(ev.PayloadJson)!;
        Assert.Equal(WorkUnitStatus.Created, payload.PreviousStatus);
        Assert.Equal(WorkUnitStatus.Queued, payload.NewStatus);
    }

    [Fact]
    public async Task UpdateStatusAsync_emits_no_event_when_sessionId_omitted()
    {
        var (svc, events) = Build();
        await svc.CreateAsync(MakeWorkUnit("WU-1"));

        await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Queued);

        var sessionEvents = await events.GetSessionEventsAsync("SES-1");
        Assert.Empty(sessionEvents);
    }

    [Fact]
    public async Task Full_queue_driven_lifecycle_reaches_Merged()
    {
        var (svc, _) = Build();
        await svc.CreateAsync(MakeWorkUnit("WU-1"));

        await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Queued);
        await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Executing);
        await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Proposed);
        var final = await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Merged);

        Assert.Equal(WorkUnitStatus.Merged, final.Status);
    }

    [Fact]
    public async Task Executing_can_retry_and_recover()
    {
        var (svc, _) = Build();
        await svc.CreateAsync(MakeWorkUnit("WU-1"));
        await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Queued);
        await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Executing);

        await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Retrying);
        var recovered = await svc.UpdateStatusAsync("WU-1", WorkUnitStatus.Executing);

        Assert.Equal(WorkUnitStatus.Executing, recovered.Status);
    }

    [Fact]
    public async Task SetCurrentStageAsync_broadcasts_when_broadcaster_present()
    {
        var broadcaster = new RecordingRuntimeEventBroadcaster();
        var (svc, _) = Build(broadcaster);
        await svc.CreateAsync(MakeWorkUnit("WU-1"));

        await svc.SetCurrentStageAsync("WU-1", PipelineStage.Execute);

        var call = Assert.Single(broadcaster.Calls);
        Assert.Equal("WU-1", call.WorkUnitId);
        Assert.Equal(PipelineStage.Execute, call.Stage);
    }

    [Fact]
    public async Task SetCurrentStageAsync_broadcasts_null_when_stage_cleared()
    {
        var broadcaster = new RecordingRuntimeEventBroadcaster();
        var (svc, _) = Build(broadcaster);
        await svc.CreateAsync(MakeWorkUnit("WU-1"));
        await svc.SetCurrentStageAsync("WU-1", PipelineStage.Merge);

        await svc.SetCurrentStageAsync("WU-1", null);

        Assert.Equal(2, broadcaster.Calls.Count);
        Assert.Null(broadcaster.Calls[^1].Stage);
    }

    [Fact]
    public async Task SetCurrentStageAsync_does_not_throw_when_no_broadcaster_configured()
    {
        var (svc, _) = Build();
        await svc.CreateAsync(MakeWorkUnit("WU-1"));

        var updated = await svc.SetCurrentStageAsync("WU-1", PipelineStage.Plan);

        Assert.Equal(PipelineStage.Plan, updated.CurrentStage);
    }

    [Fact]
    public async Task UpdateStatusAsync_triggers_graph_promotion_on_Completed()
    {
        var promoter = new RecordingGraphPromoter();
        var (svc, _) = Build(graphPromoter: promoter);
        await svc.CreateAsync(MakeWorkUnit("WU-comp"));
        await svc.UpdateStatusAsync("WU-comp", WorkUnitStatus.Queued);
        await svc.UpdateStatusAsync("WU-comp", WorkUnitStatus.Executing);
        await svc.UpdateStatusAsync("WU-comp", WorkUnitStatus.Completed);

        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task UpdateStatusAsync_triggers_graph_promotion_on_Merged()
    {
        var promoter = new RecordingGraphPromoter();
        var (svc, _) = Build(graphPromoter: promoter);
        await svc.CreateAsync(MakeWorkUnit("WU-merged"));
        await svc.UpdateStatusAsync("WU-merged", WorkUnitStatus.Queued);
        await svc.UpdateStatusAsync("WU-merged", WorkUnitStatus.Executing);
        await svc.UpdateStatusAsync("WU-merged", WorkUnitStatus.Proposed);
        await svc.UpdateStatusAsync("WU-merged", WorkUnitStatus.Merged);

        Assert.Equal(1, promoter.CallCount);
    }

    [Fact]
    public async Task UpdateStatusAsync_does_not_trigger_promotion_on_non_terminal_transitions()
    {
        var promoter = new RecordingGraphPromoter();
        var (svc, _) = Build(graphPromoter: promoter);
        await svc.CreateAsync(MakeWorkUnit("WU-active"));
        await svc.UpdateStatusAsync("WU-active", WorkUnitStatus.Queued);
        await svc.UpdateStatusAsync("WU-active", WorkUnitStatus.Executing);

        Assert.Equal(0, promoter.CallCount);
    }

    [Fact]
    public async Task UpdateStatusAsync_does_not_throw_when_no_promoter_configured()
    {
        var (svc, _) = Build();
        await svc.CreateAsync(MakeWorkUnit("WU-no-promoter"));
        await svc.UpdateStatusAsync("WU-no-promoter", WorkUnitStatus.Queued);
        await svc.UpdateStatusAsync("WU-no-promoter", WorkUnitStatus.Executing);
        var final = await svc.UpdateStatusAsync("WU-no-promoter", WorkUnitStatus.Completed);

        Assert.Equal(WorkUnitStatus.Completed, final.Status);
    }

    private sealed class RecordingGraphPromoter : IStudioGraphPromoter
    {
        public int CallCount { get; private set; }

        public Task TryPromoteStudioCheckpointAsync()
        {
            CallCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRuntimeEventBroadcaster : IRuntimeEventBroadcaster
    {
        public List<(string WorkUnitId, PipelineStage? Stage)> Calls { get; } = [];

        public Task BroadcastWorkUnitStageChangedAsync(
            string workUnitId, PipelineStage? stage, CancellationToken cancellationToken = default)
        {
            Calls.Add((workUnitId, stage));
            return Task.CompletedTask;
        }

        public Task BroadcastArtifactInvalidatedAsync(
            string? workUnitId, string artifactId, IReadOnlyList<string> flaggedArtifactIds, string reason,
            CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoopBranchService : IBranchService
    {
        public Task<string> CreateBranchAsync(string name, string? fromBranchId = null, IReadOnlyList<string>? fileScope = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task CheckoutBranchAsync(string branchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListBranchesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BranchStatus> GetStatusAsync(string branchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopMergeService : IMergeService
    {
        public Task<MergeProposal> ProposeAsync(MergeProposal proposal, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal> ReviewAsync(string proposalId, MergeProposalStatus decision, string? notes = null, string? reviewedBy = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal> AutomatedReviewAsync(string proposalId, MergeProposalStatus decision, string verificationResults, string? reviewerAgentId = null, IReadOnlyList<string>? consideredArtifactIds = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default, bool autoApplied = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal> SupersedeAsync(string proposalId, string supersededByProposalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PromoteResult> PromoteAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopKnownGoodStateService : IKnownGoodStateService
    {
        public Task<KnownGoodState> MarkKnownGoodAsync(KnownGoodState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnownGoodState>> FindKnownGoodAsync(string branchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnownGoodState?> CheckoutKnownGoodAsync(string stateId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnownGoodState?> GetAsync(string stateId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopAgentControlService : IAgentControlService
    {
        public Task<string> SpawnAsync(string agentType, string workUnitId, string? taskId = null, string? model = null,
            string? baseUrl = null, string? apiKey = null, string? provider = null, string? profileId = null,
            string? autoReviewProfileId = null, IReadOnlyDictionary<PipelineStage, GoalDefaultCredentials>? stageCredentials = null,
            IReadOnlyList<string>? enabledDomainAgents = null,
            string? credentialRef = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, string? overrideModel = null, string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null, string? overrideCredentialRef = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<bool> ResupplyCredentialsAsync(string workUnitId, string? overrideModel = null, string? overrideBaseUrl = null, string? overrideApiKey = null, string? overrideProvider = null, string? overrideProfileId = null, string? overrideCredentialRef = null, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public GoalDefaultCredentials? GetGoalDefaultCredentials(string workUnitId) => null;
        public GoalDefaultCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage) => null;
        public string? GetAutoReviewProfileId(string workUnitId) => null;
        public string? GetGoalDefaultProfileId(string workUnitId) => null;
        public IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId) => null;
        public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<TResult> TrackInlineAgentAsync<TResult>(string agentId, string workUnitId, string? taskId, Func<Action<string?>, Task<TResult>> run, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
