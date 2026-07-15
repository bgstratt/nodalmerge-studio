using System.Text.Json;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Contracts.Projections;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;
using TaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;

namespace NodalMerge.Studio.Projections.Tests;

public class ProjectionManagerTests
{
    // ── Fakes ──────────────────────────────────────────────────────────────

    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        private readonly Dictionary<string, WorkUnit> _store = new();

        public void Seed(WorkUnit w) => _store[w.WorkUnitId] = w;

        public Task<WorkUnit?> GetAsync(string id, CancellationToken ct = default) =>
            Task.FromResult(_store.GetValueOrDefault(id));

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(
                _store.Values.Where(w => branchId is null || w.BranchId == branchId).ToList());

        public Task<WorkUnit> CreateAsync(WorkUnit w, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string id, WorkUnitStatus s, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetCurrentStageAsync(string id, PipelineStage? stage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string id, string? blockedReason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementReviewRejectionCountAsync(string id, bool automated, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementFailureAttemptCountAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> AmendGoalForSteeredRetryAsync(string id, string amendedGoal, string steeringContext, string deadLetterEntryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> AddDependencyAsync(string workUnitId, string dependsOnWorkUnitId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeTaskService : ITaskService
    {
        private readonly List<StudioTask> _store = [];

        public void Seed(StudioTask t) => _store.Add(t);

        public Task<StudioTask?> GetAsync(string taskId, CancellationToken ct = default) =>
            Task.FromResult(_store.FirstOrDefault(t => t.TaskId == taskId));

        public Task<IReadOnlyList<StudioTask>> ListAsync(string? workUnitId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<StudioTask>>(
                _store.Where(t => workUnitId is null || t.WorkUnitId == workUnitId).ToList());

        public Task<StudioTask> CreateAsync(StudioTask t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StudioTask> UpdateAsync(StudioTask t, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<StudioTask> AssignAsync(string taskId, string agentId, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeMergeService : IMergeService
    {
        private readonly List<MergeProposal> _store = [];

        public void Seed(MergeProposal p) => _store.Add(p);

        public Task<MergeProposal?> GetAsync(string proposalId, CancellationToken ct = default) =>
            Task.FromResult(_store.FirstOrDefault(p => p.ProposalId == proposalId));

        public Task<IReadOnlyList<MergeProposal>> ListAsync(string? sourceBranch = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MergeProposal>>(
                _store.Where(p => sourceBranch is null || p.SourceBranch == sourceBranch).ToList());

        public Task<MergeProposal> ProposeAsync(MergeProposal p, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> ValidateAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> ReviewAsync(string id, MergeProposalStatus d, string? notes = null, string? reviewedBy = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> AutomatedReviewAsync(string proposalId, MergeProposalStatus decision, string verificationResults, string? reviewerAgentId = null, IReadOnlyList<string>? consideredArtifactIds = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<MergeProposal> ApplyAsync(string id, CancellationToken ct = default, bool autoApplied = false) => throw new NotSupportedException();
        public Task<MergeProposal> SupersedeAsync(string proposalId, string supersededByProposalId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<PromoteResult> PromoteAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeAgentRuntimeService : IAgentRuntimeService
    {
        private readonly Dictionary<(string, string), ExecutionSnapshot> _store = new();

        public void Seed(ExecutionSnapshot s) => _store[(s.AgentId, s.WorkUnitId)] = s;

        public Task<ExecutionSnapshot> GetSnapshotAsync(string agentId, string workUnitId, CancellationToken ct = default)
        {
            var snapshot = _store.TryGetValue((agentId, workUnitId), out var s)
                ? s
                : new ExecutionSnapshot(agentId, workUnitId, null, null, null, [], [], 0, 0, null);
            return Task.FromResult(snapshot);
        }

        public Task RecordActionAsync(string agentId, string workUnitId, string action, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeArtifactLineageService : IArtifactLineageService
    {
        private readonly Dictionary<string, ArtifactRef> _byId = new();
        private readonly Dictionary<string, List<ArtifactRef>> _byWorkUnit = new();

        public void Seed(ArtifactRef artifact)
        {
            _byId[artifact.ArtifactId] = artifact;
            if (artifact.OwnedByWorkUnitId is { } workUnitId)
            {
                if (!_byWorkUnit.TryGetValue(workUnitId, out var list))
                    _byWorkUnit[workUnitId] = list = [];
                list.Add(artifact);
            }
        }

        public Task<ArtifactRef> RecordAsync(ArtifactRef artifact, CancellationToken ct = default)
        {
            Seed(artifact);
            return Task.FromResult(artifact);
        }

        public Task<ArtifactRef?> GetAsync(string artifactId, CancellationToken ct = default) =>
            Task.FromResult(_byId.GetValueOrDefault(artifactId));

        public Task<IReadOnlyList<ArtifactRef>> GetChainAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>(
                _byWorkUnit.TryGetValue(workUnitId, out var list) ? [.. list.OrderBy(a => a.CreatedAt)] : []);

        public Task<IReadOnlyList<ArtifactRef>> GetGlobalConstraintsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>(
                [.. _byId.Values.Where(a => a.OwnedByWorkUnitId is null && a.Type == ArtifactType.Constraint).OrderBy(a => a.CreatedAt)]);

        public Task<IReadOnlyList<ArtifactRef>> GetByTypeAsync(ArtifactType type, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>(
                [.. _byId.Values.Where(a => a.Type == type).OrderBy(a => a.CreatedAt)]);

        public Task<IReadOnlyList<ArtifactRef>> GetChildrenAsync(string parentArtifactId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ArtifactRef>>([]);

        public Task<ArtifactRef> UpdateStatusAsync(string artifactId, ArtifactStatus status, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ArtifactRef> ReparentAsync(string artifactId, string newParentArtifactId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ArtifactRef> InvalidateAsync(string artifactId, string reason, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    // In-memory stand-in for IStudioNodeStore — only ReadAllNodesAsync/WriteNodeAsync are
    // exercised by WorkspacePathways' external-update lookup.
    private sealed class FakeNodeStore : IStudioNodeStore
    {
        private readonly Dictionary<(string Kind, string EntityId), string> _nodes = new();

        public Task WriteNodeAsync(string kind, string entityId, string payloadJson, CancellationToken ct = default)
        {
            _nodes[(kind, entityId)] = payloadJson;
            return Task.CompletedTask;
        }

        public Task<string?> ReadNodeAsync(string kind, string entityId, CancellationToken ct = default) =>
            Task.FromResult(_nodes.GetValueOrDefault((kind, entityId)));

        public Task<IReadOnlyList<(string EntityId, string PayloadJson)>> ReadAllNodesAsync(string kind, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string EntityId, string PayloadJson)>>(
                [.. _nodes.Where(kv => kv.Key.Kind == kind).Select(kv => (kv.Key.EntityId, kv.Value))]);
    }

    // Minimal fake for WorkspacePathways' event-sourced derivation — only GetEventsByKindAsync
    // is exercised; seeded events carry pre-serialized payload JSON exactly as
    // ExecutionEventStreamService writes it (default serializer options: PascalCase, numeric enums).
    private sealed class FakeExecutionEventStream : IExecutionEventStream
    {
        private readonly List<ExecutionEvent> _events = [];

        public void Seed(ExecutionEventKind kind, object payload, DateTimeOffset occurredAt, string? workUnitId = null) =>
            _events.Add(new ExecutionEvent(
                $"EVT-{_events.Count}", "session-1", workUnitId, kind,
                JsonSerializer.Serialize(payload), null, occurredAt));

        public Task<ExecutionEvent> AppendAsync<T>(string sessionId, string? workUnitId, ExecutionEventKind kind, T payload, string? causedByEventId = null, string? eventId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExecutionEvent>> GetSessionEventsAsync(string sessionId, DateTimeOffset? since = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<ExecutionEvent?> GetAsync(string eventId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ExecutionEvent>> GetEventsByKindAsync(IReadOnlyList<ExecutionEventKind> kinds, DateTimeOffset? since = null, CancellationToken ct = default)
        {
            var kindSet = kinds.ToHashSet();
            return Task.FromResult<IReadOnlyList<ExecutionEvent>>(
                [.. _events.Where(e => kindSet.Contains(e.Kind)).OrderBy(e => e.OccurredAt)]);
        }
    }

    private static ProjectionManager BuildManager(
        FakeWorkUnitService? workUnits = null,
        FakeTaskService? tasks = null,
        FakeMergeService? merges = null,
        FakeAgentRuntimeService? agentRuntime = null,
        FakeArtifactLineageService? artifacts = null,
        FakeNodeStore? nodeStore = null,
        FakeExecutionEventStream? eventStream = null) =>
        new(
            workUnits ?? new FakeWorkUnitService(),
            tasks ?? new FakeTaskService(),
            merges ?? new FakeMergeService(),
            agentRuntime ?? new FakeAgentRuntimeService(),
            artifacts ?? new FakeArtifactLineageService(),
            nodeStore: nodeStore,
            eventStream: eventStream);

    private static WorkUnit MakeWorkUnit(
        string id, string goal, string branch = "main", WorkUnitStatus status = WorkUnitStatus.Active, string? parentWorkUnitId = null) =>
        new(id, goal, branch, status, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "test", null, null, null, parentWorkUnitId, [], []);

    private static ArtifactRef MakeArtifact(
        string id, ArtifactType type, string ownedByWorkUnitId, string? title = null, string? body = null) =>
        new(id, type, ownedByWorkUnitId, ArtifactStatus.Active, DateTimeOffset.UtcNow, ownedByWorkUnitId, null, title, body);

    private static StudioTask MakeTask(string id, string workUnitId, TaskStatus status = TaskStatus.Open) =>
        new(id, workUnitId, $"Task {id}", "desc", status, null, 1);

    // ── WorkUnit projection ─────────────────────────────────────────────────

    [Fact]
    public async Task WorkUnit_Normal_returns_full_payload()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Implement auth"));
        var tasks = new FakeTaskService();
        tasks.Seed(MakeTask("T-1", "WU-1", TaskStatus.Open));
        tasks.Seed(MakeTask("T-2", "WU-1", TaskStatus.Completed));
        var manager = BuildManager(workUnits, tasks);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Normal, WorkUnitId: "WU-1"));

        Assert.Equal(ProjectionType.WorkUnit, result.Type);
        Assert.Equal(ProjectionLevel.Normal, result.Level);
        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal("Implement auth", doc.GetProperty("goal").GetString());
        Assert.Equal("Active", doc.GetProperty("status").GetString());
        Assert.Equal(1, doc.GetProperty("activeTasks").GetArrayLength());
    }

    [Fact]
    public async Task WorkUnit_Compact_omits_branch_and_successCriteria()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-2", "Refactor DB"));
        var manager = BuildManager(workUnits);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Compact, WorkUnitId: "WU-2"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.True(doc.TryGetProperty("workUnitId", out _));
        Assert.False(doc.TryGetProperty("branchId", out _));
        Assert.False(doc.TryGetProperty("successCriteria", out _));
    }

    [Fact]
    public async Task WorkUnit_Emergency_returns_status_and_count_only()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-3", "Fix crash"));
        var tasks = new FakeTaskService();
        tasks.Seed(MakeTask("T-3", "WU-3", TaskStatus.InProgress));
        var manager = BuildManager(workUnits, tasks);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Emergency, WorkUnitId: "WU-3"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.True(doc.TryGetProperty("activeTaskCount", out var count));
        Assert.Equal(1, count.GetInt32());
        Assert.False(doc.TryGetProperty("goal", out _));
    }

    [Fact]
    public async Task WorkUnit_without_scope_returns_list()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-A", "Goal A", "branch-x"));
        workUnits.Seed(MakeWorkUnit("WU-B", "Goal B", "branch-y"));
        var manager = BuildManager(workUnits);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkUnit, ProjectionLevel.Normal, BranchId: "branch-x"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal(1, doc.GetProperty("count").GetInt32());
    }

    // ── Task projection ─────────────────────────────────────────────────────

    [Fact]
    public async Task Task_Normal_separates_open_blocked_completed()
    {
        var tasks = new FakeTaskService();
        tasks.Seed(MakeTask("T-1", "WU-X", TaskStatus.Open));
        tasks.Seed(MakeTask("T-2", "WU-X", TaskStatus.Blocked));
        tasks.Seed(MakeTask("T-3", "WU-X", TaskStatus.Completed));
        var manager = BuildManager(tasks: tasks);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.Task, ProjectionLevel.Normal, WorkUnitId: "WU-X"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal(1, doc.GetProperty("openTasks").GetArrayLength());
        Assert.Equal(1, doc.GetProperty("blockedTasks").GetArrayLength());
        Assert.Equal(1, doc.GetProperty("completedTasks").GetArrayLength());
    }

    [Fact]
    public async Task Task_Emergency_returns_count_and_next_task()
    {
        var tasks = new FakeTaskService();
        tasks.Seed(MakeTask("T-A", "WU-Y", TaskStatus.Open));
        tasks.Seed(MakeTask("T-B", "WU-Y", TaskStatus.Open));
        var manager = BuildManager(tasks: tasks);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.Task, ProjectionLevel.Emergency, WorkUnitId: "WU-Y"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal(2, doc.GetProperty("activeCount").GetInt32());
        Assert.True(doc.TryGetProperty("nextTask", out _));
    }

    // ── MergeProposal projection ────────────────────────────────────────────

    [Fact]
    public async Task MergeProposal_Normal_lists_pending_proposals()
    {
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal("MP-1", "feat/x", "main", "Auth feature", "summary", "desc", null, null, null, MergeProposalStatus.ReadyForReview));
        merges.Seed(new MergeProposal("MP-2", "feat/x", "main", "DB fix", "summary", "desc", null, null, null, MergeProposalStatus.Merged));
        var manager = BuildManager(merges: merges);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.MergeProposal, ProjectionLevel.Normal, BranchId: "feat/x"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal(1, doc.GetProperty("pendingProposals").GetArrayLength());
        Assert.Equal(2, doc.GetProperty("reviewStatus").EnumerateObject().Count());
    }

    // ── ExecutionSnapshot projection ────────────────────────────────────────

    [Fact]
    public async Task ExecutionSnapshot_Normal_returns_agent_state()
    {
        var agentRuntime = new FakeAgentRuntimeService();
        agentRuntime.Seed(new ExecutionSnapshot("agent-1", "WU-Z", "Implement login", null, null, ["step1", "step2"], [], 0, 0, null));
        var manager = BuildManager(agentRuntime: agentRuntime);

        var result = await manager.GetAsync(new ProjectionRequest(
            ProjectionType.ExecutionSnapshot, ProjectionLevel.Normal, WorkUnitId: "WU-Z", AgentId: "agent-1"));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.Equal("Implement login", doc.GetProperty("currentGoal").GetString());
        Assert.Equal(2, doc.GetProperty("failureHistory").GetArrayLength());
    }

    [Fact]
    public async Task ExecutionSnapshot_without_agentId_returns_error()
    {
        var manager = BuildManager();

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.ExecutionSnapshot, ProjectionLevel.Normal));

        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.True(doc.TryGetProperty("error", out _));
    }

    // ── AgentWorkspace projection — knowledge artifact inheritance (Slice 10g) ──

    [Fact]
    public async Task AgentWorkspace_includes_own_Research_artifact()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Goal"));
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(MakeArtifact("KA-1", ArtifactType.Research, "WU-1", "Stack", ".NET 8, no Redis"));
        var manager = BuildManager(workUnits, artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: "WU-1"));

        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Contains(payload!.Artifacts.Artifacts, a => a.ArtifactId == "KA-1" && a.Type == ArtifactType.Research);
    }

    [Fact]
    public async Task AgentWorkspace_inherits_parents_Research_artifact_into_the_chain()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-Parent", "Parent goal"));
        workUnits.Seed(MakeWorkUnit("WU-Child", "Child goal", parentWorkUnitId: "WU-Parent"));
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(MakeArtifact("KA-1", ArtifactType.Research, "WU-Parent", "Stack", ".NET 8, no Redis"));
        var manager = BuildManager(workUnits, artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: "WU-Child"));

        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Contains(payload!.Artifacts.Artifacts, a => a.ArtifactId == "KA-1");
    }

    [Fact]
    public async Task AgentWorkspace_InheritedConstraints_walks_two_levels_to_a_grandchild()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-Grandparent", "Root goal"));
        workUnits.Seed(MakeWorkUnit("WU-Parent", "Mid goal", parentWorkUnitId: "WU-Grandparent"));
        workUnits.Seed(MakeWorkUnit("WU-Grandchild", "Leaf goal", parentWorkUnitId: "WU-Parent"));
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(MakeArtifact("KA-1", ArtifactType.Constraint, "WU-Grandparent", "No tokens in cookies", "Auth middleware must not store session tokens."));
        var manager = BuildManager(workUnits, artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: "WU-Grandchild"));

        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Single(payload!.InheritedConstraints, a => a.ArtifactId == "KA-1");
    }

    [Fact]
    public async Task AgentWorkspace_InheritedConstraints_excludes_the_work_units_own_constraints()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Goal"));
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(MakeArtifact("KA-own", ArtifactType.Constraint, "WU-1", "Local rule", "Only applies here"));
        var manager = BuildManager(workUnits, artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal, WorkUnitId: "WU-1"));

        var payload = JsonSerializer.Deserialize<AgentWorkspaceProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Empty(payload!.InheritedConstraints); // not inherited — it's the unit's own
        Assert.Contains(payload.Artifacts.Artifacts, a => a.ArtifactId == "KA-own"); // still visible in the full chain
    }

    // ── Routing ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(ProjectionType.WorkUnit, ProjectionLevel.Full)]
    [InlineData(ProjectionType.Task, ProjectionLevel.Normal)]
    [InlineData(ProjectionType.MergeProposal, ProjectionLevel.Compact)]
    [InlineData(ProjectionType.AuthoritativeState, ProjectionLevel.Emergency)]
    [InlineData(ProjectionType.ExecutionSnapshot, ProjectionLevel.Normal)]
    public async Task GetAsync_routes_all_types_without_throwing(ProjectionType type, ProjectionLevel level)
    {
        var result = await BuildManager().GetAsync(new ProjectionRequest(type, level));
        Assert.Equal(type, result.Type);
        Assert.Equal(level, result.Level);
        Assert.NotEmpty(result.DataJson);
    }

    [Fact]
    public async Task AgentWorkspace_without_workUnitId_returns_error_without_throwing()
    {
        var result = await BuildManager().GetAsync(new ProjectionRequest(ProjectionType.AgentWorkspace, ProjectionLevel.Normal));
        var doc = JsonDocument.Parse(result.DataJson).RootElement;
        Assert.True(doc.TryGetProperty("error", out _));
    }

    // ── WorkspacePathways projection ────────────────────────────────────────

    [Fact]
    public async Task WorkspacePathways_root_work_unit_becomes_GoalStarted_node()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Add dark mode"));
        var manager = BuildManager(workUnits);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var node = Assert.Single(payload!.Nodes);
        Assert.Equal("GoalStarted", node.Kind);
        Assert.Equal("WU-1", node.WorkUnitId);
        Assert.Equal("goal:WU-1", node.NodeId);
    }

    [Fact]
    public async Task WorkspacePathways_merged_proposal_becomes_Integration_node_edged_to_its_goal()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Add dark mode"));
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-1", "goal/WU-1", "main", "Add dark mode", "summary", "desc", null, null, 0.9,
            MergeProposalStatus.Merged, WorkUnitId: "WU-1", AgentId: "agent-1", Model: "sonnet", Provider: "anthropic"));
        var manager = BuildManager(workUnits, merges: merges);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var proposalNode = Assert.Single(payload!.Nodes, n => n.Kind == "Integration");
        Assert.Equal("proposal:MP-1:integration", proposalNode.NodeId);
        Assert.Equal("Agent", proposalNode.ActorKind);
        Assert.Equal("sonnet", proposalNode.ActorModel);

        var edge = Assert.Single(payload.Edges);
        Assert.Equal("goal:WU-1", edge.FromNodeId);
        Assert.Equal("proposal:MP-1:integration", edge.ToNodeId);
        Assert.Equal("Integration", edge.Kind);
    }

    [Fact]
    public async Task WorkspacePathways_rejected_proposal_on_a_child_work_unit_anchors_to_the_root_goal()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-Root", "Ship feature"));
        workUnits.Seed(MakeWorkUnit("WU-Child", "Sub-task", parentWorkUnitId: "WU-Root"));
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-2", "task/WU-Child", "goal/WU-Root", "Sub-task", "summary", "desc", null, null, null,
            MergeProposalStatus.Rejected, WorkUnitId: "WU-Child"));
        var manager = BuildManager(workUnits, merges: merges);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var rejectionNode = Assert.Single(payload!.Nodes, n => n.Kind == "Rejection");
        var edge = Assert.Single(payload.Edges);
        Assert.Equal("goal:WU-Root", edge.FromNodeId); // anchored to root, not the child WU
        Assert.Equal(rejectionNode.NodeId, edge.ToNodeId);
    }

    [Fact]
    public async Task WorkspacePathways_dead_goal_with_no_proposal_becomes_DeadBranch_node()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Crashed goal", status: WorkUnitStatus.DeadLettered));
        var manager = BuildManager(workUnits);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Contains(payload!.Nodes, n => n.Kind == "GoalStarted");
        Assert.Contains(payload.Nodes, n => n.Kind == "DeadBranch");
        Assert.Contains(payload.Edges, e => e.Kind == "DeadEnd");
    }

    [Fact]
    public async Task WorkspacePathways_external_changeset_artifact_becomes_ExternalUpdate_node()
    {
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(new ArtifactRef(
            "EXT-1", ArtifactType.ExternalChangeset, null, ArtifactStatus.Applied, DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: "RepositoryDrift: 2 added, 1 modified, 0 deleted (generation 1)"));
        var manager = BuildManager(artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var node = Assert.Single(payload!.Nodes);
        Assert.Equal("ExternalUpdate", node.Kind);
        Assert.Equal("External", node.ActorKind);
        Assert.Equal("external:EXT-1", node.NodeId);
    }

    [Fact]
    public async Task WorkspacePathways_external_changeset_parses_body_for_files_and_diff_state_ids()
    {
        var artifacts = new FakeArtifactLineageService();
        // Exact shape RepositorySyncService.SyncCoreAsync serializes into ArtifactRef.Body — a
        // plain JsonSerializer.Serialize(new {...}) call, so this is genuinely camelCase, not
        // JsonOptions-normalized. Regression coverage for a case-sensitivity bug: deserializing
        // this without JsonSerializerOptions.Web silently drops every field.
        var body = "{\"repositoryPath\":\"C:/repo\",\"added\":[\"src/A.cs\"],\"modified\":[\"src/B.cs\"],"
            + "\"deleted\":[],\"preSyncSnapshotStateId\":\"kgs-pre\",\"postSyncSnapshotStateId\":\"kgs-post\"}";
        artifacts.Seed(new ArtifactRef(
            "EXT-2", ArtifactType.ExternalChangeset, null, ArtifactStatus.Applied, DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: "RepositoryDrift", Body: body));
        var manager = BuildManager(artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var node = Assert.Single(payload!.Nodes);
        Assert.Equal(["src/A.cs", "src/B.cs"], node.FilesTouched);
        Assert.Equal("kgs-pre", node.ExternalSyncStateIdBefore);
        Assert.Equal("kgs-post", node.ExternalSyncStateIdAfter);
    }

    [Fact]
    public async Task WorkspacePathways_external_changesets_are_omitted_when_branch_scoped()
    {
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(new ArtifactRef(
            "EXT-1", ArtifactType.ExternalChangeset, null, ArtifactStatus.Applied, DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null));
        var manager = BuildManager(artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal, BranchId: "main"));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Empty(payload!.Nodes);
    }

    [Fact]
    public async Task WorkspacePathways_branch_scoped_child_proposal_emits_no_dangling_root_edge()
    {
        // The child work unit is on branch "work-child"; its root-goal parent lives on a
        // different branch, so a branch-scoped request can't see it. The proposal node must
        // still appear, but with no edge — an edge to "goal:WU-Child" would reference a node
        // that is never emitted (children don't get GoalStarted nodes).
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-Root", "Ship feature", branch: "work-root"));
        workUnits.Seed(MakeWorkUnit("WU-Child", "Sub-task", branch: "work-child", parentWorkUnitId: "WU-Root"));
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-9", "work-child", "work-root", "Sub-task", "summary", "desc", null, null, null,
            MergeProposalStatus.Merged, WorkUnitId: "WU-Child"));
        var manager = BuildManager(workUnits, merges: merges);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal, BranchId: "work-child"));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Single(payload!.Nodes, n => n.Kind == "Integration");
        Assert.Empty(payload.Edges);
        var nodeIds = payload.Nodes.Select(n => n.NodeId).ToHashSet();
        Assert.All(payload.Edges, e => Assert.Contains(e.FromNodeId, nodeIds));
    }

    // ── WorkspacePathways — event-sourced derivation ────────────────────────

    [Fact]
    public async Task WorkspacePathways_merged_then_superseded_proposal_keeps_both_history_nodes()
    {
        // The retroactive-history-rewrite fix: a reconciliation constituent's status is
        // Superseded *now*, but the event log proves it integrated first. State-derived, the
        // Integration moment vanished; event-sourced, both nodes exist with true timestamps
        // and a lifecycle chain edge between them.
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Add feature"));
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-1", "work-1", "candidate", "Add feature", "summary", "desc", null, null, null,
            MergeProposalStatus.Superseded, WorkUnitId: "WU-1"));
        var events = new FakeExecutionEventStream();
        var appliedAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var supersededAt = DateTimeOffset.UtcNow.AddMinutes(-2);
        events.Seed(ExecutionEventKind.MergeApplied,
            new MergeAppliedPayload("MP-1", "candidate", "", "snap-42"), appliedAt, "WU-1");
        events.Seed(ExecutionEventKind.MergeProposalStatusChanged,
            new MergeProposalStatusChangedPayload("MP-1", MergeProposalStatus.Merged, MergeProposalStatus.Superseded), supersededAt, "WU-1");
        var manager = BuildManager(workUnits, merges: merges, eventStream: events);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var integration = Assert.Single(payload!.Nodes, n => n.Kind == "Integration");
        var superseded = Assert.Single(payload.Nodes, n => n.Kind == "Superseded");
        Assert.Equal("snap-42", integration.SnapshotId);          // point-in-time anchor from the event
        Assert.Equal(appliedAt, integration.OccurredAt);          // true merge time, not DiffGeneratedAt
        Assert.Equal(supersededAt, superseded.OccurredAt);
        Assert.Contains(payload.Edges, e => e.FromNodeId == integration.NodeId && e.ToNodeId == superseded.NodeId);
    }

    [Fact]
    public async Task WorkspacePathways_rejection_event_carries_reviewer_identity()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Fix bug"));
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-2", "work-1", "main", "Fix bug", "summary", "desc", null, null, null,
            MergeProposalStatus.Rejected, WorkUnitId: "WU-1"));
        var events = new FakeExecutionEventStream();
        events.Seed(ExecutionEventKind.ProposalRejected,
            new ProposalRejectedPayload("MP-2", "reviewer-agent-7", "tests failed"), DateTimeOffset.UtcNow, "WU-1");
        var manager = BuildManager(workUnits, merges: merges, eventStream: events);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var rejection = Assert.Single(payload!.Nodes, n => n.Kind == "Rejection");
        Assert.Equal("reviewer-agent-7", rejection.ReviewedBy);
    }

    [Fact]
    public async Task WorkspacePathways_state_fallback_uses_proposal_ReviewedBy_when_no_events_exist()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "Old goal"));
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-3", "work-1", "main", "Old goal", "summary", "desc", null, null, null,
            MergeProposalStatus.Merged, WorkUnitId: "WU-1", ReviewedBy: "user"));
        var manager = BuildManager(workUnits, merges: merges); // no event stream at all

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var integration = Assert.Single(payload!.Nodes, n => n.Kind == "Integration");
        Assert.Equal("user", integration.ReviewedBy);
        Assert.Equal("proposal:MP-3:integration", integration.NodeId);
    }

    // ── WorkspacePathways — nested topology ─────────────────────────────────

    [Fact]
    public async Task WorkspacePathways_child_proposal_anchors_to_parents_earlier_proposal_node()
    {
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-Root", "Ship feature"));
        workUnits.Seed(MakeWorkUnit("WU-Child", "Sub-task", parentWorkUnitId: "WU-Root"));
        var merges = new FakeMergeService();
        // Parent's own proposal merged first; the child's proposal came after and should chain
        // to it — nested topology — not jump straight to the root goal node.
        merges.Seed(new MergeProposal(
            "MP-Parent", "work-root", "main", "Ship feature", "summary", "desc", null,
            null, null, MergeProposalStatus.Merged, WorkUnitId: "WU-Root",
            DiffGeneratedAt: DateTimeOffset.UtcNow.AddMinutes(-30)));
        merges.Seed(new MergeProposal(
            "MP-Child", "work-child", "work-root", "Sub-task", "summary", "desc", null,
            null, null, MergeProposalStatus.Merged, WorkUnitId: "WU-Child",
            DiffGeneratedAt: DateTimeOffset.UtcNow.AddMinutes(-5)));
        var manager = BuildManager(workUnits, merges: merges);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.WorkspacePathways, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<WorkspacePathwaysProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var childEdge = Assert.Single(payload!.Edges, e => e.ToNodeId == "proposal:MP-Child:integration");
        Assert.Equal("proposal:MP-Parent:integration", childEdge.FromNodeId);
        var parentEdge = Assert.Single(payload.Edges, e => e.ToNodeId == "proposal:MP-Parent:integration");
        Assert.Equal("goal:WU-Root", parentEdge.FromNodeId);
    }

    // ── EngineeringState projection ─────────────────────────────────────────

    [Fact]
    public async Task EngineeringState_decision_on_a_merged_work_unit_is_current()
    {
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-1", "goal/WU-1", "main", "Add auth", "summary", "desc", null, null, null,
            MergeProposalStatus.Merged, WorkUnitId: "WU-1"));
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "Use JWT", "Reason: stateless"));
        var manager = BuildManager(merges: merges, artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.EngineeringState, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<EngineeringStateProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var fact = Assert.Single(payload!.Facts);
        Assert.Equal("DEC-1", fact.ArtifactId);
        Assert.True(fact.IsCurrent);
        Assert.Empty(fact.SupersededBy);
    }

    [Fact]
    public async Task EngineeringState_decision_on_an_unmerged_work_unit_is_excluded()
    {
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-1", "goal/WU-1", "main", "Add auth", "summary", "desc", null, null, null,
            MergeProposalStatus.ReadyForReview, WorkUnitId: "WU-1"));
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "Use JWT"));
        var manager = BuildManager(merges: merges, artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.EngineeringState, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<EngineeringStateProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Empty(payload!.Facts);
    }

    [Fact]
    public async Task EngineeringState_global_constraint_is_included_unconditionally()
    {
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(new ArtifactRef(
            "CON-1", ArtifactType.Constraint, null, ArtifactStatus.Active, DateTimeOffset.UtcNow,
            OwnedByWorkUnitId: null, OwnedByAgentId: null, Title: "No MediatR", Body: "Promoted finding"));
        var manager = BuildManager(artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.EngineeringState, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<EngineeringStateProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var fact = Assert.Single(payload!.Facts);
        Assert.Equal("CON-1", fact.ArtifactId);
        Assert.True(fact.IsCurrent);
    }

    [Fact]
    public async Task EngineeringState_supersession_chain_resolves_to_current_truth()
    {
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-1", "goal/WU-1", "main", "Pick ORM", "summary", "desc", null, null, null,
            MergeProposalStatus.Merged, WorkUnitId: "WU-1"));
        merges.Seed(new MergeProposal(
            "MP-2", "goal/WU-2", "main", "Revisit ORM", "summary", "desc", null, null, null,
            MergeProposalStatus.Merged, WorkUnitId: "WU-2"));
        var artifacts = new FakeArtifactLineageService();
        var original = MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "Use EF Core");
        artifacts.Seed(original);
        var successor = MakeArtifact("DEC-2", ArtifactType.Decision, "WU-2", "Use Dapper") with
        {
            CreatedAt = original.CreatedAt.AddMinutes(1),
            Supersedes = ["DEC-1"],
        };
        artifacts.Seed(successor);
        var manager = BuildManager(merges: merges, artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.EngineeringState, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<EngineeringStateProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        Assert.Equal(2, payload!.Facts.Count);
        var oldFact = payload.Facts.Single(f => f.ArtifactId == "DEC-1");
        Assert.False(oldFact.IsCurrent);
        Assert.Equal(["DEC-2"], oldFact.SupersededBy);
        var newFact = payload.Facts.Single(f => f.ArtifactId == "DEC-2");
        Assert.True(newFact.IsCurrent);
    }

    [Fact]
    public async Task EngineeringState_supersession_claim_from_an_unmerged_work_unit_does_not_retire_a_promoted_fact()
    {
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-1", "goal/WU-1", "main", "Pick ORM", "summary", "desc", null, null, null,
            MergeProposalStatus.Merged, WorkUnitId: "WU-1"));
        merges.Seed(new MergeProposal(
            "MP-2", "goal/WU-2", "main", "Revisit ORM", "summary", "desc", null, null, null,
            MergeProposalStatus.ReadyForReview, WorkUnitId: "WU-2"));
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "Use EF Core"));
        artifacts.Seed(MakeArtifact("DEC-2", ArtifactType.Decision, "WU-2", "Use Dapper") with { Supersedes = ["DEC-1"] });
        var manager = BuildManager(merges: merges, artifacts: artifacts);

        var result = await manager.GetAsync(new ProjectionRequest(ProjectionType.EngineeringState, ProjectionLevel.Normal));

        var payload = JsonSerializer.Deserialize<EngineeringStateProjectionPayload>(result.DataJson, JsonSerializerOptions.Web);
        var fact = Assert.Single(payload!.Facts);
        Assert.Equal("DEC-1", fact.ArtifactId);
        Assert.True(fact.IsCurrent);
    }

    [Fact]
    public async Task EngineeringState_two_calls_produce_the_same_facts_in_the_same_order()
    {
        // GeneratedAt legitimately differs per call, so this compares Facts (the deterministic
        // part — principle 2) rather than raw JSON bytes.
        var merges = new FakeMergeService();
        merges.Seed(new MergeProposal(
            "MP-1", "goal/WU-1", "main", "Add auth", "summary", "desc", null, null, null,
            MergeProposalStatus.Merged, WorkUnitId: "WU-1"));
        var artifacts = new FakeArtifactLineageService();
        artifacts.Seed(MakeArtifact("DEC-1", ArtifactType.Decision, "WU-1", "Use JWT"));
        var manager = BuildManager(merges: merges, artifacts: artifacts);

        var first = await manager.GetAsync(new ProjectionRequest(ProjectionType.EngineeringState, ProjectionLevel.Normal));
        var second = await manager.GetAsync(new ProjectionRequest(ProjectionType.EngineeringState, ProjectionLevel.Normal));

        // List<T> has no value equality, so record equality on Facts (which carry List<string>
        // properties from JSON deserialization) would spuriously differ — compare via
        // re-serialization instead, which is the actual "byte-identical" property principle 2 cares
        // about.
        var firstFacts = JsonSerializer.Deserialize<EngineeringStateProjectionPayload>(first.DataJson, JsonSerializerOptions.Web)!.Facts;
        var secondFacts = JsonSerializer.Deserialize<EngineeringStateProjectionPayload>(second.DataJson, JsonSerializerOptions.Web)!.Facts;
        Assert.Equal(JsonSerializer.Serialize(firstFacts), JsonSerializer.Serialize(secondFacts));
    }
}
