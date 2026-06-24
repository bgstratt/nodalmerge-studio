using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class WorkSchedulerAwaitingFileLeaseTests
{
    [Fact]
    public async Task MarkAwaitingFileLeaseAsync_parks_the_item_instead_of_removing_it()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        await scheduler.EnqueueAsync(unit.WorkUnitId, "worker");

        // Sanity: before parking, the item is acquirable.
        var acquired = await scheduler.TryAcquireAsync("agent-1");
        Assert.NotNull(acquired);
        Assert.Equal(unit.WorkUnitId, acquired!.WorkUnitId);

        // A real run would call ReleaseAsync next; simulate the AwaitingFileLease outcome
        // instead — the item must stay queued, not be dropped like ReleaseAsync's success/failure
        // paths do.
        await scheduler.MarkAwaitingFileLeaseAsync(unit.WorkUnitId);

        var pending = await scheduler.ListPendingAsync();
        Assert.Contains(pending, i => i.WorkUnitId == unit.WorkUnitId);

        // Parked: TryAcquireAsync must skip it, same as an AwaitingResume item.
        var skipped = await scheduler.TryAcquireAsync("agent-2");
        Assert.Null(skipped);

        // Cleared: now eligible again.
        await scheduler.ClearAwaitingFileLeaseAsync(unit.WorkUnitId);

        var resumed = await scheduler.TryAcquireAsync("agent-3");
        Assert.NotNull(resumed);
        Assert.Equal(unit.WorkUnitId, resumed!.WorkUnitId);
    }

    [Fact]
    public async Task ClearAwaitingFileLeaseAsync_on_an_item_not_parked_is_a_noop()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        await scheduler.EnqueueAsync(unit.WorkUnitId, "worker");

        await scheduler.ClearAwaitingFileLeaseAsync(unit.WorkUnitId);

        var acquired = await scheduler.TryAcquireAsync("agent-1");
        Assert.NotNull(acquired);
        Assert.Equal(unit.WorkUnitId, acquired!.WorkUnitId);
    }
}
