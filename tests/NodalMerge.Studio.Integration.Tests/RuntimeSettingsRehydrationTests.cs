using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 13d — WorkspaceOptions.UseLlmProfileSelection (12d's REST-toggleable setting) was an
/// in-memory singleton mutation with no durable backing, so a host restart silently reverted it
/// to its config-file default. RuntimeSettingsService persists the runtime-mutable subset of
/// WorkspaceOptions to the node store on every /studio/options POST and reapplies it on startup.
/// Reuses 13a/13b/13c's dual-StudioWebApplication-instance harness against the production
/// storage path; exercises the same mutate-then-persist call the /studio/options POST handler
/// makes (StudioRestEndpoints.cs) rather than going over real HTTP, matching 13a's precedent of
/// not starting the full host (Kestrel, scheduler poll loop) in this test suite.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class RuntimeSettingsRehydrationTests : IAsyncLifetime
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-runtimesettings-rehydrate-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry. The old
    // synchronous Dispose ran an un-retried Directory.Delete that raced background writes to
    // nodes.db and flaked; ClearAllPools + retrying delete now live in the shared helper.
    public Task DisposeAsync() => TestTeardown.ClearSqlitePoolsAndDeleteAsync(_tempRoot);

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
    public async Task UseLlmProfileSelection_toggle_survives_a_restart()
    {
        var app1 = BuildApp();
        var options1 = app1.Services.GetRequiredService<WorkspaceOptions>();
        var runtimeSettings1 = app1.Services.GetRequiredService<RuntimeSettingsService>();

        Assert.False(options1.UseLlmProfileSelection);

        // Mirrors exactly what the /studio/options POST handler does.
        options1.UseLlmProfileSelection = true;
        await runtimeSettings1.PersistAsync();

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);

        var options2 = app2.Services.GetRequiredService<WorkspaceOptions>();
        Assert.True(options2.UseLlmProfileSelection);
    }

    [Fact]
    public async Task SchedulerConcurrencySettings_survive_a_restart()
    {
        // Slice 14e — MaxConcurrentWorkers/SchedulerPollIntervalMs join the same runtime-mutable
        // subset UseLlmProfileSelection already covers above.
        var app1 = BuildApp();
        var options1 = app1.Services.GetRequiredService<WorkspaceOptions>();
        var runtimeSettings1 = app1.Services.GetRequiredService<RuntimeSettingsService>();

        Assert.Equal(3, options1.MaxConcurrentWorkers);
        Assert.Equal(2_000, options1.SchedulerPollIntervalMs);

        options1.MaxConcurrentWorkers = 7;
        options1.SchedulerPollIntervalMs = 500;
        await runtimeSettings1.PersistAsync();

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);

        var options2 = app2.Services.GetRequiredService<WorkspaceOptions>();
        Assert.Equal(7, options2.MaxConcurrentWorkers);
        Assert.Equal(500, options2.SchedulerPollIntervalMs);
    }

    [Fact]
    public async Task DocFetchTools_toggle_survives_a_restart()
    {
        var app1 = BuildApp();
        var options1 = app1.Services.GetRequiredService<WorkspaceOptions>();
        var runtimeSettings1 = app1.Services.GetRequiredService<RuntimeSettingsService>();

        Assert.False(options1.DocFetchTools);

        options1.DocFetchTools = true;
        await runtimeSettings1.PersistAsync();

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);

        var options2 = app2.Services.GetRequiredService<WorkspaceOptions>();
        Assert.True(options2.DocFetchTools);
    }
}
