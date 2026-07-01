using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.McpServer.Tools;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Capability-gap fix — amends a work unit's FileScope in place instead of always forking a sibling
// via SteeringService. See SetFileScopeAsync (InMemoryWorkUnitService.cs).
[Trait("Category", "Integration")]
public class FileScopeAmendmentTests
{
    private static (IOrchestratorService orchestrator, IWorkUnitService workUnits, IExecutionEventStream events, IServiceProvider services) BuildServices()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());
        return (
            app.Services.GetRequiredService<IOrchestratorService>(),
            app.Services.GetRequiredService<IWorkUnitService>(),
            app.Services.GetRequiredService<IExecutionEventStream>(),
            app.Services);
    }

    [Fact]
    public async Task SetFileScopeAsync_amends_an_active_work_unit_in_place()
    {
        var (orchestrator, workUnits, _, _) = BuildServices();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test", fileScope: ["src/old/**"]);

        var updated = await workUnits.SetFileScopeAsync(wu.WorkUnitId, ["src/new/**"]);

        Assert.Equal(["src/new/**"], updated.FileScope);
        var reloaded = await workUnits.GetAsync(wu.WorkUnitId);
        Assert.Equal(["src/new/**"], reloaded!.FileScope);
    }

    [Theory]
    [InlineData(WorkUnitStatus.Completed)]
    [InlineData(WorkUnitStatus.Cancelled)]
    public async Task SetFileScopeAsync_throws_for_terminal_work_units(WorkUnitStatus terminalStatus)
    {
        var (orchestrator, workUnits, _, _) = BuildServices();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");
        await workUnits.UpdateStatusAsync(wu.WorkUnitId, WorkUnitStatus.Active);
        await workUnits.UpdateStatusAsync(wu.WorkUnitId, terminalStatus);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => workUnits.SetFileScopeAsync(wu.WorkUnitId, ["src/new/**"]));
    }

    [Fact]
    public async Task SetFileScopeAsync_appends_WorkUnitFileScopeChanged_event_when_sessionId_given()
    {
        var (orchestrator, workUnits, events, _) = BuildServices();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test", fileScope: ["src/old/**"]);

        await workUnits.SetFileScopeAsync(wu.WorkUnitId, ["src/new/**"], sessionId: "S-1");

        var sessionEvents = await events.GetSessionEventsAsync("S-1");
        var changeEvent = Assert.Single(sessionEvents, e => e.Kind == ExecutionEventKind.WorkUnitFileScopeChanged);
        var payload = JsonSerializer.Deserialize<WorkUnitFileScopeChangedPayload>(changeEvent.PayloadJson);
        Assert.Equal(["src/old/**"], payload!.PreviousScope);
        Assert.Equal(["src/new/**"], payload.NewScope);
    }

    [Fact]
    public async Task MCP_WorkUnitUpdate_amends_fileScope_in_place()
    {
        var (orchestrator, workUnits, _, services) = BuildServices();
        var tools = ActivatorUtilities.CreateInstance<WorkUnitTools>(services);
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test", fileScope: ["src/old/**"]);

        var json = await tools.UpdateAsync(wu.WorkUnitId, fileScope: ["src/new/**"]);
        var doc = JsonDocument.Parse(json).RootElement;

        // UpdateAsync returns the WorkUnit record directly, so its PascalCase property names go
        // through System.Text.Json's default serialization unchanged.
        Assert.Equal(
            ["src/new/**"],
            doc.GetProperty("data").GetProperty("FileScope").EnumerateArray().Select(e => e.GetString()).ToList());

        var reloaded = await workUnits.GetAsync(wu.WorkUnitId);
        Assert.Equal(["src/new/**"], reloaded!.FileScope);
    }
}
