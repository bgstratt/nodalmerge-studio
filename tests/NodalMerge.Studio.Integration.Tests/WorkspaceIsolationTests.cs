using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the success criteria from Slice 10c — Workspace Isolation, adapted to the
/// "wrapper" backing model agreed for v1: AgentWorkspace tracks lifecycle and enforces
/// FileScope over the work unit's existing branch/file-copy isolation rather than a real
/// git worktree (see WorkspaceBackingModel.LogicalBranchWorkspace). Real worktrees are a
/// future, pluggable backing model, not this slice.
/// </summary>
[Trait("Category", "Integration")]
public class WorkspaceIsolationTests
{
    private sealed class FakeWorkUnitService : IWorkUnitService
    {
        private readonly Dictionary<string, WorkUnit> _units = new();

        public void Seed(WorkUnit workUnit) => _units[workUnit.WorkUnitId] = workUnit;

        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken ct = default)
        {
            _units[workUnit.WorkUnitId] = workUnit;
            return Task.FromResult(workUnit);
        }

        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? blockedReason, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default)
        {
            _units.TryGetValue(workUnitId, out var wu);
            return Task.FromResult(wu);
        }

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>(_units.Values.Where(w => branchId is null || w.BranchId == branchId).ToList());

        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);

        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<WorkUnit>>([]);

        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeFileWorkspaceService : IFileWorkspaceService
    {
        private readonly Dictionary<string, Dictionary<string, string>> _branches = new();

        public Task InitBranchAsync(string branchId, string? seedFromBranchId = null, CancellationToken ct = default)
        {
            _branches.TryAdd(branchId, new Dictionary<string, string>());
            return Task.CompletedTask;
        }

        public Task<string?> ReadAsync(string branchId, string relativePath, CancellationToken ct = default) =>
            Task.FromResult(_branches.TryGetValue(branchId, out var files) && files.TryGetValue(relativePath, out var c) ? c : null);

        public Task<IReadOnlyList<WorkspaceFileRead>> ReadManyAsync(string branchId, IReadOnlyList<string> paths, CancellationToken ct = default)
        {
            var files = _branches.TryGetValue(branchId, out var b) ? b : null;
            IReadOnlyList<WorkspaceFileRead> results = paths
                .Select(p => files is not null && files.TryGetValue(p, out var c)
                    ? new WorkspaceFileRead(p, c, true)
                    : new WorkspaceFileRead(p, null, false))
                .ToList();
            return Task.FromResult(results);
        }

        public Task WriteAsync(string branchId, string relativePath, string content, CancellationToken ct = default)
        {
            if (!_branches.TryGetValue(branchId, out var files))
                _branches[branchId] = files = new Dictionary<string, string>();
            files[relativePath] = content;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(string branchId, string relativePath, CancellationToken ct = default)
        {
            if (_branches.TryGetValue(branchId, out var files)) files.Remove(relativePath);
            return Task.CompletedTask;
        }

        public Task<bool> ExistsAsync(string branchId, string relativePath, CancellationToken ct = default) =>
            Task.FromResult(_branches.TryGetValue(branchId, out var files) && files.ContainsKey(relativePath));

        public Task<IReadOnlyList<string>> ListAsync(string branchId, string? subPath = null, string? pattern = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(_branches.TryGetValue(branchId, out var files) ? files.Keys.ToList() : []);

        public Task<(IReadOnlyList<WorkspaceSearchMatch> Matches, bool Truncated)> SearchAsync(
            string branchId, string query, string? subPath = null, string? filePattern = null,
            bool regex = false, bool caseSensitive = false, int contextLines = 3, int maxResults = 200,
            CancellationToken ct = default) =>
            Task.FromResult<(IReadOnlyList<WorkspaceSearchMatch>, bool)>(([], false));

        public Task<WorkspaceReplaceResult> ReplaceAsync(
            string branchId, string relativePath, string oldText, string newText, int expectedMatches = 1,
            CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceReplaceResult(0, 0, 0, string.Empty));

        public Task<string> DiffAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default) =>
            Task.FromResult(string.Empty);

        public Task ApplyBranchAsync(string sourceBranchId, string targetBranchId, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task CopyFilesAsync(string sourceBranchId, string targetBranchId, IReadOnlyList<string> relativePaths, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<string?> GetWorkingDirectoryAsync(string branchId, CancellationToken ct = default) =>
            Task.FromResult<string?>(_branches.ContainsKey(branchId) ? $"/fake/{branchId}" : null);

        public Task<WorkspaceDiff> DiffExternalPathAsync(string branchId, string externalPath, CancellationToken ct = default) =>
            Task.FromResult(new WorkspaceDiff([], [], [], string.Empty));

        public Task ApplyExternalPathAsync(string branchId, string externalPath, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private static WorkUnit MakeWorkUnit(string id, string branchId, IReadOnlyList<string>? fileScope = null) =>
        new(id, "goal", branchId, WorkUnitStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "owner", null, null, null, null, [], fileScope ?? []);

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private static (AgentWorkspaceService Workspaces, FakeWorkUnitService WorkUnits, FakeFileWorkspaceService FileWorkspace, ExecutionEventStreamService Events) Build()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var workUnits = new FakeWorkUnitService();
        var fileWorkspace = new FakeFileWorkspaceService();
        var workspaces = new AgentWorkspaceService(store, new SingleServiceProvider(workUnits), fileWorkspace, events);
        return (workspaces, workUnits, fileWorkspace, events);
    }

    // ── Isolation: two work units never see each other's writes ─────────────

    [Fact]
    public async Task Two_work_units_with_distinct_branches_are_fully_isolated()
    {
        var (workspaces, workUnits, fileWorkspace, _) = Build();
        workUnits.Seed(MakeWorkUnit("WU-1", "branch-1"));
        workUnits.Seed(MakeWorkUnit("WU-2", "branch-2"));

        await fileWorkspace.InitBranchAsync("branch-1");
        await fileWorkspace.InitBranchAsync("branch-2");
        await fileWorkspace.WriteAsync("branch-1", "src/a.cs", "// from WU-1");
        await fileWorkspace.WriteAsync("branch-2", "src/b.cs", "// from WU-2");

        var ws1 = await workspaces.CreateAsync("WU-1", "main");
        var ws2 = await workspaces.CreateAsync("WU-2", "main");

        Assert.NotEqual(ws1.Path, ws2.Path);
        Assert.False(await fileWorkspace.ExistsAsync("branch-1", "src/b.cs"));
        Assert.False(await fileWorkspace.ExistsAsync("branch-2", "src/a.cs"));
        Assert.True(await fileWorkspace.ExistsAsync("branch-1", "src/a.cs"));
        Assert.True(await fileWorkspace.ExistsAsync("branch-2", "src/b.cs"));
    }

    // ── ValidateWriteAsync — FileScope enforcement ───────────────────────────

    [Fact]
    public async Task ValidateWriteAsync_allows_any_path_when_FileScope_is_empty()
    {
        var (workspaces, _, _, _) = Build();
        Assert.True(await workspaces.ValidateWriteAsync("WU-1", "anything/goes.cs", []));
    }

    [Fact]
    public async Task ValidateWriteAsync_allows_path_matching_glob_scope()
    {
        var (workspaces, _, _, _) = Build();
        Assert.True(await workspaces.ValidateWriteAsync("WU-1", "src/api/handler.cs", ["src/api/**"]));
    }

    [Fact]
    public async Task ValidateWriteAsync_blocks_path_outside_scope()
    {
        var (workspaces, _, _, _) = Build();
        Assert.False(await workspaces.ValidateWriteAsync("WU-1", "src/ui/handler.cs", ["src/api/**"]));
    }

    // ── Lifecycle: Create / Archive / Destroy ────────────────────────────────

    [Fact]
    public async Task CreateAsync_is_idempotent_keyed_by_WorkUnitId()
    {
        var (workspaces, workUnits, fileWorkspace, events) = Build();
        workUnits.Seed(MakeWorkUnit("WU-1", "branch-1"));
        await fileWorkspace.InitBranchAsync("branch-1");

        var first = await workspaces.CreateAsync("WU-1", "main", sessionId: "SES-1");
        var second = await workspaces.CreateAsync("WU-1", "main", sessionId: "SES-1");

        Assert.Equal(first.WorkspaceId, second.WorkspaceId);
        Assert.Equal(first.CreatedAt, second.CreatedAt);

        var sessionEvents = await events.GetSessionEventsAsync("SES-1");
        Assert.Single(sessionEvents, e => e.Kind == ExecutionEventKind.WorkspaceCreated);
    }

    [Fact]
    public async Task ArchiveAsync_populates_DestroyedAt_and_is_idempotent()
    {
        var (workspaces, workUnits, fileWorkspace, _) = Build();
        workUnits.Seed(MakeWorkUnit("WU-1", "branch-1"));
        await fileWorkspace.InitBranchAsync("branch-1");
        var created = await workspaces.CreateAsync("WU-1", "main");

        await workspaces.ArchiveAsync(created.WorkspaceId);
        var archived = await workspaces.GetAsync(created.WorkspaceId);
        Assert.NotNull(archived!.DestroyedAt);

        await workspaces.ArchiveAsync(created.WorkspaceId); // second call is a no-op
        var stillArchived = await workspaces.GetAsync(created.WorkspaceId);
        Assert.Equal(archived.DestroyedAt, stillArchived!.DestroyedAt);
    }

    [Fact]
    public async Task DestroyAsync_populates_DestroyedAt_without_deleting_branch_files()
    {
        var (workspaces, workUnits, fileWorkspace, _) = Build();
        workUnits.Seed(MakeWorkUnit("WU-1", "branch-1"));
        await fileWorkspace.InitBranchAsync("branch-1");
        await fileWorkspace.WriteAsync("branch-1", "src/a.cs", "// partial work");
        var created = await workspaces.CreateAsync("WU-1", "main");

        await workspaces.DestroyAsync(created.WorkspaceId, reason: "work unit abandoned");

        var destroyed = await workspaces.GetAsync(created.WorkspaceId);
        Assert.NotNull(destroyed!.DestroyedAt);
        // The shared branch directory is not touched by Destroy in the wrapper backing model —
        // a failed run stays inspectable.
        Assert.True(await fileWorkspace.ExistsAsync("branch-1", "src/a.cs"));
    }

    // ── Scheduler integration ────────────────────────────────────────────────

    [Fact]
    public async Task Scheduler_acquire_creates_workspace_and_success_release_archives_it()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "branch-1"));
        var fileWorkspace = new FakeFileWorkspaceService();
        var workspaces = new AgentWorkspaceService(store, new SingleServiceProvider(workUnits), fileWorkspace, events);
        var scheduler = new WorkSchedulerService(store, events, workspaces);

        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");
        Assert.NotNull(await scheduler.TryAcquireAsync("agent-X"));

        var workspace = await workspaces.GetAsync("ws-WU-1");
        Assert.NotNull(workspace);
        Assert.Null(workspace!.DestroyedAt);

        await scheduler.ReleaseAsync("WU-1", success: true);

        var archived = await workspaces.GetAsync("ws-WU-1");
        Assert.NotNull(archived!.DestroyedAt);
    }

    [Fact]
    public async Task Scheduler_failed_release_leaves_workspace_intact_for_retry()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var workUnits = new FakeWorkUnitService();
        workUnits.Seed(MakeWorkUnit("WU-1", "branch-1"));
        var fileWorkspace = new FakeFileWorkspaceService();
        var workspaces = new AgentWorkspaceService(store, new SingleServiceProvider(workUnits), fileWorkspace, events);
        var scheduler = new WorkSchedulerService(store, events, workspaces);

        await scheduler.EnqueueAsync("WU-1", "profile-a", sessionId: "SES-1");
        await scheduler.TryAcquireAsync("agent-X");
        await scheduler.ReleaseAsync("WU-1", success: false);

        var workspace = await workspaces.GetAsync("ws-WU-1");
        Assert.NotNull(workspace);
        Assert.Null(workspace!.DestroyedAt); // not destroyed — the next retry reuses it
    }
}
