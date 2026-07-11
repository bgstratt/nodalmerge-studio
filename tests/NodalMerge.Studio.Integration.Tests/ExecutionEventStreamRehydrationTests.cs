using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 13c — ExecutionEventStreamService wrote every appended event to IStudioNodeStore but
/// never read itself back on startup, so a host restart silently dropped event history (and the
/// session index built from it). Reuses 13a/13b's dual-StudioWebApplication-instance harness
/// against the production storage path.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class ExecutionEventStreamRehydrationTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-events-rehydrate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildApp()
    {
        var sqliteDbPath = Path.Combine(_tempRoot, "nodes.db");
        var blobsRootPath = Path.Combine(_tempRoot, "blobs");
        var workspaceRootPath = Path.Combine(_tempRoot, "workspace");

        return StudioWebApplication.Build(
            [],
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = sqliteDbPath,
                ["NodalMerge:Storage:FileBlobs:RootPath"] = blobsRootPath,
                ["Workspace:RootPath"] = workspaceRootPath,
            }));
    }

    private static async Task RehydrateAsync(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        foreach (var rehydratable in app.Services.GetServices<IRehydratable>())
            await rehydratable.RehydrateAsync();
    }

    [Fact]
    public async Task Events_across_sessions_survive_a_restart_in_their_original_order()
    {
        var app1 = BuildApp();
        var events1 = app1.Services.GetRequiredService<IExecutionEventStream>();

        var a1 = await events1.AppendAsync("sess-a", "wu-a", ExecutionEventKind.SessionStarted, new { note = "a1" });
        var a2 = await events1.AppendAsync("sess-a", "wu-a", ExecutionEventKind.WorkUnitStarted, new { note = "a2" });
        var a3 = await events1.AppendAsync("sess-a", "wu-a", ExecutionEventKind.WorkUnitCompleted, new { note = "a3" });

        var b1 = await events1.AppendAsync("sess-b", "wu-b", ExecutionEventKind.SessionStarted, new { note = "b1" });
        var b2 = await events1.AppendAsync("sess-b", "wu-b", ExecutionEventKind.WorkUnitStarted, new { note = "b2" });

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);

        var events2 = app2.Services.GetRequiredService<IExecutionEventStream>();

        var rehydratedA1 = await events2.GetAsync(a1.EventId);
        Assert.NotNull(rehydratedA1);
        Assert.Equal(a1.PayloadJson, rehydratedA1!.PayloadJson);

        var sessionAEvents = await events2.GetSessionEventsAsync("sess-a");
        Assert.Equal(
            new[] { a1.EventId, a2.EventId, a3.EventId },
            sessionAEvents.Select(e => e.EventId));

        var sessionBEvents = await events2.GetSessionEventsAsync("sess-b");
        Assert.Equal(
            new[] { b1.EventId, b2.EventId },
            sessionBEvents.Select(e => e.EventId));
    }
}
