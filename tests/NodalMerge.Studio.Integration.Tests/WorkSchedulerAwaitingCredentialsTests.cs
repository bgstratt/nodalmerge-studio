using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class WorkSchedulerAwaitingCredentialsTests
{
    [Fact]
    public async Task MarkAwaitingCredentialsAsync_parks_the_item_instead_of_removing_it()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        await scheduler.EnqueueAsync(unit.WorkUnitId, "worker");

        await scheduler.MarkAwaitingCredentialsAsync(unit.WorkUnitId);

        var pending = await scheduler.ListPendingAsync();
        Assert.Contains(pending, i => i.WorkUnitId == unit.WorkUnitId);
        Assert.True(pending.Single(i => i.WorkUnitId == unit.WorkUnitId).AwaitingCredentials);

        // Parked: TryAcquireAsync must skip it, same as AwaitingFileLease/AwaitingResume.
        Assert.Null(await scheduler.TryAcquireAsync("agent-1"));
    }

    [Fact]
    public async Task SupplyCredentialsAsync_unparks_the_item_and_warms_the_shared_cache()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var credentialCache = app.Services.GetRequiredService<IRuntimeCredentialCache>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        await scheduler.EnqueueAsync(unit.WorkUnitId, "worker", credentialRef: "ref-1");
        await scheduler.MarkAwaitingCredentialsAsync(unit.WorkUnitId);
        Assert.Null(await scheduler.TryAcquireAsync("agent-1"));

        await scheduler.SupplyCredentialsAsync(
            unit.WorkUnitId, "anthropic", "claude-fake", "http://fake-llm", "sk-resupplied-key");

        var acquired = await scheduler.TryAcquireAsync("agent-1");
        Assert.NotNull(acquired);
        Assert.Equal(unit.WorkUnitId, acquired!.WorkUnitId);
        Assert.Equal("sk-resupplied-key", acquired.ApiKey);
        Assert.False(acquired.AwaitingCredentials);

        var cached = credentialCache.TryGet("ref-1");
        Assert.NotNull(cached);
        Assert.Equal("sk-resupplied-key", cached!.ApiKey);
    }

    [Fact]
    public async Task ApiKey_never_appears_in_the_persisted_scheduler_payload()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var nodeStore = app.Services.GetRequiredService<IStudioNodeStore>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        await scheduler.EnqueueAsync(unit.WorkUnitId, "worker", apiKey: "sk-must-not-be-persisted");

        var records = await nodeStore.ReadAllNodesAsync(StudioNodeKind.SchedulerV1);
        Assert.Contains(records, r => r.EntityId == unit.WorkUnitId);
        Assert.All(records, r => Assert.DoesNotContain("sk-must-not-be-persisted", r.PayloadJson));
    }
}
