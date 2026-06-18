using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Slice 14b success criteria — two slices with overlapping fileScope, no dependency between them.
// No orchestrator agent is spawned here (unlike FanOutServiceTests.cs) — these tests only need
// IFanOutService.TryFanOutFromPlanAsync's own enqueue/policy-gate behavior, and spawning a real
// orchestrator starts a background agent loop whose own post-turn fan-out (OrchestratorAgentLoop.cs)
// can race the test's explicit call under heavy parallel test load, consuming the work before the
// explicit call sees anything left to do.
[Trait("Category", "Integration")]
public class NonOverlappingFileScopeFanOutTests
{
    private const string PlanJson = """
        {
          "slices": [
            { "sliceId": "s1", "goal": "Implement Foo.cs (part 1)", "fileScope": ["src/Foo.cs"], "dependsOn": [], "steps": ["edit"] },
            { "sliceId": "s2", "goal": "Implement Foo.cs (part 2)", "fileScope": ["src/Foo.cs"], "dependsOn": [], "steps": ["edit"] }
          ]
        }
        """;

    [Fact]
    public async Task Toggle_off_both_overlapping_slices_enqueue_unchanged_from_today()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator  = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits     = app.Services.GetRequiredService<IWorkUnitService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var fanOut        = app.Services.GetRequiredService<IFanOutService>();
        var options       = app.Services.GetRequiredService<WorkspaceOptions>();

        Assert.False(options.BlockOverlappingFileScope);

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo", "test");
        await fileWorkspace.WriteAsync(parent.BranchId, PlanDocumentPaths.FileName, PlanJson);

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        Assert.Equal(2, result.EnqueuedWorkUnitIds.Count);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        Assert.All(children, c => Assert.Equal(WorkUnitStatus.Queued, c.Status));
    }

    [Fact]
    public async Task Toggle_on_second_overlapping_slice_is_rejected_at_enqueue()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator  = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits     = app.Services.GetRequiredService<IWorkUnitService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var fanOut        = app.Services.GetRequiredService<IFanOutService>();
        var options       = app.Services.GetRequiredService<WorkspaceOptions>();
        var decisionLog   = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        options.BlockOverlappingFileScope = true;

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo", "test");
        await fileWorkspace.WriteAsync(parent.BranchId, PlanDocumentPaths.FileName, PlanJson);

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        Assert.Single(result.EnqueuedWorkUnitIds);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        var s1 = children.Single(c => c.FanOutInfo?.SliceId == "s1");
        var s2 = children.Single(c => c.FanOutInfo?.SliceId == "s2");

        Assert.Equal(WorkUnitStatus.Queued, s1.Status);
        Assert.Null(s1.FanOutInfo?.BlockedReason);

        Assert.Equal(WorkUnitStatus.Created, s2.Status);
        Assert.NotNull(s2.FanOutInfo?.BlockedReason);
        Assert.Contains("s1", s2.FanOutInfo!.BlockedReason);

        var events = await decisionLog.GetEventsAsync(parent.WorkUnitId);
        Assert.Contains(events, e => e.Action == OrchestrationAction.PolicyBlocked);
    }

    [Fact]
    public async Task Toggle_on_blocked_slice_enqueues_once_the_conflicting_sibling_finishes()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator  = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits     = app.Services.GetRequiredService<IWorkUnitService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var fanOut        = app.Services.GetRequiredService<IFanOutService>();
        var options       = app.Services.GetRequiredService<WorkspaceOptions>();

        options.BlockOverlappingFileScope = true;

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo", "test");
        await fileWorkspace.WriteAsync(parent.BranchId, PlanDocumentPaths.FileName, PlanJson);

        await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        var s1 = children.Single(c => c.FanOutInfo?.SliceId == "s1");
        var s2 = children.Single(c => c.FanOutInfo?.SliceId == "s2");
        Assert.Equal(WorkUnitStatus.Created, s2.Status);

        // s1 finishes running — no longer "active," so s2 is no longer blocked on retry.
        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Executing);
        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Proposed);

        var retryResult = await fanOut.TryEnqueueReadyDependentsAsync(parent.WorkUnitId);

        Assert.Contains(s2.WorkUnitId, retryResult.EnqueuedWorkUnitIds);
        var s2Updated = await workUnits.GetAsync(s2.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Queued, s2Updated!.Status);
        Assert.Null(s2Updated.FanOutInfo?.BlockedReason);
    }
}
