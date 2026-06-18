using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the success criteria from Slice 10f.5 — Intent Graph & Conflict Resolver, scoped to
/// what's buildable without Phase 4 fan-out (no real concurrent workers exist yet). As-built:
/// only "Option A — strict partitioning" (detect overlap, attach a non-blocking ConflictWarning)
/// is implemented; region locking, optimistic execution, and the revalidation loop all assume
/// real concurrent execution and are deferred — see the 10f.5 as-built note.
/// </summary>
[Trait("Category", "Integration")]
public class IntentGraphTests
{
    private static ChangeIntent MakeIntent(
        string id, string workUnitId, string targetPath, string region = "") =>
        new(id, workUnitId, "modify", targetPath, region, "", null, DateTimeOffset.UtcNow);

    // ── RecordIntentAsync / QueryIntentsAsync ────────────────────────────────

    [Fact]
    public async Task QueryIntentsAsync_returns_intents_ordered_by_CreatedAt_scoped_to_one_work_unit()
    {
        var store = new InMemoryStudioNodeStore();
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);

        await graph.RecordIntentAsync(MakeIntent("CI-1", "WU-1", "src/a.cs"));
        await graph.RecordIntentAsync(MakeIntent("CI-2", "WU-1", "src/b.cs"));
        await graph.RecordIntentAsync(MakeIntent("CI-3", "WU-2", "src/c.cs"));

        var intents = await graph.QueryIntentsAsync("WU-1");

