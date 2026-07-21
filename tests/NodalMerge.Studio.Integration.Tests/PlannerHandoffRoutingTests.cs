using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;
using StudioArtifactStatus = NodalMerge.Studio.Contracts.Domain.ArtifactStatus;

namespace NodalMerge.Studio.Integration.Tests;

// Regression for the "zombie reconciliation unit" pattern: a PARENTED orchestrator work unit
// (reconciliation units are the canonical case — their parent is the goal whose proposals they
// combine) enqueues a planner on itself; when that planner item completed, the scheduler's
// ReleaseAsync resolved its "orchestrator target" via the parent walk that is only correct for
// worker items, aimed the whole post-planner handoff (and reinvoke) at the OUTER goal's
// orchestrator, and the freshly recorded plan never fanned out — the unit sat Executing forever
// with a Plan, no children, and no queue item.
[Trait("Category", "Integration")]
public class PlannerHandoffRoutingTests
{
    private const string PlanJson = """
        {
          "slices": [
            {
              "sliceId": "s1",
              "goal": "Combine both versions of Shared.cs",
              "fileScope": ["src/Shared.cs"],
              "dependsOn": [],
              "steps": ["Merge the two edits"]
            }
          ]
        }
        """;

    [Fact]
    public async Task Planner_completing_on_a_parented_work_unit_fans_out_that_units_own_plan()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits    = app.Services.GetRequiredService<IWorkUnitService>();
        var artifacts    = app.Services.GetRequiredService<IArtifactLineageService>();
        var scheduler    = app.Services.GetRequiredService<IWorkScheduler>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();

        var goal = await orchestrator.CreateWorkUnitAsync("outer goal", "test");
        var reconciliation = await orchestrator.CreateWorkUnitAsync(
            "reconcile diverged proposals", "test", parentWorkUnitId: goal.WorkUnitId);

        // Register orchestrator credentials for the reconciliation unit itself, the same way
        // ReconciliationAgentService.TriggerAsync does — fan-out reads them per work unit.
        await agentControl.SpawnAsync(
            "orchestrator", reconciliation.WorkUnitId,
            model: "fake", baseUrl: "http://fake", apiKey: "fake");

        // Simulate the planner run the reconciliation orchestrator enqueues on ITSELF:
        // enqueue + acquire the planner item, record the Plan artifact (what PlannerAgentLoop
        // does), then release with success — the handoff under test.
        await scheduler.EnqueueAsync(
            reconciliation.WorkUnitId, "planner",
            model: "fake", baseUrl: "http://fake", apiKey: "fake");
        var item = await scheduler.TryAcquireAsync("test-planner-agent");
        Assert.NotNull(item);
        Assert.Equal(reconciliation.WorkUnitId, item!.WorkUnitId);

        await artifacts.RecordAsync(new ArtifactRef(
            $"PLAN-{Guid.NewGuid():N}",
            ArtifactType.Plan,
            reconciliation.WorkUnitId,
            StudioArtifactStatus.Active,
            DateTimeOffset.UtcNow,
            reconciliation.WorkUnitId,
            null,
            "Plan",
            PlanJson));

        await scheduler.ReleaseAsync(reconciliation.WorkUnitId, success: true);

        // The plan must fan out on the RECONCILIATION unit (the planner ran on it), not on the
        // outer goal. Before the fix, children landed nowhere: the goal has no plan, and the
        // reconciliation unit's plan was never handed to fan-out.
        var reconciliationChildren = await workUnits.GetChildrenAsync(reconciliation.WorkUnitId);
        var slice = Assert.Single(reconciliationChildren);
        Assert.Equal("s1", slice.FanOutInfo?.SliceId);
        Assert.Equal(WorkUnitStatus.Queued, slice.Status);

        var goalChildren = await workUnits.GetChildrenAsync(goal.WorkUnitId);
        Assert.Equal([reconciliation.WorkUnitId], goalChildren.Select(c => c.WorkUnitId));
    }
}
