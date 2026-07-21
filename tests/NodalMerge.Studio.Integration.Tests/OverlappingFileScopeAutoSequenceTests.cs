using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

// Replaces the old NonOverlappingFileScopeRule[Tests]/NonOverlappingFileScopeFanOutTests — that
// rule (opt-in, default off) rejected the second of two overlapping siblings at BeforeEnqueue and
// left it stuck with a FanOutInfo.BlockedReason until a human noticed and retried. Two sibling
// slices sharing a file with no dependsOn between them is a planning gap, not something worth a
// human-facing block: FanOutService.AutoSequenceOverlappingSiblingsAsync now inserts the missing
// dependsOn edge itself, always on, no toggle — see that method's own comment for the ordering and
// cycle-safety reasoning.
[Trait("Category", "Integration")]
public class OverlappingFileScopeAutoSequenceTests
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
    public async Task Overlapping_siblings_with_no_declared_dependency_get_auto_sequenced()
    {
        await using var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits    = app.Services.GetRequiredService<IWorkUnitService>();
        var artifacts    = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut       = app.Services.GetRequiredService<IFanOutService>();

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", PlanJson));

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        var s1 = children.Single(c => c.FanOutInfo?.SliceId == "s1");
        var s2 = children.Single(c => c.FanOutInfo?.SliceId == "s2");

        // s1 was created first (plan array order, no unmet deps on either), so it's the "older"
        // sibling in the auto-sequencing's deterministic ordering — it enqueues immediately.
        Assert.Contains(s1.WorkUnitId, result.EnqueuedWorkUnitIds);
        Assert.Equal(WorkUnitStatus.Queued, s1.Status);

        // s2 gets sequenced behind s1 instead of rejected — no BlockedReason, just a real
        // dependsOn edge that IsReadyToEnqueueAsync now correctly holds it back on.
        Assert.DoesNotContain(s2.WorkUnitId, result.EnqueuedWorkUnitIds);
        Assert.Equal(WorkUnitStatus.Created, s2.Status);
        Assert.Null(s2.FanOutInfo?.BlockedReason);
        Assert.Contains(s1.WorkUnitId, s2.DependsOn);
    }

    [Fact]
    public async Task Auto_sequenced_sibling_enqueues_once_its_dependency_actually_merges()
    {
        await using var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits    = app.Services.GetRequiredService<IWorkUnitService>();
        var artifacts    = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut       = app.Services.GetRequiredService<IFanOutService>();

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", PlanJson));

        await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        var s1 = children.Single(c => c.FanOutInfo?.SliceId == "s1");
        var s2 = children.Single(c => c.FanOutInfo?.SliceId == "s2");
        Assert.Equal(WorkUnitStatus.Created, s2.Status);

        // Same "Proposed isn't enough" gate real dependsOn already enforces (FanOutServiceTests):
        // a lease-avoidance edge behaves exactly like a declared one, not a weaker imitation of it.
        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Executing);
        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Proposed);
        var stillWaiting = await fanOut.TryEnqueueReadyDependentsAsync(parent.WorkUnitId);
        Assert.DoesNotContain(s2.WorkUnitId, stillWaiting.EnqueuedWorkUnitIds);

        await workUnits.UpdateStatusAsync(s1.WorkUnitId, WorkUnitStatus.Merged);
        var dependentResult = await fanOut.TryEnqueueReadyDependentsAsync(parent.WorkUnitId);

        Assert.Contains(s2.WorkUnitId, dependentResult.EnqueuedWorkUnitIds);
        var s2Updated = await workUnits.GetAsync(s2.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Queued, s2Updated!.Status);
    }

    [Fact]
    public async Task Non_overlapping_siblings_are_not_sequenced_and_both_enqueue_immediately()
    {
        const string disjointPlanJson = """
            {
              "slices": [
                { "sliceId": "s1", "goal": "Implement Foo.cs", "fileScope": ["src/Foo.cs"], "dependsOn": [], "steps": ["edit"] },
                { "sliceId": "s2", "goal": "Implement Bar.cs", "fileScope": ["src/Bar.cs"], "dependsOn": [], "steps": ["edit"] }
              ]
            }
            """;

        await using var app = StudioWebApplication.Build(
            [],
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits    = app.Services.GetRequiredService<IWorkUnitService>();
        var artifacts    = app.Services.GetRequiredService<IArtifactLineageService>();
        var fanOut       = app.Services.GetRequiredService<IFanOutService>();

        var parent = await orchestrator.CreateWorkUnitAsync("Build Foo and Bar", "test");
        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}", ArtifactType.Plan, parent.WorkUnitId,
            ArtifactStatus.Active, DateTimeOffset.UtcNow, parent.WorkUnitId, null, "Plan", disjointPlanJson));

        var result = await fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId);

        Assert.Equal(2, result.EnqueuedWorkUnitIds.Count);
        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        Assert.All(children, c => Assert.Empty(c.DependsOn));
    }
}
