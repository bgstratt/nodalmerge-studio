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
        var firstAdvance = await service.ReleaseAndAdvanceAsync("src/Foo.cs");
        Assert.Equal("wu-b", firstAdvance);

        var secondAdvance = await service.ReleaseAndAdvanceAsync("src/Foo.cs");
        Assert.Null(secondAdvance);
    }

    [Fact]
    public async Task ReleaseAndAdvanceAsync_pops_waiters_in_fifo_order()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        await service.TryAcquireOrEnqueueAsync("wu-a", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-b", "src/Foo.cs");
        await service.TryAcquireOrEnqueueAsync("wu-c", "src/Foo.cs");

        var first = await service.ReleaseAndAdvanceAsync("src/Foo.cs");
        Assert.Equal("wu-b", first);

        var (grantedToC, holderAfterB) = await service.TryAcquireOrEnqueueAsync("wu-c", "src/Foo.cs");
        Assert.False(grantedToC);
        Assert.Equal("wu-b", holderAfterB);

        var second = await service.ReleaseAndAdvanceAsync("src/Foo.cs");
        Assert.Equal("wu-c", second);

        var third = await service.ReleaseAndAdvanceAsync("src/Foo.cs");
        Assert.Null(third);
    }

    [Fact]
    public async Task ReleaseAndAdvanceAsync_on_an_unheld_path_returns_null()
    {
        var service = new FileLeaseService(new InMemoryStudioNodeStore());

        var result = await service.ReleaseAndAdvanceAsync("src/NeverTouched.cs");

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

        var next = await service.ReleaseAndAdvanceAsync("src/Foo.cs");

        Assert.Equal("wu-c", next);
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

        var next = await rehydrated.ReleaseAndAdvanceAsync("src/Foo.cs");
        Assert.Equal("wu-b", next);
    }
}
