using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Merge;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge.Tests;

public class AutomatedReviewGateServiceTests
{
    [Fact]
    public async Task HandleAutomatedRejectionAsync_requeues_children_before_max_rejections()
    {
        const string parentId = "WU-PARENT";
        const string childId = "WU-CHILD";
        const string proposalId = "MP-1";

        var workUnits = new FakeWorkUnitService(
            MakeUnit(parentId, WorkUnitStatus.Proposed),
            MakeUnit(childId, WorkUnitStatus.Proposed, parentId));

        var merge = new FakeMergeService(MakeRejectedProposal(proposalId));
        var scheduler = new FakeScheduler();
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService(), scheduler);

        var result = await gate.HandleAutomatedRejectionAsync(parentId, proposalId, "agent-reviewer");

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        Assert.Contains((childId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.Contains((parentId, WorkUnitStatus.Executing), workUnits.StatusCalls);
        Assert.Equal(1, workUnits.Units[parentId].ExecutionInfo!.AutomatedReviewRejectionCount);
        Assert.Contains((childId, "worker"), scheduler.Enqueued);
    }

    [Fact]
    public async Task HandleAutomatedRejectionAsync_escalates_to_dead_letter_after_max_rejections()
    {
        const string parentId = "WU-PARENT";
        const string childId = "WU-CHILD";
        const string proposalId = "MP-1";

        var workUnits = new FakeWorkUnitService(
            MakeUnit(
                parentId,
                WorkUnitStatus.Proposed,
                executionInfo: new WorkUnitExecutionInfo(FailureAttemptCount: 0, AutomatedReviewRejectionCount: 2)),
            MakeUnit(childId, WorkUnitStatus.Proposed, parentId));

        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, "Still broken."));
        var deadLetter = new FakeDeadLetterService();
        var gate = BuildGate(workUnits, merge, deadLetter);

        var result = await gate.HandleAutomatedRejectionAsync(parentId, proposalId, "agent-reviewer");

