using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 12 — admin override for a file lease stuck with no live agent to StopAsync and no
/// pending proposal to reject (e.g. a crash mid-write before either could run). Exercises the
/// REST surface directly (GET /studio/file-leases, POST /studio/file-leases/release) rather than
/// driving a real agent run — the underlying release mechanics are already covered end-to-end by
/// FileLeaseConflictIntegrationTests; this is specifically about the admin entry point existing
/// and being wired to the same ForceReleaseAllForWorkUnitAsync + scheduler-clear path.
/// </summary>
[Trait("Category", "Integration")]
public class FileLeaseRestEndpointTests
{
    private static WebApplication BuildTestApp() =>
        StudioWebApplication.Build(
            [],
            configureWebHost: webHost => webHost.UseTestServer(),
            configureServices: services => services.AddInMemoryStorage());

    [Fact]
    public async Task GetFileLeases_lists_the_holder_and_its_wait_queue()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        var fileLease = app.Services.GetRequiredService<IFileLeaseService>();

        await fileLease.TryAcquireOrEnqueueAsync("wu-holder", "src/Shared.cs");
        await fileLease.TryAcquireOrEnqueueAsync("wu-waiter", "src/Shared.cs");

        var response = await client.GetAsync("/studio/file-leases");
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(1, json.GetArrayLength());
        var lease = json[0];
        Assert.Equal("src/shared.cs", lease.GetProperty("path").GetString()); // normalized lowercase
        Assert.Equal("wu-holder", lease.GetProperty("holderWorkUnitId").GetString());
        Assert.Equal("wu-waiter", lease.GetProperty("waitQueue")[0].GetString());
    }

    [Fact]
    public async Task ReleaseFileLease_promotes_the_waiter_and_clears_its_scheduler_park()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        var fileLease = app.Services.GetRequiredService<IFileLeaseService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        await fileLease.TryAcquireOrEnqueueAsync("wu-holder", "src/Shared.cs");
        await fileLease.TryAcquireOrEnqueueAsync("wu-waiter", "src/Shared.cs");

        // Simulate the waiter having actually been parked by a real conflict (the shape
        // McpToolDispatcher/WorkerAgentLoop produce), so the endpoint has something real to clear.
        await scheduler.EnqueueAsync("wu-waiter", "worker");
        await scheduler.MarkAwaitingFileLeaseAsync("wu-waiter");

        var response = await client.PostAsJsonAsync(
            "/studio/file-leases/release", new { workUnitId = "wu-holder" });
        response.EnsureSuccessStatusCode();
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal("wu-holder", json.GetProperty("workUnitId").GetString());
        Assert.Equal("wu-waiter", json.GetProperty("promotedWorkUnitIds")[0].GetString());

        var leases = await fileLease.ListAsync();
        Assert.Single(leases);
        Assert.Equal("wu-waiter", leases[0].HolderWorkUnitId);
        Assert.Empty(leases[0].WaitQueue);

        var pending = await scheduler.ListPendingAsync();
        var waiterItem = pending.Single(i => i.WorkUnitId == "wu-waiter");
        Assert.False(waiterItem.AwaitingFileLease, "Promoted waiter must no longer be parked.");
    }

    [Fact]
    public async Task ReleaseFileLease_requires_workUnitId()
    {
        await using var app = BuildTestApp();
        await app.StartAsync();
        var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync("/studio/file-leases/release", new { workUnitId = "" });

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }
}
