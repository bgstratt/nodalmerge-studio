using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/phase-d-implementation.md D3 — plan-staleness signals only, never auto-replan.
/// IPlanStalenessService is hooked from two existing checkpoints (ArtifactCommandService.RecordAsync
/// for a superseding Decision, InMemoryDeadLetterService.RecordFailureAsync for a dead-lettered
/// slice) — no polling timer. Each threshold is exercised at, and one below, its configured value;
/// a third test proves crossing a threshold never enqueues anything.
/// </summary>
[Trait("Category", "Integration")]
public class PlanStalenessSignalTests
{
    private static async Task<ArtifactRef> RecordSupersedingDecisionAsync(
        IArtifactCommandService artifacts, string workUnitId, string supersededId) =>
        await artifacts.RecordAsync(
            workUnitId, "Decision", $"Revised decision superseding {supersededId}",
            "Because the original approach no longer fits.", supersedes: [supersededId]);

    [Fact]
    public async Task Superseding_decisions_at_threshold_raise_the_signal_but_one_below_does_not()
    {
        await using var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var artifacts = app.Services.GetRequiredService<IArtifactCommandService>();
        var events = app.Services.GetRequiredService<IExecutionEventStream>();
        var options = app.Services.GetRequiredService<WorkspaceOptions>();

        var parent = await orchestratorSvc.CreateWorkUnitAsync("Parent goal", "integration-test");
        var plan = await artifacts.RecordPlanAsync(
            parent.WorkUnitId, """{"slices":[{"sliceId":"s1","goal":"g","fileScope":[],"dependsOn":[],"steps":[]}]}""");

        var threshold = options.PlanStalenessSupersedingDecisionThreshold;
        Assert.True(threshold > 1, "test assumes a threshold of at least 2 to exercise the one-below case");

        // One below threshold — the signal must not fire yet.
        for (var i = 0; i < threshold - 1; i++)
            await RecordSupersedingDecisionAsync(artifacts, parent.WorkUnitId, $"KA-old-{i}");

        var beforeThreshold = await events.GetEventsByKindAsync([ExecutionEventKind.PlanStalenessSignalRaised]);
        Assert.DoesNotContain(beforeThreshold, e => e.WorkUnitId == parent.WorkUnitId);

        // Crossing the threshold — the signal must fire now.
        await RecordSupersedingDecisionAsync(artifacts, parent.WorkUnitId, "KA-old-last");

        var afterThreshold = await events.GetEventsByKindAsync([ExecutionEventKind.PlanStalenessSignalRaised]);
        var raised = Assert.Single(afterThreshold, e => e.WorkUnitId == parent.WorkUnitId);
        Assert.Contains("SupersedingDecisions", raised.PayloadJson);
        Assert.Contains(plan.ArtifactId, raised.PayloadJson);
    }

    [Fact]
    public async Task Dead_lettered_siblings_at_threshold_raise_the_signal_but_one_below_does_not()
    {
        await using var app = StudioWebApplication.Build(
            [], configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var deadLetter = app.Services.GetRequiredService<IDeadLetterService>();
        var events = app.Services.GetRequiredService<IExecutionEventStream>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();
        var options = app.Services.GetRequiredService<WorkspaceOptions>();

        var parent = await orchestratorSvc.CreateWorkUnitAsync("Parent goal", "integration-test");

        var threshold = options.PlanStalenessDeadLetteredSliceThreshold;
        Assert.True(threshold > 1, "test assumes a threshold of at least 2 to exercise the one-below case");

        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var children = new List<WorkUnit>();
        for (var i = 0; i < threshold; i++)
        {
            children.Add(await orchestratorSvc.CreateWorkUnitAsync(
                $"Slice {i}", "integration-test",
                parentWorkUnitId: parent.WorkUnitId, seedFromBranchId: parent.BranchId, sliceId: $"s{i}"));
        }

        // The dead-letter-eligible transitions (Executing -> DeadLettered) — a freshly created work
        // unit starts life at Created, which mirrors what the real scheduler does before a worker
        // run can fail: Created -> Queued -> Executing.
        async Task MarkExecutingAsync(string workUnitId)
        {
            await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.Queued);
            await workUnits.UpdateStatusAsync(workUnitId, WorkUnitStatus.Executing);
        }

        // One below threshold.
        for (var i = 0; i < threshold - 1; i++)
        {
            await MarkExecutingAsync(children[i].WorkUnitId);
            await deadLetter.RecordFailureAsync(
                children[i].WorkUnitId, $"worker-{i}", PipelineStage.Execute, "worker",
                "Simulated failure for this test", kind: FailureKind.Exception);
        }

        var beforeThreshold = await events.GetEventsByKindAsync([ExecutionEventKind.PlanStalenessSignalRaised]);
        Assert.DoesNotContain(beforeThreshold, e => e.WorkUnitId == parent.WorkUnitId);

        // Crossing the threshold.
        await MarkExecutingAsync(children[^1].WorkUnitId);
        await deadLetter.RecordFailureAsync(
            children[^1].WorkUnitId, "worker-last", PipelineStage.Execute, "worker",
            "Simulated failure for this test", kind: FailureKind.Exception);

        var afterThreshold = await events.GetEventsByKindAsync([ExecutionEventKind.PlanStalenessSignalRaised]);
        var raised = Assert.Single(afterThreshold, e => e.WorkUnitId == parent.WorkUnitId);
        Assert.Contains("DeadLetteredSlices", raised.PayloadJson);

        // No auto-replan anywhere — crossing the threshold must never enqueue anything itself.
        var pending = await scheduler.ListPendingAsync();
        Assert.Empty(pending);
    }
}
