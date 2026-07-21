using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 13a — every other integration test in this suite opts into AddInMemoryStorage(); the
/// production storage path (AddNodalMergeStorage(), i.e. the embedded NodalMerge engine's Sqlite
/// node store + File blob store, which is what StudioWebApplication.Build([]) uses by default
/// since AddStudioServices always calls AddNodalMergeStorage()) had zero behavioral coverage.
/// That blind spot let a real bug ship: NodalMergeBranchService.CreateBranchAsync never called
/// IFileWorkspaceService.InitBranchAsync, so child work units' branches never inherited their
/// parent's files in production even though the in-memory equivalent (InMemoryBranchService) did
/// it correctly. These tests build a real StudioWebApplication against a temp SQLite db + temp
/// workspace root (via StudioWebApplication.Build's configureConfiguration hook, which forwards
/// to NodalMerge.DotNetHost.HostApplication.Build's existing parameter of the same name — this
/// has to run before AddNodalMergeHostProviders binds SqliteNodeStorageOptions/
/// FileBlobStorageOptions, which configureServices runs too late for) to exercise that path end
/// to end, including across a simulated process restart.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class ProductionStorageIntegrationTests : IAsyncLifetime
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-prodstorage-{Guid.NewGuid():N}");

    public Task InitializeAsync() => Task.CompletedTask;

    // B2 (plans/test-suite-remediation-plan.md): async teardown with a bounded retry. Microsoft.Data
    // .Sqlite pools native connections by default, so the db handle stays open on Windows past our
    // last SqliteConnection.Dispose; the old un-retried Directory.Delete raced that and flaked.
    // ClearAllPools + retrying delete now live in the shared helper.
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

    // Mirrors what StudioStateRehydrationService.StartAsync does at real host startup, without
    // also starting InMemoryAgentRuntimeService's scheduler poll loop (the other IHostedService
    // registered alongside it) or binding Kestrel to a port.
    private static async Task RehydrateAsync(Microsoft.AspNetCore.Builder.WebApplication app)
    {
        foreach (var rehydratable in app.Services.GetServices<IRehydratable>())
            await rehydratable.RehydrateAsync();
    }

    [Fact]
    public async Task Child_work_unit_branch_inherits_parents_files_through_production_storage()
    {
        var app = BuildApp();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();

        var parent = await orchestrator.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace.WriteAsync(parent.BranchId, "src/feature.cs", "// parent content");

        var child = await orchestrator.CreateWorkUnitAsync(
            "Slice work", "test",
            parentWorkUnitId: parent.WorkUnitId,
            seedFromBranchId: parent.BranchId);

        Assert.True(await fileWorkspace.ExistsAsync(child.BranchId, "src/feature.cs"));
        Assert.Equal("// parent content", await fileWorkspace.ReadAsync(child.BranchId, "src/feature.cs"));
    }

    [Fact]
    public async Task Second_app_instance_against_the_same_paths_sees_the_first_instances_state()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var fileWorkspace1 = app1.Services.GetRequiredService<IFileWorkspaceService>();

        var workUnit = await orchestrator1.CreateWorkUnitAsync("Implement feature", "test");
        await fileWorkspace1.WriteAsync(workUnit.BranchId, "src/feature.cs", "// durable content");

        await app1.DisposeAsync();

        var app2 = BuildApp();
        await RehydrateAsync(app2);

        var workUnits2 = app2.Services.GetRequiredService<IWorkUnitService>();
        var branches2 = app2.Services.GetRequiredService<IBranchService>();
        var fileWorkspace2 = app2.Services.GetRequiredService<IFileWorkspaceService>();

        var rehydrated = await workUnits2.GetAsync(workUnit.WorkUnitId);
        Assert.NotNull(rehydrated);
        Assert.Equal(workUnit.BranchId, rehydrated!.BranchId);

        var status = await branches2.GetStatusAsync(workUnit.BranchId);
        Assert.Equal("active", status.Status);

        Assert.Equal("// durable content", await fileWorkspace2.ReadAsync(workUnit.BranchId, "src/feature.cs"));
    }
}