        Assert.Equal(AutomatedRejectionOutcome.EscalatedToDeadLetter, result.Outcome);
        Assert.Single(deadLetter.Calls);
        Assert.Equal(parentId, deadLetter.Calls[0].WorkUnitId);
        Assert.Equal(PipelineStage.Review, deadLetter.Calls[0].Stage);
        Assert.Equal("reviewer", deadLetter.Calls[0].ProfileId);
        Assert.Contains("Still broken", deadLetter.Calls[0].Reason, StringComparison.Ordinal);
        Assert.DoesNotContain((childId, WorkUnitStatus.Queued), workUnits.StatusCalls);
    }

    private static WorkUnit MakeUnit(
        string id,
        WorkUnitStatus status,
        string? parentId = null,
        WorkUnitExecutionInfo? executionInfo = null) =>
        new(
            id,
            "goal",
            "main",
            status,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            "owner",
            null,
            null,
            null,
            parentId,
            [],
            [],
            ExecutionInfo: executionInfo);

    private static MergeProposal MakeRejectedProposal(string proposalId, string reason = "Missing required file.") =>
        new(
            proposalId,
            "feat/x",
            "main",
            "goal",
            "summary",
            "desc",
            reason,
            null,
            null,
            MergeProposalStatus.Rejected);

    private static AutomatedReviewGateService BuildGate(
        FakeWorkUnitService workUnits,
        FakeMergeService merge,
        FakeDeadLetterService deadLetter,
        FakeScheduler? scheduler = null)
    {
        scheduler ??= new FakeScheduler();
        return new AutomatedReviewGateService(
            new FakeAgentControlService("reviewer"),
            merge,
            new FakeArtifactLineageService(),
            scheduler,
            workUnits,
            new FakeTaskService(),
            deadLetter);
    }

    private sealed class FakeAgentControlService(string autoReviewProfileId) : IAgentControlService
    {
        public string? GetAutoReviewProfileId(string workUnitId) => autoReviewProfileId;

        public OrchestratorCredentials? GetOrchestratorCredentials(string workUnitId) => null;

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
            CancellationToken cancellationToken = default) =>
            Task.FromResult("agent");

        public Task ReinvokeOrchestratorAsync(
            string workUnitId,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task PauseAsync(string agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ResumeAsync(string agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task StopAsync(string agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> GetStatusAsync(string agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AgentInfo>> ListActiveAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<AgentInfo>> ListAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMergeService(MergeProposal proposal) : IMergeService
    {
        public Task<MergeProposal> AutomatedReviewAsync(
            string proposalId,
            MergeProposalStatus decision,
            string verificationResults,
            string? reviewerAgentId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal?> GetAsync(string proposalId, CancellationToken cancellationToken = default) =>
            Task.FromResult<MergeProposal?>(proposal.ProposalId == proposalId ? proposal : null);

        public Task<IReadOnlyList<MergeProposal>> ListAsync(
            string? sourceBranch = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MergeProposal>>([proposal]);

        public Task<MergeProposal> ProposeAsync(MergeProposal proposal, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> ReviewAsync(
            string proposalId,
            MergeProposalStatus decision,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> SupersedeAsync(
            string proposalId,
            string supersededByProposalId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeArtifactLineageService : IArtifactLineageService
    {
        public Task<ArtifactRef> RecordAsync(ArtifactRef artifact, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ArtifactRef?> GetAsync(string artifactId, CancellationToken ct = default) =>
            Task.FromResult<ArtifactRef?>(null);

        public Task<IReadOnlyList<ArtifactRef>> GetChainAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>([]);

        public Task<IReadOnlyList<ArtifactRef>> GetChildrenAsync(string parentArtifactId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>([]);

        public Task<ArtifactRef> UpdateStatusAsync(string artifactId, ArtifactStatus status, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ArtifactRef> ReparentAsync(string artifactId, string newParentArtifactId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeScheduler : IWorkScheduler
    {
        public List<(string WorkUnitId, string ProfileId)> Enqueued { get; } = [];

        public Task EnqueueAsync(
            string workUnitId,
            string profileId,
            string? taskId = null,
            string? model = null,
            string? baseUrl = null,
            string? apiKey = null,
            string? provider = null,
            string? sessionId = null,
            CancellationToken ct = default)
        {
            Enqueued.Add((workUnitId, profileId));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ScheduledItem>> ListPendingAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledItem>>([]);

        public Task<ScheduledItem?> TryAcquireAsync(string agentId, CancellationToken ct = default) =>
            Task.FromResult<ScheduledItem?>(null);

        public Task ReleaseAsync(string workUnitId, bool success, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeTaskService : ITaskService
    {
        public Task<StudioTask> CreateAsync(StudioTask task, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StudioTask?> GetAsync(string taskId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<StudioTask> UpdateAsync(StudioTask task, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<StudioTask>>([]);

        public Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        public Dictionary<string, WorkUnit> Units { get; } = new();
        public List<(string WorkUnitId, WorkUnitStatus Status)> StatusCalls { get; } = [];

        public FakeWorkUnitService(params WorkUnit[] units)
        {
            foreach (var unit in units)
                Units[unit.WorkUnitId] = unit;
        }

        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default)
        {
            Units[workUnit.WorkUnitId] = workUnit;
            return Task.FromResult(workUnit);
        }

        public Task<WorkUnit> UpdateStatusAsync(
            string workUnitId,
            WorkUnitStatus status,
            string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            StatusCalls.Add((workUnitId, status));
            Units[workUnitId] = Units[workUnitId] with { Status = status };
            return Task.FromResult(Units[workUnitId]);
        }

        public Task<WorkUnit> SetCurrentStageAsync(
            string workUnitId,
            PipelineStage? stage,
            CancellationToken cancellationToken = default)
        {
            Units[workUnitId] = Units[workUnitId] with { CurrentStage = stage };
            return Task.FromResult(Units[workUnitId]);
        }

        public Task<WorkUnit> SetFanOutBlockedReasonAsync(
            string workUnitId,
            string? blockedReason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Units.TryGetValue(workUnitId, out var unit) ? unit : null);

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(Units.Values.ToList());

        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(
                Units.Values.Where(u => u.ParentWorkUnitId == parentId).ToList());

        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);
    }

    private sealed class FakeDeadLetterService : IDeadLetterService
    {
        public List<(string WorkUnitId, string AgentId, PipelineStage Stage, string ProfileId, string Reason)> Calls { get; } = [];

        public Task<DeadLetterEntry> RecordFailureAsync(
            string workUnitId,
            string agentId,
            PipelineStage stage,
            string profileId,
            string reason,
            string? taskId = null,
            string? lastProjectionSnapshot = null,
            string? sessionId = null,
            CancellationToken cancellationToken = default)
        {
            Calls.Add((workUnitId, agentId, stage, profileId, reason));
            return Task.FromResult(new DeadLetterEntry(
                "DL-1",
                workUnitId,
                agentId,
                stage,
                profileId,
                reason,
                null,
                3,
                DateTimeOffset.UtcNow,
                taskId,
                true));
        }

        public Task<DeadLetterEntry?> GetAsync(string entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult<DeadLetterEntry?>(null);

        public Task<DeadLetterEntry?> GetLatestForWorkUnitAsync(
            string workUnitId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<DeadLetterEntry?>(null);

        public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);

        public Task<DeadLetterRetryResult> RetryAsync(string entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeadLetterRetryResult(DeadLetterRetryOutcome.Retried));
    }
}
