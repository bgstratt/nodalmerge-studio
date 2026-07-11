using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class FileLeaseServiceTests
{
    [Fact]
    public async Task TryAcquireOrEnqueueAsync_grants_immediately_when_unheld()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        var (granted, holder) = await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");

        Assert.True(granted);
        Assert.Equal("wu-a", holder);
    }

    [Fact]
    public async Task TryAcquireOrEnqueueAsync_is_idempotent_for_the_existing_holder()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        var (granted, holder) = await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");

        Assert.True(granted);
        Assert.Equal("wu-a", holder);
    }

    [Fact]
    public async Task TryAcquireOrEnqueueAsync_queues_a_conflicting_sibling()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        var (granted, holder) = await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");

        Assert.False(granted);
        Assert.Equal("wu-a", holder);
    }

    [Fact]
    public async Task TryAcquireOrEnqueueAsync_queuing_the_same_waiter_twice_is_idempotent()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");

        // Released once — if wu-b had been queued twice, it would now be the holder a second
        // time once we release again rather than the queue being empty.
        var firstAdvance = await service.ReleaseAndAdvanceAsync("wu-a", "src/Foo.cs");
        Assert.Equal("wu-b", firstAdvance);

        var secondAdvance = await service.ReleaseAndAdvanceAsync("wu-b", "src/Foo.cs");
        Assert.Null(secondAdvance);
    }

    [Fact]
    public async Task ReleaseAndAdvanceAsync_pops_waiters_in_fifo_order()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-c", "src/Foo.cs");

        var first = await service.ReleaseAndAdvanceAsync("wu-a", "src/Foo.cs");
        Assert.Equal("wu-b", first);

        var (grantedToC, holderAfterB) = await service.TryAcquireOrEnqueueAsync("wu-c", "src/Foo.cs");
        Assert.False(grantedToC);
        Assert.Equal("wu-b", holderAfterB);

        var second = await service.ReleaseAndAdvanceAsync("wu-b", "src/Foo.cs");
        Assert.Equal("wu-c", second);

        var third = await service.ReleaseAndAdvanceAsync("wu-c", "src/Foo.cs");
        Assert.Null(third);
    }

    [Fact]
    public async Task ReleaseAndAdvanceAsync_on_an_unheld_path_returns_null()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        var result = await service.ReleaseAndAdvanceAsync("wu-x", "src/NeverTouched.cs");

        Assert.Null(result);
    }

    [Fact]
    public async Task ForceReleaseAllForWorkUnitAsync_advances_every_path_the_unit_held()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Bar.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-c", "src/Bar.cs");

        // The promoted waiters are returned, not just advanced internally — a caller (e.g.
        // InMemoryDeadLetterService, InMemoryAgentRuntimeService.StopAsync) needs these IDs to
        // clear each one's scheduler-level AwaitingFileLease flag, or a promoted waiter would sit
        // parked forever despite already holding the lease it was waiting on.
        var promoted = await service.ForceReleaseAllForWorkUnitAsync("wu-a");
        Assert.Equal(new[] { "wu-b", "wu-c" }, promoted.OrderBy(x => x));

        var (fooGranted, fooHolder) = await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");
        Assert.True(fooGranted);
        Assert.Equal("wu-b", fooHolder);

        var (barGranted, barHolder) = await service.TryAcquireOrEnqueueAsync("wu-c", "src/Bar.cs");
        Assert.True(barGranted);
        Assert.Equal("wu-c", barHolder);
    }

    [Fact]
    public async Task ForceReleaseAllForWorkUnitAsync_drops_the_unit_from_queues_it_was_waiting_in()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-c", "src/Foo.cs");

        // wu-b crashes/dead-letters while only queued, never holding anything.
        await service.ForceReleaseAllForWorkUnitAsync("wu-b");

        var next = await service.ReleaseAndAdvanceAsync("wu-a", "src/Foo.cs");

        Assert.Equal("wu-c", next);
    }

    // Reported live bug: two already-running work units each hold one file and want the other's
    // — the classic 2-cycle deadlock. Before the fix, wu-b's call below would just queue forever
    // (both sides permanently parked, since the file lease is held until merge, not released
    // between calls) — the test itself is the regression guard: it must actually return.
    [Fact]
    public async Task Two_tasks_each_wanting_the_others_held_file_resolves_the_deadlock()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Bar.cs");

        // wu-a now wants Bar.cs (held by wu-b) — no cycle yet, just a normal queue.
        var (grantedA, holderA) = await service.TryAcquireOrEnqueueAsync("wu-a", "src/Bar.cs");
        Assert.False(grantedA);
        Assert.Equal("wu-b", holderA);

        // wu-b now wants Foo.cs (held by wu-a) — this closes the cycle.
        var (grantedB, holderB) = await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");

        // Tie-broken victim (equal 1-lease holdings, "wu-a" < "wu-b" ordinally) is wu-a — its
        // lease is released to break the cycle, so wu-b (already holding Bar.cs) gets Foo.cs too
        // instead of both sides hanging.
        Assert.True(grantedB);
        Assert.Equal("wu-b", holderB);

        var leases = await service.ListAsync();
        Assert.All(leases, l => Assert.Equal("wu-b", l.HolderWorkUnitId));

        // wu-a was dropped from Bar.cs's wait queue too, not just stripped of what it held —
        // otherwise it would resurface as a stale waiter once wu-b eventually releases Bar.cs.
        var barLease = leases.Single(l => l.Path == "src/bar.cs");
        Assert.DoesNotContain("wu-a", barLease.WaitQueue);
    }

    [Fact]
    public async Task Deadlock_resolution_records_a_dependsOn_edge_from_the_victim_to_the_survivor()
    {
        var workUnits = new RecordingWorkUnitService();
        var services = new SingleServiceProvider(workUnits);
        var service = new FileLeaseService(new InMemoryStudioNodeStore(), services);

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Bar.cs");
        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Bar.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");

        // wu-a (the victim) now depends on wu-b (the survivor) — a retry or future fan-out won't
        // race this same pair again; it'll wait for wu-b to actually merge first.
        Assert.Contains("wu-b", workUnits.DependsOn.GetValueOrDefault("wu-a", []));
    }

    // Reported concern: "stop all" on a previous session's goals must not leave the new session's
    // work blocked on stale leases from an unrelated goal — and more generally, two independent
    // goals that happen to touch the same relative file path in the shared repo should never
    // lease-block each other at all. That's on the user to resolve via merge/reconciliation if
    // they choose to run two goals against the same files concurrently, not something the
    // scheduler should proactively serialize.
    [Fact]
    public async Task Two_unrelated_root_goals_never_contend_for_the_same_path()
    {
        var workUnits = new NoSharedRootWorkUnitService();
        var services = new SingleServiceProvider(workUnits);
        var service = new FileLeaseService(new InMemoryStudioNodeStore(), services);

        // goal-1 and goal-2 are each their own root — no common ancestor — even though both
        // touch the exact same path.
        var (grantedFirst, holderFirst) = await service.TryAcquireOrEnqueueAsync("goal-1", "src/Foo.cs");
        Assert.True(grantedFirst);
        Assert.Equal("goal-1", holderFirst);

        var (grantedSecond, holderSecond) = await service.TryAcquireOrEnqueueAsync("goal-2", "src/Foo.cs");

        // Must be granted immediately, not queued behind goal-1 — an unrelated goal's lease is
        // invisible to this one.
        Assert.True(grantedSecond);
        Assert.Equal("goal-2", holderSecond);
    }

    private sealed class NoSharedRootWorkUnitService : IWorkUnitService
    {
        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default) =>
            Task.FromResult<WorkUnit?>(new WorkUnit(workUnitId, "goal", "branch-1", WorkUnitStatus.Created,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner", null, null, null, null, [], []));

        public Task<WorkUnit> AddDependencyAsync(string workUnitId, string dependsOnWorkUnitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? blockedReason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementReviewRejectionCountAsync(string workUnitId, bool automated, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementFailureAttemptCountAsync(string workUnitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> AmendGoalForSteeredRetryAsync(string workUnitId, string amendedGoal, string steeringContext, string deadLetterEntryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
    }

    private sealed class SingleServiceProvider(object service) : IServiceProvider
    {
        public object? GetService(Type serviceType) => serviceType.IsInstanceOfType(service) ? service : null;
    }

    private sealed class RecordingWorkUnitService : IWorkUnitService
    {
        public Dictionary<string, List<string>> DependsOn { get; } = [];

        public Task<WorkUnit> AddDependencyAsync(string workUnitId, string dependsOnWorkUnitId, CancellationToken ct = default)
        {
            if (!DependsOn.TryGetValue(workUnitId, out var list))
                DependsOn[workUnitId] = list = [];
            if (!list.Contains(dependsOnWorkUnitId))
                list.Add(dependsOnWorkUnitId);
            return Task.FromResult(new WorkUnit(workUnitId, "goal", "branch-1", WorkUnitStatus.Created,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner", null, null, null, null, list, []));
        }

        // wu-a and wu-b are modeled as siblings under a shared "goal-root" — the scenario this
        // fake exists to test (deadlock between two contending siblings) only applies within one
        // root goal; two work units with no common root must never lease-block each other (see
        // FileLeaseService.ResolveScopeIdAsync's own doc comment).
        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken ct = default)
        {
            var parentWorkUnitId = workUnitId == "goal-root" ? null : "goal-root";
            return Task.FromResult<WorkUnit?>(new WorkUnit(workUnitId, "goal", "branch-1", WorkUnitStatus.Created,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "owner", null, null, null, parentWorkUnitId,
                DependsOn.GetValueOrDefault(workUnitId, []), []));
        }

        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFanOutBlockedReasonAsync(string workUnitId, string? blockedReason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementReviewRejectionCountAsync(string workUnitId, bool automated, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> IncrementFailureAttemptCountAsync(string workUnitId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> AmendGoalForSteeredRetryAsync(string workUnitId, string amendedGoal, string steeringContext, string deadLetterEntryId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<WorkUnit> SetFileScopeAsync(string workUnitId, IReadOnlyList<string> fileScope, string? sessionId = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<WorkUnit>>([]);
    }

    [Fact]
    public async Task Lease_state_survives_rehydration()
    {
        var nodeStore = new InMemoryStudioNodeStore();
        var original = new FileLeaseService(nodeStore);
        await original.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        await original.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");

        var rehydrated = new FileLeaseService(nodeStore);
        await rehydrated.RehydrateAsync();

        var (granted, holder) = await rehydrated.TryAcquireOrEnqueueAsync("wu-c", "src/Foo.cs");
        Assert.False(granted);
        Assert.Equal("wu-a", holder);

        var next = await rehydrated.ReleaseAndAdvanceAsync("wu-a", "src/Foo.cs");
        Assert.Equal("wu-b", next);
    }
}
