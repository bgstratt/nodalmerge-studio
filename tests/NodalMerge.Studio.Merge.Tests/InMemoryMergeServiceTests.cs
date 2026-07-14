using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Merge;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Merge.Tests;

public class InMemoryMergeServiceTests
{
    private static InMemoryMergeService Build()
    {
        var store = new InMemoryStudioNodeStore();
        return new(store, new NoopFileWorkspaceService(), new WorkspaceOptions(), new NoopEventStream(),
            new ArtifactLineageService(store));
    }

    private sealed class NoopEventStream : NodalMerge.Studio.Core.Services.IExecutionEventStream
    {
        public Task<NodalMerge.Studio.Contracts.Domain.ExecutionEvent> AppendAsync<T>(
            string sessionId, string? workUnitId,
            NodalMerge.Studio.Contracts.Domain.ExecutionEventKind kind, T payload,
            string? causedByEventId = null, string? eventId = null, CancellationToken ct = default) =>
            Task.FromResult(new NodalMerge.Studio.Contracts.Domain.ExecutionEvent(
                eventId ?? Guid.NewGuid().ToString("N"), sessionId, workUnitId, kind, "{}", causedByEventId, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>> GetSessionEventsAsync(
            string sessionId, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>>([]);

        public Task<NodalMerge.Studio.Contracts.Domain.ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default) =>
            Task.FromResult<NodalMerge.Studio.Contracts.Domain.ExecutionEvent?>(null);

        public Task<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>> GetEventsByKindAsync(
            IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEventKind> kinds, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>>([]);
    }

    private sealed class NoopFileWorkspaceService : NodalMerge.Studio.Core.Services.IFileWorkspaceService
    {
        public Task InitBranchAsync(string b, string? s = null, IReadOnlyList<string>? fileScope = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> MaterializeFileAsync(string b, string path, CancellationToken ct = default) => Task.FromResult(false);
        public Task<string?> ReadAsync(string b, string p, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<IReadOnlyList<NodalMerge.Studio.Core.Services.WorkspaceFileRead>> ReadManyAsync(string b, IReadOnlyList<string> paths, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NodalMerge.Studio.Core.Services.WorkspaceFileRead>>(
                paths.Select(p => new NodalMerge.Studio.Core.Services.WorkspaceFileRead(p, null, false)).ToList());
        public Task WriteAsync(string b, string p, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string b, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string b, string p, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> ListAsync(string b, string? s = null, string? p2 = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> ListIncludingDotfilesAsync(string b, string s, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<(IReadOnlyList<NodalMerge.Studio.Core.Services.WorkspaceSearchMatch> Matches, bool Truncated)> SearchAsync(string b, string query, string? s = null, string? fp = null, bool regex = false, bool cs = false, int cl = 3, int mr = 200, CancellationToken ct = default) => Task.FromResult<(IReadOnlyList<NodalMerge.Studio.Core.Services.WorkspaceSearchMatch>, bool)>(([], false));
        public Task<NodalMerge.Studio.Core.Services.WorkspaceReplaceResult> ReplaceAsync(string b, string p, string oldText, string newText, int expectedMatches = 1, CancellationToken ct = default) => Task.FromResult(new NodalMerge.Studio.Core.Services.WorkspaceReplaceResult(0, 0, 0, string.Empty));
        public Task<string> DiffAsync(string s, string t, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task ApplyBranchAsync(string s, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task CopyFilesAsync(string s, string t, IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetWorkingDirectoryAsync(string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task<NodalMerge.Studio.Core.Services.WorkspaceDiff> DiffExternalPathAsync(string b, string e, CancellationToken ct = default) =>
            Task.FromResult(new NodalMerge.Studio.Core.Services.WorkspaceDiff([], [], [], string.Empty));
        public Task ApplyExternalPathAsync(string b, string e, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static MergeProposal MakeProposal(string id, string source = "feat/x", string target = "main") =>
        new(id, source, target, "goal", "summary", "desc", null, null, null, MergeProposalStatus.Draft);

    // ── Phase 4 slice 11a — MergeProposalStatusChanged events + WorkUnit Merged transition ─

    private sealed class RecordingEventStream : NodalMerge.Studio.Core.Services.IExecutionEventStream
    {
        public List<NodalMerge.Studio.Contracts.Domain.ExecutionEvent> Events { get; } = [];

        public Task<NodalMerge.Studio.Contracts.Domain.ExecutionEvent> AppendAsync<T>(
            string sessionId, string? workUnitId,
            NodalMerge.Studio.Contracts.Domain.ExecutionEventKind kind, T payload,
            string? causedByEventId = null, string? eventId = null, CancellationToken ct = default)
        {
            var ev = new NodalMerge.Studio.Contracts.Domain.ExecutionEvent(
                eventId ?? Guid.NewGuid().ToString("N"), sessionId, workUnitId, kind,
                System.Text.Json.JsonSerializer.Serialize(payload), causedByEventId, DateTimeOffset.UtcNow);
            Events.Add(ev);
            return Task.FromResult(ev);
        }

        public Task<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>> GetSessionEventsAsync(
            string sessionId, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>>(
                [.. Events.Where(e => e.SessionId == sessionId)]);

        public Task<NodalMerge.Studio.Contracts.Domain.ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default) =>
            Task.FromResult(Events.FirstOrDefault(e => e.EventId == eventId));

        public Task<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>> GetEventsByKindAsync(
            IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEventKind> kinds, DateTimeOffset? since = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<NodalMerge.Studio.Contracts.Domain.ExecutionEvent>>(
                [.. Events.Where(e => kinds.Contains(e.Kind) && (since is null || e.OccurredAt > since.Value))]);
    }

    private sealed class RecordingWorkUnitService : NodalMerge.Studio.Core.Services.IWorkUnitService
    {
        public List<(string WorkUnitId, WorkUnitStatus Status, string? SessionId)> Calls { get; } = [];

        // Phase 2 item 2 follow-up — settable so ApplyAsync's owningWorkUnit lookup (used to
        // resolve a per-work-unit RepositoryId for multi-repo write-back) can be exercised.
        public WorkUnit? WorkUnitToReturn { get; set; }

        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken ct = default)
        {
            Calls.Add((workUnitId, status, sessionId));
            return Task.FromResult(new WorkUnit(workUnitId, "goal", "branch-1", status, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, "owner", null, null, null, null, [], []));
        }

        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken ct = default) =>
            Task.FromResult(new WorkUnit(workUnitId, "goal", "branch-1", WorkUnitStatus.Merged, DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, "owner", null, null, null, null, [], [], CurrentStage: stage));
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? blockedReason, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkUnit> IncrementReviewRejectionCountAsync(string workUnitId, bool automated, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkUnit> IncrementFailureAttemptCountAsync(string workUnitId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkUnit> AmendGoalForSteeredRetryAsync(string workUnitId, string amendedGoal, string steeringContext, string deadLetterEntryId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult(WorkUnitToReturn);
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<WorkUnit> AddDependencyAsync(string workUnitId, string dependsOnWorkUnitId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    // Phase 2 item 2 follow-up — records SyncBranchFromRepositoryAsync calls so tests can assert
    // whether the post-merge-write-back auto-resync fired, and can simulate a failure to prove it
    // never affects the merge itself.
    private sealed class RecordingRepositorySyncService : NodalMerge.Studio.Core.Services.IRepositorySyncService
    {
        public List<(string BranchId, string RepositoryPath, SyncTrigger Trigger)> Calls { get; } = [];
        public bool ThrowOnSync { get; set; }

        public Task<PendingExternalSync?> SyncBranchFromRepositoryAsync(
            string branchId, string repositoryPath, SyncTrigger trigger, CancellationToken ct = default)
        {
            Calls.Add((branchId, repositoryPath, trigger));
            if (ThrowOnSync) throw new InvalidOperationException("simulated resync failure");
            return Task.FromResult<PendingExternalSync?>(null);
        }

        public Task<RepositorySyncState?> GetStateAsync(string branchId, CancellationToken ct = default) =>
            Task.FromResult<RepositorySyncState?>(null);
    }

    // Phase 2 item 2 follow-up — resolves a single work unit's registered repository to a
    // caller-supplied path, so tests can prove the multi-repo write-back guard skips the
    // auto-resync when the resolved writeBackPath isn't the global default.
    private sealed class FakeRepositoryRegistryService(string repositoryId, string path)
        : NodalMerge.Studio.Storage.IRepositoryRegistryService
    {
        public Task<RepositoryV1> RegisterAsync(string p, string? label, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<RepositoryV1> CreateAsync(string p, string? label, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<RepositoryV1> CloneAsync(string url, string targetPath, string? label, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> ReadFileAsync(string repoId, string relativePath, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<string>> ListFilesAsync(string repoId, string? subPath = null, string? pattern = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<RepositoryV1>> ListAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<RepositoryV1?> GetAsync(string repoId, CancellationToken ct = default) =>
            Task.FromResult(repoId == repositoryId ? new RepositoryV1(repositoryId, path, null, DateTimeOffset.UtcNow) : null);
        public Task<IReadOnlyList<string>> FilterUnregisteredAsync(IReadOnlyList<string> paths, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private static (InMemoryMergeService Svc, RecordingEventStream Events, RecordingWorkUnitService WorkUnits, ArtifactLineageService Artifacts) BuildWithLifecycle()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new RecordingEventStream();
        var workUnits = new RecordingWorkUnitService();
        var artifacts = new ArtifactLineageService(store);
        var svc = new InMemoryMergeService(store, new NoopFileWorkspaceService(), new WorkspaceOptions(), events,
            artifacts, new SingleServiceProvider(workUnits));
        return (svc, events, workUnits, artifacts);
    }

    // ReviewAsync/ApplyAsync call IArtifactLineageService.UpdateStatusAsync when WorkUnitId is
    // set — in production, McpToolDispatcher.MergeProposeAsync records this artifact before
    // ReviewAsync ever runs; these tests call InMemoryMergeService directly, so it must be
    // seeded explicitly.
    private static Task SeedProposalArtifactAsync(ArtifactLineageService artifacts, string proposalId, string workUnitId) =>
        artifacts.RecordAsync(new ArtifactRef(
            proposalId, ArtifactType.MergeProposal, workUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, workUnitId, null));

    private static MergeProposal MakeProposalWithSession(string id, string workUnitId, string sessionId) =>
        MakeProposal(id) with { WorkUnitId = workUnitId, SessionId = sessionId };

    [Fact]
    public async Task ValidateAsync_emits_MergeProposalStatusChanged_when_session_present()
    {
        var (svc, events, _, _) = BuildWithLifecycle();
        await svc.ProposeAsync(MakeProposalWithSession("MP-1", "WU-1", "SES-1"));

        await svc.ValidateAsync("MP-1");

        var ev = Assert.Single(events.Events, e => e.Kind == ExecutionEventKind.MergeProposalStatusChanged);
        var payload = System.Text.Json.JsonSerializer.Deserialize<MergeProposalStatusChangedPayload>(ev.PayloadJson)!;
        Assert.Equal(MergeProposalStatus.Draft, payload.PreviousStatus);
        Assert.Equal(MergeProposalStatus.ReadyForReview, payload.NewStatus);
    }

    [Fact]
    public async Task ReviewAsync_emits_MergeProposalStatusChanged_alongside_ProposalApproved()
    {
        var (svc, events, _, artifacts) = BuildWithLifecycle();
        await svc.ProposeAsync(MakeProposalWithSession("MP-1", "WU-1", "SES-1"));
        await SeedProposalArtifactAsync(artifacts, "MP-1", "WU-1");
        await svc.ValidateAsync("MP-1");

        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);

        Assert.Contains(events.Events, e => e.Kind == ExecutionEventKind.ProposalApproved);
        var statusChanges = events.Events
            .Where(e => e.Kind == ExecutionEventKind.MergeProposalStatusChanged)
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<MergeProposalStatusChangedPayload>(e.PayloadJson)!)
            .ToList();
        Assert.Contains(statusChanges, p => p.NewStatus == MergeProposalStatus.Approved);
    }

    [Fact]
    public async Task ApplyAsync_transitions_owning_WorkUnit_to_Merged()
    {
        var (svc, _, workUnits, artifacts) = BuildWithLifecycle();
        await svc.ProposeAsync(MakeProposalWithSession("MP-1", "WU-1", "SES-1"));
        await SeedProposalArtifactAsync(artifacts, "MP-1", "WU-1");
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);

        await svc.ApplyAsync("MP-1");

        Assert.Contains(workUnits.Calls, c => c.WorkUnitId == "WU-1" && c.Status == WorkUnitStatus.Merged && c.SessionId == "SES-1");
    }

    [Fact]
    public async Task ApplyAsync_emits_MergeProposalStatusChanged_to_Merged()
    {
        var (svc, events, _, artifacts) = BuildWithLifecycle();
        await svc.ProposeAsync(MakeProposalWithSession("MP-1", "WU-1", "SES-1"));
        await SeedProposalArtifactAsync(artifacts, "MP-1", "WU-1");
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);

        await svc.ApplyAsync("MP-1");

        var statusChanges = events.Events
            .Where(e => e.Kind == ExecutionEventKind.MergeProposalStatusChanged)
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<MergeProposalStatusChangedPayload>(e.PayloadJson)!)
            .ToList();
        Assert.Contains(statusChanges, p => p.NewStatus == MergeProposalStatus.Merged);
    }

    [Fact]
    public async Task ApplyAsync_without_WorkUnitId_does_not_call_UpdateStatusAsync()
    {
        var (svc, _, workUnits, _) = BuildWithLifecycle();
        await svc.ProposeAsync(MakeProposal("MP-1")); // no WorkUnitId/SessionId
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);

        await svc.ApplyAsync("MP-1");

        Assert.Empty(workUnits.Calls);
    }

    // ── ProposeAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ProposeAsync_stores_the_caller_supplied_status()
    {
        // ProposeAsync no longer forces Draft — the policy-gate-blocked path in
        // MergeCommandService relies on proposing straight into Rejected, and normal
        // callers (MergeCommandService, MergeReconciliationService) already pass Draft
        // themselves.
        var svc = Build();
        var proposal = MakeProposal("MP-1") with { Status = MergeProposalStatus.Rejected };

        var result = await svc.ProposeAsync(proposal);

        Assert.Equal(MergeProposalStatus.Rejected, result.Status);
    }

    // ── GetAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_returns_stored_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));

        var result = await svc.GetAsync("MP-1");

        Assert.NotNull(result);
        Assert.Equal("MP-1", result.ProposalId);
    }

    [Fact]
    public async Task GetAsync_returns_null_for_unknown_id()
    {
        var result = await Build().GetAsync("no-such-proposal");
        Assert.Null(result);
    }

    // ── ValidateAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateAsync_transitions_Draft_to_ReadyForReview()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));

        var result = await svc.ValidateAsync("MP-1");

        Assert.Equal(MergeProposalStatus.ReadyForReview, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_is_idempotent_for_already_ReadyForReview_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1"); // → ReadyForReview

        // Re-validating an already-ReadyForReview proposal is a benign no-op (e.g. a defensive
        // re-validate from the auto-reviewer before it checks status) — it must not throw.
        var result = await svc.ValidateAsync("MP-1");

        Assert.Equal(MergeProposalStatus.ReadyForReview, result.Status);
    }

    [Fact]
    public async Task ValidateAsync_rejects_non_Draft_non_ReadyForReview_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1"); // → ReadyForReview
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved); // → Approved

        // Approved cannot transition back to ReadyForReview — genuinely non-transitionable.
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ValidateAsync("MP-1"));
    }

    [Fact]
    public async Task ValidateAsync_throws_for_unknown_proposal()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Build().ValidateAsync("no-such"));
    }

    // ── ReviewAsync — human gate ─────────────────────────────────────────────

    [Fact]
    public async Task ReviewAsync_approves_ReadyForReview_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");

        var result = await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);

        Assert.Equal(MergeProposalStatus.Approved, result.Status);
    }

    [Fact]
    public async Task ReviewAsync_rejects_ReadyForReview_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");

        var result = await svc.ReviewAsync("MP-1", MergeProposalStatus.Rejected);

        Assert.Equal(MergeProposalStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task ReviewAsync_cannot_bypass_validate_step()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1")); // still Draft

        // Attempting to approve a Draft proposal must fail (AP-4 gate)
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.ReviewAsync("MP-1", MergeProposalStatus.Approved));
    }

    [Fact]
    public async Task ReviewAsync_throws_for_unknown_proposal()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            Build().ReviewAsync("no-such", MergeProposalStatus.Approved));
    }

    [Fact]
    public async Task AutomatedReviewAsync_approved_returns_to_ReadyForReview_with_notes()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-auto"));
        await svc.ValidateAsync("MP-auto");

        var result = await svc.AutomatedReviewAsync(
            "MP-auto",
            MergeProposalStatus.Approved,
            "Scope matches plan.");

        Assert.Equal(MergeProposalStatus.ReadyForReview, result.Status);
        Assert.Equal("Scope matches plan.", result.VerificationResults);
    }

    [Fact]
    public async Task AutomatedReviewAsync_rejected_terminates_before_human_gate()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-auto-rej"));
        await svc.ValidateAsync("MP-auto-rej");

        var result = await svc.AutomatedReviewAsync(
            "MP-auto-rej",
            MergeProposalStatus.Rejected,
            "Missing required file.");

        Assert.Equal(MergeProposalStatus.Rejected, result.Status);
        Assert.Equal("Missing required file.", result.VerificationResults);
    }

    // ── Slice 23 — considered-artifact citation ──────────────────────────────

    [Fact]
    public async Task AutomatedReviewAsync_sets_ConsideredArtifactIds_on_the_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-considered"));
        await svc.ValidateAsync("MP-considered");

        var result = await svc.AutomatedReviewAsync(
            "MP-considered",
            MergeProposalStatus.Approved,
            "Scope matches plan.",
            consideredArtifactIds: ["KA-1", "KA-2"]);

        Assert.Equal(["KA-1", "KA-2"], result.ConsideredArtifactIds);
    }

    [Fact]
    public async Task AutomatedReviewAsync_omits_ConsideredArtifactIds_param_defaults_to_empty()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-no-considered"));
        await svc.ValidateAsync("MP-no-considered");

        var result = await svc.AutomatedReviewAsync(
            "MP-no-considered", MergeProposalStatus.Approved, "Scope matches plan.");

        Assert.Empty(result.ConsideredArtifactIds);
    }

    [Fact]
    public async Task AutomatedReviewAsync_emits_ArtifactConsideredInDecision_per_considered_id_when_session_present()
    {
        var (svc, events, _, _) = BuildWithLifecycle();
        await svc.ProposeAsync(MakeProposalWithSession("MP-1", "WU-1", "SES-1"));
        await svc.ValidateAsync("MP-1");

        await svc.AutomatedReviewAsync(
            "MP-1", MergeProposalStatus.Rejected, "Violates a recorded constraint.",
            consideredArtifactIds: ["KA-1", "KA-2"]);

        var considered = events.Events.Where(e => e.Kind == ExecutionEventKind.ArtifactConsideredInDecision).ToList();
        Assert.Equal(2, considered.Count);
        var artifactIds = considered
            .Select(e => System.Text.Json.JsonSerializer.Deserialize<ArtifactConsideredInDecisionPayload>(e.PayloadJson)!.ArtifactId)
            .ToList();
        Assert.Equal(["KA-1", "KA-2"], artifactIds);
    }

    [Fact]
    public async Task AutomatedReviewAsync_emits_no_ArtifactConsideredInDecision_when_list_is_empty()
    {
        var (svc, events, _, _) = BuildWithLifecycle();
        await svc.ProposeAsync(MakeProposalWithSession("MP-1", "WU-1", "SES-1"));
        await svc.ValidateAsync("MP-1");

        await svc.AutomatedReviewAsync("MP-1", MergeProposalStatus.Approved, "Looks fine.");

        Assert.DoesNotContain(events.Events, e => e.Kind == ExecutionEventKind.ArtifactConsideredInDecision);
    }

    // ── ApplyAsync — human gate ──────────────────────────────────────────────

    [Fact]
    public async Task ApplyAsync_merges_Approved_proposal()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);

        var result = await svc.ApplyAsync("MP-1");

        Assert.Equal(MergeProposalStatus.Merged, result.Status);
    }

    [Fact]
    public async Task ApplyAsync_cannot_bypass_human_approval()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1"); // ReadyForReview, not Approved

        // Attempting to apply without human approval must fail (AP-4 gate)
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync("MP-1"));
    }

