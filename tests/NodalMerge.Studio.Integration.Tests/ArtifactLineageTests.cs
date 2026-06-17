using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the success criteria from Slice 10d — Artifact Lineage Store: a persistent,
/// traversable lineage graph plus conflict pre-detection at enqueue using FilesTouched.
/// </summary>
[Trait("Category", "Integration")]
public class ArtifactLineageTests
{
    private static ArtifactRef MakeArtifact(
        string id, ArtifactType type, string? parentArtifactId, string ownedByWorkUnitId, DateTimeOffset createdAt) =>
        new(id, type, parentArtifactId, ArtifactStatus.Active, createdAt, ownedByWorkUnitId, null);

    // ── RecordAsync / GetChainAsync ──────────────────────────────────────────

    [Fact]
    public async Task GetChainAsync_returns_full_chain_ordered_by_CreatedAt()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        var t0 = DateTimeOffset.UtcNow;

        await svc.RecordAsync(MakeArtifact("WU-1", ArtifactType.Goal, null, "WU-1", t0));
        await svc.RecordAsync(MakeArtifact("T-1", ArtifactType.Task, "WU-1", "WU-1", t0.AddSeconds(1)));
        await svc.RecordAsync(MakeArtifact("MP-1", ArtifactType.MergeProposal, "WU-1", "WU-1", t0.AddSeconds(2)));

        var chain = await svc.GetChainAsync("WU-1");

