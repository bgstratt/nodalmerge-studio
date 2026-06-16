using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class WorkUnitDagTests
{
    private static (IOrchestratorService orchestrator, IWorkUnitService workUnits) BuildServices()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());
        return (
            app.Services.GetRequiredService<IOrchestratorService>(),
            app.Services.GetRequiredService<IWorkUnitService>());
    }

    // ── Parent/child creation ──────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_rejects_unknown_parent()
    {
        var (orchestrator, workUnits) = BuildServices();

        var parent = await orchestrator.CreateWorkUnitAsync("parent goal", "test");
        var child = parent with
        {
            WorkUnitId = "child-1",
            ParentWorkUnitId = "does-not-exist",
        };

        await Assert.ThrowsAsync<KeyNotFoundException>(() => workUnits.CreateAsync(child));
    }

    [Fact]
    public async Task Children_with_distinct_FileScope_are_queryable()
    {
        var (orchestrator, workUnits) = BuildServices();

        var parent = await orchestrator.CreateWorkUnitAsync("parent goal", "test");

        var child1 = await orchestrator.CreateWorkUnitAsync(
            "child one", "test",
            parentWorkUnitId: parent.WorkUnitId,
            fileScope: ["src/api/**"]);

        var child2 = await orchestrator.CreateWorkUnitAsync(
            "child two", "test",
            parentWorkUnitId: parent.WorkUnitId,
            fileScope: ["src/ui/**"]);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);

        Assert.Equal(2, children.Count);
        Assert.Contains(children, c => c.WorkUnitId == child1.WorkUnitId);
        Assert.Contains(children, c => c.WorkUnitId == child2.WorkUnitId);
        Assert.Equal(["src/api/**"], children.First(c => c.WorkUnitId == child1.WorkUnitId).FileScope);
        Assert.Equal(["src/ui/**"], children.First(c => c.WorkUnitId == child2.WorkUnitId).FileScope);
    }

    [Fact]
    public async Task GetChildrenAsync_returns_empty_for_root_with_no_children()
    {
        var (orchestrator, workUnits) = BuildServices();
        var root = await orchestrator.CreateWorkUnitAsync("standalone goal", "test");

        var children = await workUnits.GetChildrenAsync(root.WorkUnitId);

        Assert.Empty(children);
    }

    // ── Dependency edges ───────────────────────────────────────────────────

    [Fact]
    public async Task GetDependentsAsync_returns_units_that_depend_on_given_unit()
    {
        var (orchestrator, workUnits) = BuildServices();

        var a = await orchestrator.CreateWorkUnitAsync("unit A", "test");
        var b = await orchestrator.CreateWorkUnitAsync("unit B", "test", dependsOn: [a.WorkUnitId]);
        var c = await orchestrator.CreateWorkUnitAsync("unit C", "test", dependsOn: [a.WorkUnitId]);
        // D depends on B, not A — should not appear in A's dependents list.
        await orchestrator.CreateWorkUnitAsync("unit D", "test", dependsOn: [b.WorkUnitId]);

        var dependentsOfA = await workUnits.GetDependentsAsync(a.WorkUnitId);

        Assert.Equal(2, dependentsOfA.Count);
        Assert.Contains(dependentsOfA, w => w.WorkUnitId == b.WorkUnitId);
        Assert.Contains(dependentsOfA, w => w.WorkUnitId == c.WorkUnitId);
    }

    // ── Single-worker path unchanged ───────────────────────────────────────

    [Fact]
    public async Task Root_work_unit_has_null_parent_and_empty_scope()
    {
        var (orchestrator, _) = BuildServices();
        var wu = await orchestrator.CreateWorkUnitAsync("goal", "test");

        Assert.Null(wu.ParentWorkUnitId);
        Assert.Empty(wu.DependsOn);
        Assert.Empty(wu.FileScope);
    }
}
