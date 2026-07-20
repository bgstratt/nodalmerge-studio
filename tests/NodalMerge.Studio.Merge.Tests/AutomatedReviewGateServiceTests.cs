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

    [Fact]
    public async Task HandleHumanRejectionAsync_retries_the_work_unit_itself_when_no_children()
    {
        const string workUnitId = "WU-SOLO";
        const string proposalId = "MP-1";

        var workUnits = new FakeWorkUnitService(MakeUnit(workUnitId, WorkUnitStatus.Proposed));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, workUnitId: workUnitId));
        var scheduler = new FakeScheduler();
        var artifactCommands = new FakeArtifactCommandService();
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService(), scheduler, artifactCommands);

        var result = await gate.HandleHumanRejectionAsync(proposalId, "Missing the empty-input edge case.");

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        Assert.Contains((workUnitId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.Contains((workUnitId, "worker"), scheduler.Enqueued);
        Assert.Equal(1, workUnits.Units[workUnitId].ExecutionInfo!.HumanReviewRejectionCount);
        Assert.Single(artifactCommands.Recorded, r => r.WorkUnitId == workUnitId && r.Type == "Constraint");
    }

    [Fact]
    public async Task HandleHumanRejectionAsync_retries_children_for_a_reconciled_fan_out_proposal()
    {
        const string parentId = "WU-PARENT";
        const string childId = "WU-CHILD";
        const string proposalId = "MP-1";

        var workUnits = new FakeWorkUnitService(
            MakeUnit(parentId, WorkUnitStatus.Proposed),
            MakeUnit(childId, WorkUnitStatus.Proposed, parentId));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, workUnitId: parentId));
        var scheduler = new FakeScheduler();
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService(), scheduler);

        var result = await gate.HandleHumanRejectionAsync(proposalId, reviewNotes: null);

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        Assert.Contains((childId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.Contains((parentId, WorkUnitStatus.Executing), workUnits.StatusCalls);
        Assert.Contains((childId, "worker"), scheduler.Enqueued);
    }

    // The max-attempts cap stops AGENTS from auto-spinning; it must never block a human. A human
    // explicitly rejecting-with-notes past the cap has looked at the failures and decided to spend
    // more — the retry proceeds (with their notes as steering) instead of dead-ending in the
    // dead-letter queue. Previously this escalated, which live-stranded finished work behind an
    // "Unreject and Revise" click that silently did nothing recoverable.
    [Fact]
    public async Task HandleHumanRejectionAsync_still_retries_past_the_max_rejection_cap()
    {
        const string workUnitId = "WU-SOLO";
        const string proposalId = "MP-1";

        var workUnits = new FakeWorkUnitService(
            MakeUnit(
                workUnitId,
                WorkUnitStatus.Proposed,
                executionInfo: new WorkUnitExecutionInfo(FailureAttemptCount: 0, AutomatedReviewRejectionCount: 0, HumanReviewRejectionCount: 2)));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, workUnitId: workUnitId));
        var deadLetter = new FakeDeadLetterService();
        var scheduler = new FakeScheduler();
        var gate = BuildGate(workUnits, merge, deadLetter, scheduler);

        var result = await gate.HandleHumanRejectionAsync(proposalId, "Still wrong.");

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        Assert.Empty(deadLetter.Calls);
        Assert.Contains((workUnitId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.Contains((workUnitId, "worker"), scheduler.Enqueued);
    }

    [Fact]
    public async Task HandleHumanRejectionAsync_revise_mode_attaches_revision_context_and_leaves_workspace_untouched()
    {
        const string workUnitId = "WU-SOLO";
        const string proposalId = "MP-1";

        var workUnits = new FakeWorkUnitService(MakeUnit(workUnitId, WorkUnitStatus.Proposed, branchId: "wu-branch"));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, workUnitId: workUnitId));
        var fileWorkspace = new FakeFileWorkspaceService();
        var artifactLineage = new FakeArtifactLineageService();
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService(), artifactLineage: artifactLineage, fileWorkspace: fileWorkspace);

        var result = await gate.HandleHumanRejectionAsync(proposalId, "Missing the empty-input edge case.", RestartMode.Revise);

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        Assert.Empty(fileWorkspace.AppliedBranches);
        Assert.Contains((workUnitId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.Contains(artifactLineage.Recorded, a => a.Type == ArtifactType.RevisionContext && a.OwnedByWorkUnitId == workUnitId);
    }

    [Fact]
    public async Task HandleHumanRejectionAsync_revert_mode_resets_solo_workspace_from_base_snapshot()
    {
        const string workUnitId = "WU-SOLO";
        const string proposalId = "MP-1";

        var workUnits = new FakeWorkUnitService(MakeUnit(workUnitId, WorkUnitStatus.Proposed, branchId: "wu-branch"));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, workUnitId: workUnitId));
        var fileWorkspace = new FakeFileWorkspaceService();
        var artifactLineage = new FakeArtifactLineageService();
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService(), artifactLineage: artifactLineage, fileWorkspace: fileWorkspace);

        var result = await gate.HandleHumanRejectionAsync(proposalId, "Went off track.", RestartMode.Revert);

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        Assert.Contains(($"base/{proposalId}", "wu-branch"), fileWorkspace.AppliedBranches);
        Assert.Contains((workUnitId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.DoesNotContain(artifactLineage.Recorded, a => a.Type == ArtifactType.RevisionContext);
    }

    [Fact]
    public async Task HandleHumanRejectionAsync_revert_mode_resets_each_fan_out_childs_own_branch()
    {
        const string parentId = "WU-PARENT";
        const string childId = "WU-CHILD";
        const string parentProposalId = "MP-PARENT";
        const string childProposalId = "MP-CHILD";

        var workUnits = new FakeWorkUnitService(
            MakeUnit(parentId, WorkUnitStatus.Proposed, branchId: "parent-branch"),
            MakeUnit(childId, WorkUnitStatus.Proposed, parentId, branchId: "child-branch"));
        var merge = new FakeMergeService(MakeRejectedProposal(parentProposalId, workUnitId: parentId));
        var artifactLineage = new FakeArtifactLineageService();
        artifactLineage.SeedProposalArtifact(childId, childProposalId);
        var fileWorkspace = new FakeFileWorkspaceService();
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService(), artifactLineage: artifactLineage, fileWorkspace: fileWorkspace);

        var result = await gate.HandleHumanRejectionAsync(parentProposalId, reviewNotes: null, mode: RestartMode.Revert);

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        // The child's own proposal (not the parent's reconciled one) determines its base snapshot.
        Assert.Contains(($"base/{childProposalId}", "child-branch"), fileWorkspace.AppliedBranches);
    }

    [Fact]
    public async Task HandleHumanRejectionAsync_shares_one_rejection_counter_across_revise_and_revert()
    {
        const string workUnitId = "WU-SOLO";
        const string proposalId = "MP-1";

        var workUnits = new FakeWorkUnitService(MakeUnit(workUnitId, WorkUnitStatus.Proposed));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, workUnitId: workUnitId));
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService());

        await gate.HandleHumanRejectionAsync(proposalId, "First pass.", RestartMode.Revise);
        Assert.Equal(1, workUnits.Units[workUnitId].ExecutionInfo!.HumanReviewRejectionCount);

        await gate.HandleHumanRejectionAsync(proposalId, "Second pass, different approach.", RestartMode.Revert);
        Assert.Equal(2, workUnits.Units[workUnitId].ExecutionInfo!.HumanReviewRejectionCount);
    }

    [Fact]
    public async Task HandleAutomatedTaskRejectionAsync_retries_only_the_owning_child_not_its_siblings()
    {
        const string parentId = "WU-PARENT";
        const string childId = "WU-CHILD";
        const string siblingId = "WU-SIBLING";
        const string proposalId = "MP-CHILD";

        var workUnits = new FakeWorkUnitService(
            MakeUnit(parentId, WorkUnitStatus.Proposed),
            MakeUnit(childId, WorkUnitStatus.Proposed, parentId),
            MakeUnit(siblingId, WorkUnitStatus.Proposed, parentId));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, workUnitId: childId));
        var scheduler = new FakeScheduler();
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService(), scheduler);

        var result = await gate.HandleAutomatedTaskRejectionAsync(proposalId, "auto-reviewer-1");

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        Assert.Contains((childId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.DoesNotContain((siblingId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.DoesNotContain((parentId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.Contains((childId, "worker"), scheduler.Enqueued);
        Assert.Equal(1, workUnits.Units[childId].ExecutionInfo!.AutomatedReviewRejectionCount);
        Assert.Equal(0, workUnits.Units[childId].ExecutionInfo!.HumanReviewRejectionCount);
    }

    [Fact]
    public async Task HandleAutomatedTaskRejectionAsync_attaches_revision_context_from_verification_results()
    {
        const string childId = "WU-CHILD";
        const string proposalId = "MP-CHILD";

        var workUnits = new FakeWorkUnitService(MakeUnit(childId, WorkUnitStatus.Proposed, "WU-PARENT"));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, "Missing Foo.cs", childId));
        var artifactLineage = new FakeArtifactLineageService();
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService(), artifactLineage: artifactLineage);

        var result = await gate.HandleAutomatedTaskRejectionAsync(proposalId, "auto-reviewer-1");

        Assert.Equal(AutomatedRejectionOutcome.RetriedWorkers, result.Outcome);
        Assert.Contains(artifactLineage.Recorded, a => a.Type == ArtifactType.RevisionContext && a.OwnedByWorkUnitId == childId);
    }

    [Fact]
    public async Task HandleAutomatedTaskRejectionAsync_escalates_to_dead_letter_after_max_rejections_without_touching_human_counter()
    {
        const string childId = "WU-CHILD";
        const string proposalId = "MP-CHILD";

        var workUnits = new FakeWorkUnitService(
            MakeUnit(
                childId,
                WorkUnitStatus.Proposed,
                "WU-PARENT",
                executionInfo: new WorkUnitExecutionInfo(FailureAttemptCount: 0, AutomatedReviewRejectionCount: 2)));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, "Still broken.", childId));
        var deadLetter = new FakeDeadLetterService();
        var gate = BuildGate(workUnits, merge, deadLetter);

        var result = await gate.HandleAutomatedTaskRejectionAsync(proposalId, "auto-reviewer-1");

        Assert.Equal(AutomatedRejectionOutcome.EscalatedToDeadLetter, result.Outcome);
        Assert.Single(deadLetter.Calls);
        Assert.Equal(childId, deadLetter.Calls[0].WorkUnitId);
        Assert.Contains("Still broken", deadLetter.Calls[0].Reason, StringComparison.Ordinal);
        Assert.DoesNotContain((childId, WorkUnitStatus.Queued), workUnits.StatusCalls);
        Assert.Equal(0, workUnits.Units[childId].ExecutionInfo!.HumanReviewRejectionCount);
    }

    [Fact]
    public async Task HandleAutomatedTaskRejectionAsync_and_HandleHumanRejectionAsync_track_independent_counters()
    {
        const string childId = "WU-CHILD";
        const string proposalId = "MP-CHILD";

        var workUnits = new FakeWorkUnitService(MakeUnit(childId, WorkUnitStatus.Proposed, "WU-PARENT"));
        var merge = new FakeMergeService(MakeRejectedProposal(proposalId, workUnitId: childId));
        var gate = BuildGate(workUnits, merge, new FakeDeadLetterService());

        await gate.HandleAutomatedTaskRejectionAsync(proposalId, "auto-reviewer-1");
        await gate.HandleAutomatedTaskRejectionAsync(proposalId, "auto-reviewer-1");

        Assert.Equal(2, workUnits.Units[childId].ExecutionInfo!.AutomatedReviewRejectionCount);
        Assert.Equal(0, workUnits.Units[childId].ExecutionInfo!.HumanReviewRejectionCount);
    }

    private static WorkUnit MakeUnit(
        string id,
        WorkUnitStatus status,
        string? parentId = null,
        WorkUnitExecutionInfo? executionInfo = null,
        string? branchId = null) =>
        new(
            id,
            "goal",
            branchId ?? "main",
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

    private static MergeProposal MakeRejectedProposal(
        string proposalId, string reason = "Missing required file.", string workUnitId = "WU-PARENT") =>
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
            MergeProposalStatus.Rejected,
            WorkUnitId: workUnitId);

    private static AutomatedReviewGateService BuildGate(
        FakeWorkUnitService workUnits,
        FakeMergeService merge,
        FakeDeadLetterService deadLetter,
        FakeScheduler? scheduler = null,
        FakeArtifactCommandService? artifactCommands = null,
        FakeArtifactLineageService? artifactLineage = null,
        FakeFileWorkspaceService? fileWorkspace = null)
    {
        scheduler ??= new FakeScheduler();
        return new AutomatedReviewGateService(
            new FakeAgentControlService("reviewer"),
            merge,
            artifactLineage ?? new FakeArtifactLineageService(),
            scheduler,
            workUnits,
            new FakeTaskService(),
            deadLetter,
            artifactCommands ?? new FakeArtifactCommandService(),
            fileWorkspace ?? new FakeFileWorkspaceService(),
            // L2.4 — no blob store → resolver returns the inline WorkspaceChanges (unchanged behavior).
            new NodalMerge.Studio.Storage.MergeDiffResolverService());
    }

    private sealed class FakeAgentControlService(string autoReviewProfileId) : IAgentControlService
    {
        public string? GetAutoReviewProfileId(string workUnitId) => autoReviewProfileId;
        public string? GetGoalDefaultProfileId(string workUnitId) => null;

        public GoalDefaultCredentials? GetGoalDefaultCredentials(string workUnitId) => null;

        public GoalDefaultCredentials? GetCredentialsForStage(string workUnitId, PipelineStage stage) => null;

        public GoalDefaultCredentials? GetCredentialsForProfile(string workUnitId, string profileId) => null;

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
            IReadOnlyDictionary<PipelineStage, GoalDefaultCredentials>? stageCredentials = null,
            IReadOnlyList<string>? enabledDomainAgents = null,
            string? credentialRef = null,
            bool lenientToolParsing = false,
            IReadOnlyDictionary<string, GoalDefaultCredentials>? profileCredentials = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult("agent");

        public IReadOnlyList<string>? GetEnabledDomainAgents(string workUnitId) => null;

        public Task ReinvokeOrchestratorAsync(
            string workUnitId,
            string? sessionId = null,
            string? overrideModel = null,
            string? overrideBaseUrl = null,
            string? overrideApiKey = null,
            string? overrideProvider = null,
            string? overrideProfileId = null,
            string? overrideCredentialRef = null,
            bool ensurePlanner = false,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> ResupplyCredentialsAsync(
            string workUnitId,
            string? overrideModel = null,
            string? overrideBaseUrl = null,
            string? overrideApiKey = null,
            string? overrideProvider = null,
            string? overrideProfileId = null,
            string? overrideCredentialRef = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

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

        public Task<TResult> TrackInlineAgentAsync<TResult>(string agentId, string workUnitId, string? taskId, Func<Action<string?>, Task<TResult>> run, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeMergeService(MergeProposal proposal) : IMergeService
    {
        public Task<MergeProposal> AutomatedReviewAsync(
            string proposalId,
            MergeProposalStatus decision,
            string verificationResults,
            string? reviewerAgentId = null,
            IReadOnlyList<string>? consideredArtifactIds = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken cancellationToken = default, bool autoApplied = false) =>
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
            string? notes = null,
            string? reviewedBy = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> SupersedeAsync(
            string proposalId,
            string supersededByProposalId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PromoteResult> PromoteAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeArtifactLineageService : IArtifactLineageService
    {
        public Dictionary<string, List<ArtifactRef>> ChainByWorkUnitId { get; } = new();
        public List<ArtifactRef> Recorded { get; } = [];
        public List<(string ArtifactId, ArtifactStatus Status)> StatusUpdates { get; } = [];

        public Task<ArtifactRef> RecordAsync(ArtifactRef artifact, CancellationToken ct = default)
        {
            Recorded.Add(artifact);
            var ownerId = artifact.OwnedByWorkUnitId ?? artifact.ParentArtifactId;
            if (ownerId is not null)
            {
                if (!ChainByWorkUnitId.TryGetValue(ownerId, out var list))
                    ChainByWorkUnitId[ownerId] = list = [];
                list.Add(artifact);
            }
            return Task.FromResult(artifact);
        }

        public Task<ArtifactRef?> GetAsync(string artifactId, CancellationToken ct = default) =>
            Task.FromResult<ArtifactRef?>(null);

        public Task<IReadOnlyList<ArtifactRef>> GetChainAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>(
                ChainByWorkUnitId.TryGetValue(workUnitId, out var list) ? list : []);

        public Task<IReadOnlyList<ArtifactRef>> GetGlobalConstraintsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>([]);

        public Task<IReadOnlyList<ArtifactRef>> GetByTypeAsync(ArtifactType type, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>(
                [.. Recorded.Where(a => a.Type == type).OrderBy(a => a.CreatedAt)]);

        public Task<IReadOnlyList<ArtifactRef>> GetChildrenAsync(string parentArtifactId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>([]);

        public Task<ArtifactRef> UpdateStatusAsync(string artifactId, ArtifactStatus status, CancellationToken ct = default)
        {
            StatusUpdates.Add((artifactId, status));
            var existing = Recorded.First(a => a.ArtifactId == artifactId);
            return Task.FromResult(existing with { Status = status });
        }

        public Task<ArtifactRef> ReparentAsync(string artifactId, string newParentArtifactId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ArtifactRef> InvalidateAsync(string artifactId, string reason, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        // Seeds a fake MergeProposal ArtifactRef for a work unit's chain, so revert-mode tests can
        // resolve base/{proposalId} the same way the real code does for fan-out children.
        public void SeedProposalArtifact(string workUnitId, string proposalId)
        {
            var artifact = new ArtifactRef(
                proposalId, ArtifactType.MergeProposal, workUnitId, ArtifactStatus.Active,
                DateTimeOffset.UtcNow, workUnitId, null);
            if (!ChainByWorkUnitId.TryGetValue(workUnitId, out var list))
                ChainByWorkUnitId[workUnitId] = list = [];
            list.Add(artifact);
        }
    }

    private sealed class FakeFileWorkspaceService : IFileWorkspaceService
    {
        public List<(string SourceBranchId, string TargetBranchId)> AppliedBranches { get; } = [];

        public Task InitBranchAsync(string branchId, string? seedFromBranchId = null,
            IReadOnlyList<string>? fileScope = null, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> ReadAsync(string branchId, string relativePath, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task WriteAsync(string branchId, string relativePath, string content, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task DeleteAsync(string branchId, string relativePath, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<bool> ExistsAsync(string branchId, string relativePath, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<string>> ListAsync(string branchId, string? subPath = null, string? pattern = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<IReadOnlyList<string>> ListIncludingDotfilesAsync(string branchId, string subPath, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>([]);

        public Task<(IReadOnlyList<WorkspaceSearchMatch> Matches, bool Truncated)> SearchAsync(
            string branchId, string query, string? subPath = null, string? filePattern = null,
            bool regex = false, bool caseSensitive = false, int contextLines = 3, int maxResults = 200,
            CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<WorkspaceSearchMatch>, bool)>(([], false));

        public Task<WorkspaceReplaceResult> ReplaceAsync(
            string branchId, string relativePath, string oldText, string newText, int expectedMatches = 1,
            CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorkspaceFileRead>> ReadManyAsync(
            string branchId, IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkspaceFileRead>>([]);

        public Task<bool> MaterializeFileAsync(string branchId, string path, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<string> DiffAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task ApplyBranchAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default)
        {
            AppliedBranches.Add((sourceBranchId, targetBranchId));
            return Task.CompletedTask;
        }

        public Task CopyFilesAsync(
            string sourceBranchId, string targetBranchId, IReadOnlyList<string> relativePaths, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetWorkingDirectoryAsync(string branchId, CancellationToken ct = default) =>
            Task.FromResult<string?>(null);

        public Task<WorkspaceDiff> DiffExternalPathAsync(string branchId, string externalPath, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task ApplyExternalPathAsync(string branchId, string externalPath, CancellationToken ct = default) =>
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
            string? credentialRef = null,
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

        public Task MarkAwaitingFileLeaseAsync(string workUnitId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task MarkAwaitingResumeAsync(string workUnitId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task ClearAwaitingFileLeaseAsync(string workUnitId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task MarkAwaitingCredentialsAsync(string workUnitId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task SupplyCredentialsAsync(string workUnitId, string? provider, string? model, string? baseUrl, string? apiKey, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<ScheduledItem>> ListAwaitingResumeAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ScheduledItem>>([]);

        public Task ApproveResumeAsync(string workUnitId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<int> ApproveResumeAllAsync(CancellationToken ct = default) =>
            Task.FromResult(0);

        public Task ForceResumeAsync(string workUnitId, CancellationToken ct = default) =>
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

        public Task<WorkUnit> IncrementReviewRejectionCountAsync(
            string workUnitId,
            bool automated,
            CancellationToken cancellationToken = default)
        {
            var unit = Units[workUnitId];
            var executionInfo = unit.ExecutionInfo ?? new WorkUnitExecutionInfo(0, 0);
            executionInfo = automated
                ? executionInfo with { AutomatedReviewRejectionCount = executionInfo.AutomatedReviewRejectionCount + 1 }
                : executionInfo with { HumanReviewRejectionCount = executionInfo.HumanReviewRejectionCount + 1 };
            Units[workUnitId] = unit with { ExecutionInfo = executionInfo };
            return Task.FromResult(Units[workUnitId]);
        }

        public Task<WorkUnit> IncrementFailureAttemptCountAsync(
            string workUnitId,
            CancellationToken cancellationToken = default)
        {
            var unit = Units[workUnitId];
            var executionInfo = (unit.ExecutionInfo ?? new WorkUnitExecutionInfo(0, 0)) with
            {
                FailureAttemptCount = (unit.ExecutionInfo?.FailureAttemptCount ?? 0) + 1,
            };
            Units[workUnitId] = unit with { ExecutionInfo = executionInfo };
            return Task.FromResult(Units[workUnitId]);
        }

        public Task<WorkUnit> AmendGoalForSteeredRetryAsync(
            string workUnitId,
            string amendedGoal,
            string steeringContext,
            string deadLetterEntryId,
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

        public Task<WorkUnit> SetFileScopeAsync(
            string workUnitId,
            IReadOnlyList<string> fileScope,
            string? sessionId = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> AddDependencyAsync(
            string workUnitId,
            string dependsOnWorkUnitId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeArtifactCommandService : IArtifactCommandService
    {
        public List<(string WorkUnitId, string Type, string Title, string Body)> Recorded { get; } = [];

        public Task<ArtifactRef> RecordAsync(
            string workUnitId,
            string type,
            string title,
            string body,
            string? parentArtifactId = null,
            IReadOnlyList<string>? supersedes = null,
            CancellationToken ct = default)
        {
            Recorded.Add((workUnitId, type, title, body));
            return Task.FromResult(new ArtifactRef(
                $"KA-{Guid.NewGuid():N}",
                Enum.Parse<ArtifactType>(type),
                parentArtifactId ?? workUnitId,
                ArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                workUnitId,
                null,
                title,
                body));
        }

        public Task<IReadOnlyList<ArtifactRef>> QueryAsync(
            string workUnitId, string? type = null, string? keywords = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>([]);

        public Task<ArtifactRef> RecordPlanAsync(string workUnitId, string planContent, CancellationToken ct = default) =>
            Task.FromResult(new ArtifactRef(
                $"PLAN-{Guid.NewGuid():N}",
                ArtifactType.Plan,
                workUnitId,
                ArtifactStatus.Active,
                DateTimeOffset.UtcNow,
                workUnitId,
                null,
                "Plan",
                planContent));

        public Task<IReadOnlyList<ArtifactRef>> ListAsync(
            string workUnitId, bool includeAncestors = true, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>([]);

        public Task<ArtifactRef> InvalidateAsync(string artifactId, string reason, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
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
            string? model = null,
            string? baseUrl = null,
            string? apiKey = null,
            string? provider = null,
            FailureKind kind = FailureKind.Exception,
            string? credentialRef = null,
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

        public Task<IReadOnlyList<DeadLetterEntry>> GetHistoryForWorkUnitAsync(
            string workUnitId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);

        public Task<IReadOnlyList<DeadLetterEntry>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<DeadLetterEntry>>([]);

        public Task<DeadLetterRetryResult> RetryAsync(string entryId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeadLetterRetryResult(DeadLetterRetryOutcome.Retried));

        public Task<DeadLetterRetryResult> RetryWithCredentialOverrideAsync(
            string entryId,
            string? overrideModel,
            string? overrideBaseUrl,
            string? overrideApiKey,
            string? overrideProvider,
            string? overrideProfileId,
            string? overrideCredentialRef = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeadLetterRetryResult(DeadLetterRetryOutcome.Retried));

        public Task<DeadLetterRetryResult> RetryWithContextAsync(
            string entryId,
            string steeringContext,
            string? overrideModel = null,
            string? overrideBaseUrl = null,
            string? overrideApiKey = null,
            string? overrideProvider = null,
            string? overrideProfileId = null,
            string? overrideCredentialRef = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeadLetterRetryResult(DeadLetterRetryOutcome.Retried));
    }
}