        Assert.Equal(["WU-1", "T-1", "MP-1"], chain.Select(a => a.ArtifactId));
        Assert.Equal([ArtifactType.Goal, ArtifactType.Task, ArtifactType.MergeProposal], chain.Select(a => a.Type));
    }

    [Fact]
    public async Task GetChainAsync_is_scoped_to_a_single_work_unit()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        await svc.RecordAsync(MakeArtifact("WU-1", ArtifactType.Goal, null, "WU-1", DateTimeOffset.UtcNow));
        await svc.RecordAsync(MakeArtifact("WU-2", ArtifactType.Goal, null, "WU-2", DateTimeOffset.UtcNow));

        var chain = await svc.GetChainAsync("WU-1");

        Assert.Single(chain);
        Assert.Equal("WU-1", chain[0].ArtifactId);
    }

    [Fact]
    public async Task RecordAsync_called_twice_with_same_ArtifactId_is_a_noop()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        var original = MakeArtifact("ART-1", ArtifactType.Task, "WU-1", "WU-1", DateTimeOffset.UtcNow);

        var first = await svc.RecordAsync(original);
        var second = await svc.RecordAsync(original with { Status = ArtifactStatus.Superseded });

        Assert.Equal(ArtifactStatus.Active, first.Status);
        Assert.Equal(ArtifactStatus.Active, second.Status); // second call returned the original, unchanged
        Assert.Single(await svc.GetChainAsync("WU-1"));
    }

    // ── GetAsync ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_returns_null_for_unknown_artifact()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        Assert.Null(await svc.GetAsync("no-such-artifact"));
    }

    // ── GetChildrenAsync — traversable root-to-leaf ──────────────────────────

    [Fact]
    public async Task GetChildrenAsync_returns_direct_children_of_a_parent_artifact()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        await svc.RecordAsync(MakeArtifact("WU-1", ArtifactType.Goal, null, "WU-1", DateTimeOffset.UtcNow));
        await svc.RecordAsync(MakeArtifact("T-1", ArtifactType.Task, "WU-1", "WU-1", DateTimeOffset.UtcNow));
        await svc.RecordAsync(MakeArtifact("T-2", ArtifactType.Task, "WU-1", "WU-1", DateTimeOffset.UtcNow));
        await svc.RecordAsync(MakeArtifact("MP-1", ArtifactType.MergeProposal, "T-1", "WU-1", DateTimeOffset.UtcNow));

        var childrenOfGoal = await svc.GetChildrenAsync("WU-1");
        var childrenOfTask1 = await svc.GetChildrenAsync("T-1");

        Assert.Equal(2, childrenOfGoal.Count);
        Assert.Contains(childrenOfGoal, a => a.ArtifactId == "T-1");
        Assert.Contains(childrenOfGoal, a => a.ArtifactId == "T-2");
        Assert.Single(childrenOfTask1, a => a.ArtifactId == "MP-1");
    }

    // ── UpdateStatusAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task UpdateStatusAsync_transitions_Active_to_Approved_and_is_visible_in_next_GetChainAsync()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        await svc.RecordAsync(MakeArtifact("MP-1", ArtifactType.MergeProposal, "WU-1", "WU-1", DateTimeOffset.UtcNow));

        var updated = await svc.UpdateStatusAsync("MP-1", ArtifactStatus.Approved);
        Assert.Equal(ArtifactStatus.Approved, updated.Status);

        var chain = await svc.GetChainAsync("WU-1");
        Assert.Equal(ArtifactStatus.Approved, chain.Single(a => a.ArtifactId == "MP-1").Status);
    }

    [Fact]
    public async Task UpdateStatusAsync_throws_for_unknown_artifact()
    {
        var svc = new ArtifactLineageService(new InMemoryStudioNodeStore());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.UpdateStatusAsync("no-such", ArtifactStatus.Approved));
    }

    // ── Conflict pre-detection at enqueue (FilesTouched overlap) ─────────────

    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        private readonly Dictionary<string, WorkUnit> _units = new();
        public void Seed(WorkUnit wu) => _units[wu.WorkUnitId] = wu;
        public Task<WorkUnit> CreateAsync(WorkUnit wu, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string id, WorkUnitStatus s, string? sessionId = null, CancellationToken ct = default)
        {
            var updated = _units[id] with { Status = s };
            _units[id] = updated;
            return Task.FromResult(updated);
        }
        public Task<WorkUnit> SetCurrentStageAsync(string id, PipelineStage? stage, CancellationToken ct = default)
        {
            var updated = _units[id] with { CurrentStage = stage };
            _units[id] = updated;
            return Task.FromResult(updated);
        }
        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult(_units.GetValueOrDefault(workUnitId));
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([.. _units.Values]);
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([.. _units.Values.Where(w => w.ParentWorkUnitId == parentId)]);
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);
    }

    private sealed class FakeMergeService : IMergeService
    {
        private readonly Dictionary<string, MergeProposal> _proposals = new();
        public void Seed(MergeProposal p) => _proposals[p.ProposalId] = p;
        public Task<MergeProposal> ProposeAsync(MergeProposal p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal?> GetAsync(string proposalId, CancellationToken ct = default) =>
            Task.FromResult(_proposals.GetValueOrDefault(proposalId));
        public Task<MergeProposal> ValidateAsync(string proposalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> ReviewAsync(string proposalId, MergeProposalStatus d, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> AutomatedReviewAsync(string proposalId, MergeProposalStatus decision, string verificationResults, string? reviewerAgentId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> ApplyAsync(string proposalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MergeProposal>>([.. _proposals.Values]);

        public Task<MergeProposal> SupersedeAsync(string proposalId, string supersededByProposalId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private static WorkUnit MakeWorkUnit(string id, string? parentId, IReadOnlyList<string> fileScope) =>
        new(id, "goal", "branch-" + id, WorkUnitStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "owner", null, null, null, parentId, [], fileScope);

    [Fact]
    public async Task EnqueueAsync_attaches_ConflictWarning_when_sibling_proposal_overlaps_FileScope()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var workUnits = new FakeWorkUnitService();
        var artifacts = new ArtifactLineageService(store);
        var merge = new FakeMergeService();

        workUnits.Seed(MakeWorkUnit("WU-P", null, []));
        workUnits.Seed(MakeWorkUnit("WU-A", "WU-P", ["src/api/**"]));
        workUnits.Seed(MakeWorkUnit("WU-B", "WU-P", ["src/api/**"]));

        // WU-A already produced a MergeProposal touching a file inside the shared scope.
        merge.Seed(new MergeProposal("MP-A", "branch-WU-A", "main", "goal", "summary", "desc",
            null, null, null, MergeProposalStatus.Draft, WorkUnitId: "WU-A",
            FilesTouched: ["src/api/handler.cs"]));
        await artifacts.RecordAsync(new ArtifactRef(
            "MP-A", ArtifactType.MergeProposal, "WU-A", ArtifactStatus.Active, DateTimeOffset.UtcNow, "WU-A", null));

        var noopWorkspaces = new NoopAgentWorkspaceService();
        var scheduler = new WorkSchedulerService(store, events, noopWorkspaces, new SingleServiceProvider(workUnits), artifacts, merge);

        await scheduler.EnqueueAsync("WU-B", "profile-a", sessionId: "SES-1");

        var pending = await scheduler.ListPendingAsync();
        var item = pending.Single(i => i.WorkUnitId == "WU-B");

        Assert.NotNull(item.Conflict);
        Assert.Contains("src/api/handler.cs", item.Conflict!.OverlappingFiles);
        Assert.Contains("WU-A", item.Conflict.ConflictingWorkUnitIds);

        var sessionEvents = await events.GetSessionEventsAsync("SES-1");
        Assert.Single(sessionEvents, e => e.Kind == ExecutionEventKind.ConflictDetected);
    }

    [Fact]
    public async Task EnqueueAsync_does_not_attach_ConflictWarning_when_FileScope_does_not_overlap()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var workUnits = new FakeWorkUnitService();
        var artifacts = new ArtifactLineageService(store);
        var merge = new FakeMergeService();

        workUnits.Seed(MakeWorkUnit("WU-P", null, []));
        workUnits.Seed(MakeWorkUnit("WU-A", "WU-P", ["src/api/**"]));
        workUnits.Seed(MakeWorkUnit("WU-B", "WU-P", ["src/ui/**"]));

        merge.Seed(new MergeProposal("MP-A", "branch-WU-A", "main", "goal", "summary", "desc",
            null, null, null, MergeProposalStatus.Draft, WorkUnitId: "WU-A",
            FilesTouched: ["src/api/handler.cs"]));
        await artifacts.RecordAsync(new ArtifactRef(
            "MP-A", ArtifactType.MergeProposal, "WU-A", ArtifactStatus.Active, DateTimeOffset.UtcNow, "WU-A", null));

        var scheduler = new WorkSchedulerService(store, events, new NoopAgentWorkspaceService(), new SingleServiceProvider(workUnits), artifacts, merge);

        await scheduler.EnqueueAsync("WU-B", "profile-a", sessionId: "SES-1");

        var item = (await scheduler.ListPendingAsync()).Single(i => i.WorkUnitId == "WU-B");
        Assert.Null(item.Conflict);
    }

    private sealed class NoopAgentWorkspaceService : IAgentWorkspaceService
    {
        public Task<AgentWorkspace> CreateAsync(string workUnitId, string baseBranch, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<AgentWorkspace?> GetAsync(string workspaceId, CancellationToken ct = default) =>
            Task.FromResult<AgentWorkspace?>(null);
        public Task ArchiveAsync(string workspaceId, string? sessionId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task DestroyAsync(string workspaceId, string? reason = null, string? sessionId = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ValidateWriteAsync(string workUnitId, string path, IReadOnlyList<string> fileScope, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