    [Fact]
    public async Task ApplyAsync_cannot_apply_Draft_directly()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1")); // Draft only

        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync("MP-1"));
    }

    [Fact]
    public async Task ApplyAsync_throws_for_unknown_proposal()
    {
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Build().ApplyAsync("no-such"));
    }

    // ── ApplyAsync — post-merge write-back resync (Phase 2 item 2 follow-up) ──

    [Fact]
    public async Task ApplyAsync_triggers_PostMergeWriteBack_resync_when_writeBackPath_matches_seed_default()
    {
        var store = new InMemoryStudioNodeStore();
        var sync = new RecordingRepositorySyncService();
        var seedPath = Path.Combine(Path.GetTempPath(), "seed-repo");
        var svc = new InMemoryMergeService(store, new NoopFileWorkspaceService(),
            new WorkspaceOptions { SeedRepositoryPath = seedPath }, new NoopEventStream(),
            new ArtifactLineageService(store), repositorySync: sync);

        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);
        await svc.ApplyAsync("MP-1");

        var call = Assert.Single(sync.Calls);
        Assert.Equal("main", call.BranchId);
        Assert.Equal(seedPath, call.RepositoryPath);
        Assert.Equal(SyncTrigger.PostMergeWriteBack, call.Trigger);
    }

    [Fact]
    public async Task ApplyAsync_does_not_trigger_resync_when_writeBackPath_is_blank()
    {
        var store = new InMemoryStudioNodeStore();
        var sync = new RecordingRepositorySyncService();
        var svc = new InMemoryMergeService(store, new NoopFileWorkspaceService(),
            new WorkspaceOptions(), new NoopEventStream(), new ArtifactLineageService(store),
            repositorySync: sync);

        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);
        await svc.ApplyAsync("MP-1");

        Assert.Empty(sync.Calls);
    }

    [Fact]
    public async Task ApplyAsync_does_not_trigger_resync_for_a_non_default_repository_path()
    {
        var store = new InMemoryStudioNodeStore();
        var sync = new RecordingRepositorySyncService();
        var workUnits = new RecordingWorkUnitService();
        var otherRepoPath = Path.Combine(Path.GetTempPath(), "other-repo");
        workUnits.WorkUnitToReturn = new WorkUnit("wu-1", "goal", "branch-1", WorkUnitStatus.Executing,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner", null, null, null, null, [], [],
            RepositoryId: "repo-other");
        var repositories = new FakeRepositoryRegistryService("repo-other", otherRepoPath);
        var seedPath = Path.Combine(Path.GetTempPath(), "seed-repo");
        var artifacts = new ArtifactLineageService(store);
        var svc = new InMemoryMergeService(store, new NoopFileWorkspaceService(),
            new WorkspaceOptions { SeedRepositoryPath = seedPath }, new NoopEventStream(),
            artifacts, new SingleServiceProvider(workUnits),
            repositories: repositories, repositorySync: sync);

        await svc.ProposeAsync(MakeProposal("MP-1") with { WorkUnitId = "wu-1" });
        await SeedProposalArtifactAsync(artifacts, "MP-1", "wu-1");
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);
        await svc.ApplyAsync("MP-1");

        Assert.Empty(sync.Calls);
    }

    [Fact]
    public async Task ApplyAsync_succeeds_even_when_resync_throws()
    {
        var store = new InMemoryStudioNodeStore();
        var sync = new RecordingRepositorySyncService { ThrowOnSync = true };
        var seedPath = Path.Combine(Path.GetTempPath(), "seed-repo");
        var svc = new InMemoryMergeService(store, new NoopFileWorkspaceService(),
            new WorkspaceOptions { SeedRepositoryPath = seedPath }, new NoopEventStream(),
            new ArtifactLineageService(store), repositorySync: sync);

        await svc.ProposeAsync(MakeProposal("MP-1"));
        await svc.ValidateAsync("MP-1");
        await svc.ReviewAsync("MP-1", MergeProposalStatus.Approved);
        var result = await svc.ApplyAsync("MP-1");

        Assert.Equal(MergeProposalStatus.Merged, result.Status);
        Assert.Single(sync.Calls); // it did try
    }

    // ── ListAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ListAsync_filters_by_source_branch()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-A", source: "feat/a"));
        await svc.ProposeAsync(MakeProposal("MP-B", source: "feat/b"));

        var results = await svc.ListAsync("feat/a");

        Assert.Single(results);
        Assert.Equal("MP-A", results[0].ProposalId);
    }

    [Fact]
    public async Task ListAsync_returns_all_when_no_branch_filter()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-1", source: "feat/x"));
        await svc.ProposeAsync(MakeProposal("MP-2", source: "feat/y"));

        var results = await svc.ListAsync();

        Assert.Equal(2, results.Count);
    }

    // ── Full AP-4 happy path ─────────────────────────────────────────────────

    [Fact]
    public async Task Full_AP4_path_propose_validate_approve_apply()
    {
        var svc = Build();

        var proposed = await svc.ProposeAsync(MakeProposal("MP-full"));
        Assert.Equal(MergeProposalStatus.Draft, proposed.Status);

        var validated = await svc.ValidateAsync("MP-full");
        Assert.Equal(MergeProposalStatus.ReadyForReview, validated.Status);

        var approved = await svc.ReviewAsync("MP-full", MergeProposalStatus.Approved);
        Assert.Equal(MergeProposalStatus.Approved, approved.Status);

        var merged = await svc.ApplyAsync("MP-full");
        Assert.Equal(MergeProposalStatus.Merged, merged.Status);
    }

    [Fact]
    public async Task Full_AP4_rejection_path_propose_validate_reject()
    {
        var svc = Build();
        await svc.ProposeAsync(MakeProposal("MP-rej"));
        await svc.ValidateAsync("MP-rej");

        var rejected = await svc.ReviewAsync("MP-rej", MergeProposalStatus.Rejected);
        Assert.Equal(MergeProposalStatus.Rejected, rejected.Status);

        // Cannot apply a rejected proposal
        await Assert.ThrowsAsync<InvalidOperationException>(() => svc.ApplyAsync("MP-rej"));
    }
}
