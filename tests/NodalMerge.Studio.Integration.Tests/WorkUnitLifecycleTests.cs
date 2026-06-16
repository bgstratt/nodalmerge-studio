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
    private static (InMemoryWorkUnitService Svc, ExecutionEventStreamService Events) Build()
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
            events);
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

    private sealed class NoopBranchService : IBranchService
    {
        public Task<string> CreateBranchAsync(string name, string? fromBranchId = null, CancellationToken cancellationToken = default) =>
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
        public Task<MergeProposal> ReviewAsync(string proposalId, MergeProposalStatus decision, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal> AutomatedReviewAsync(string proposalId, MergeProposalStatus decision, string verificationResults, string? reviewerAgentId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MergeProposal> SupersedeAsync(string proposalId, string supersededByProposalId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopKnownGoodStateService : IKnownGoodStateService
    {
        public Task<KnownGoodState> MarkKnownGoodAsync(KnownGoodState state, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<KnownGoodState>> FindKnownGoodAsync(string branchId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<KnownGoodState?> CheckoutKnownGoodAsync(string stateId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class NoopAgentControlService : IAgentControlService
    {
        public Task<string> SpawnAsync(string agentType, string workUnitId, string? taskId = null, string? model = null,
            string? baseUrl = null, string? apiKey = null, string? provider = null, string? profileId = null,
            string? autoReviewProfileId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReinvokeOrchestratorAsync(string workUnitId, string? sessionId = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId) => null;
        public string? GetAutoReviewProfileId(string workUnitId) => null;
        public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task StopAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