        Assert.Equal(["CI-1", "CI-2"], intents.Select(i => i.IntentId));
    }

    [Fact]
    public async Task RecordIntentAsync_called_twice_with_same_IntentId_is_a_noop()
    {
        var store = new InMemoryStudioNodeStore();
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);
        var original = MakeIntent("CI-1", "WU-1", "src/a.cs");

        await graph.RecordIntentAsync(original);
        await graph.RecordIntentAsync(original with { TargetPath = "src/changed.cs" });

        var intents = await graph.QueryIntentsAsync("WU-1");
        Assert.Single(intents);
        Assert.Equal("src/a.cs", intents[0].TargetPath); // first record wins
    }

    [Fact]
    public async Task RecordIntentAsync_mirrors_into_the_artifact_lineage_store()
    {
        var store = new InMemoryStudioNodeStore();
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);

        await graph.RecordIntentAsync(MakeIntent("CI-1", "WU-1", "src/a.cs"));

        var artifact = await artifacts.GetAsync("CI-1");
        Assert.NotNull(artifact);
        Assert.Equal(ArtifactType.ChangeIntent, artifact!.Type);
        Assert.Equal("WU-1", artifact.OwnedByWorkUnitId);
    }

    // ── QueryOverlappingAsync ─────────────────────────────────────────────

    [Fact]
    public async Task QueryOverlappingAsync_finds_whole_file_overlap_across_work_units()
    {
        var store = new InMemoryStudioNodeStore();
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);

        var a = MakeIntent("CI-A", "WU-A", "src/shared.cs");
        var b = MakeIntent("CI-B", "WU-B", "src/shared.cs");
        await graph.RecordIntentAsync(a);
        await graph.RecordIntentAsync(b);

        var overlapping = await graph.QueryOverlappingAsync(a);

        Assert.Single(overlapping, i => i.IntentId == "CI-B");
    }

    [Fact]
    public async Task QueryOverlappingAsync_distinct_regions_on_the_same_file_do_not_overlap()
    {
        var store = new InMemoryStudioNodeStore();
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);

        var a = MakeIntent("CI-A", "WU-A", "src/shared.cs", region: "method:Foo");
        var b = MakeIntent("CI-B", "WU-B", "src/shared.cs", region: "method:Bar");
        await graph.RecordIntentAsync(a);
        await graph.RecordIntentAsync(b);

        Assert.Empty(await graph.QueryOverlappingAsync(a));
    }

    [Fact]
    public async Task QueryOverlappingAsync_same_region_on_the_same_file_overlaps()
    {
        var store = new InMemoryStudioNodeStore();
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);

        var a = MakeIntent("CI-A", "WU-A", "src/shared.cs", region: "method:Foo");
        var b = MakeIntent("CI-B", "WU-B", "src/shared.cs", region: "method:Foo");
        await graph.RecordIntentAsync(a);
        await graph.RecordIntentAsync(b);

        Assert.Single(await graph.QueryOverlappingAsync(a), i => i.IntentId == "CI-B");
    }

    [Fact]
    public async Task QueryOverlappingAsync_ignores_intents_from_the_same_work_unit()
    {
        var store = new InMemoryStudioNodeStore();
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);

        var a = MakeIntent("CI-A", "WU-1", "src/shared.cs");
        var b = MakeIntent("CI-B", "WU-1", "src/shared.cs"); // same work unit, sequential intents
        await graph.RecordIntentAsync(a);
        await graph.RecordIntentAsync(b);

        Assert.Empty(await graph.QueryOverlappingAsync(a));
    }

    // ── RemoveIntentAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task RemoveIntentAsync_removes_from_both_work_unit_and_path_indices()
    {
        var store = new InMemoryStudioNodeStore();
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);
        var a = MakeIntent("CI-A", "WU-1", "src/shared.cs");
        var b = MakeIntent("CI-B", "WU-2", "src/shared.cs");
        await graph.RecordIntentAsync(a);
        await graph.RecordIntentAsync(b);

        await graph.RemoveIntentAsync("CI-A");

        Assert.Empty(await graph.QueryIntentsAsync("WU-1"));
        Assert.Empty(await graph.QueryOverlappingAsync(b)); // CI-A no longer findable as an overlap
    }

    // ── Scheduler integration ────────────────────────────────────────────

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    [Fact]
    public async Task EnqueueAsync_attaches_ConflictWarning_when_a_sibling_declared_an_overlapping_intent()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);
        var workspaces = new AgentWorkspaceService(
            store, new SingleServiceProvider(new NoopWorkUnitService()), new NoopFileWorkspaceService(), events);
        var scheduler = new WorkSchedulerService(
            store, events, workspaces, intents: graph);

        // WU-A has already declared it intends to touch src/shared.cs before WU-B is enqueued.
        await graph.RecordIntentAsync(MakeIntent("CI-A", "WU-A", "src/shared.cs"));

        await scheduler.EnqueueAsync("WU-B", "worker", sessionId: "SES-1");
        // WU-B declares its own (overlapping) intent only after being enqueued the first time —
        // re-enqueue (idempotent retry) should now surface the conflict.
        await graph.RecordIntentAsync(MakeIntent("CI-B", "WU-B", "src/shared.cs"));
        await scheduler.EnqueueAsync("WU-B", "worker", sessionId: "SES-1");

        var pending = await scheduler.ListPendingAsync();
        var item = pending.Single(i => i.WorkUnitId == "WU-B");

        Assert.NotNull(item.Conflict);
        Assert.Contains("src/shared.cs", item.Conflict!.OverlappingFiles);
        Assert.Contains("WU-A", item.Conflict.ConflictingWorkUnitIds);
    }

    [Fact]
    public async Task EnqueueAsync_does_not_attach_ConflictWarning_when_no_intents_are_declared()
    {
        var store = new InMemoryStudioNodeStore();
        var events = new ExecutionEventStreamService(store);
        var artifacts = new ArtifactLineageService(store);
        var graph = new IntentGraphService(store, artifacts);
        var workspaces = new AgentWorkspaceService(
            store, new SingleServiceProvider(new NoopWorkUnitService()), new NoopFileWorkspaceService(), events);
        var scheduler = new WorkSchedulerService(store, events, workspaces, intents: graph);

        await scheduler.EnqueueAsync("WU-1", "worker", sessionId: "SES-1");

        var item = (await scheduler.ListPendingAsync()).Single(i => i.WorkUnitId == "WU-1");
        Assert.Null(item.Conflict);
    }

    private sealed class NoopWorkUnitService : IWorkUnitService
    {
        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? blockedReason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<WorkUnit?>(null);
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
    }

    private sealed class NoopFileWorkspaceService : IFileWorkspaceService
    {
        public Task InitBranchAsync(string b, string? s = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> ReadAsync(string b, string p, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task WriteAsync(string b, string p, string c, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(string b, string p, CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> ExistsAsync(string b, string p, CancellationToken ct = default) => Task.FromResult(false);
        public Task<IReadOnlyList<string>> ListAsync(string b, string? s = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<string> DiffAsync(string s, string t, CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task ApplyBranchAsync(string s, string t, CancellationToken ct = default) => Task.CompletedTask;
        public Task CopyFilesAsync(string s, string t, IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetWorkingDirectoryAsync(string b, CancellationToken ct = default) => Task.FromResult<string?>(null);
    }
}
