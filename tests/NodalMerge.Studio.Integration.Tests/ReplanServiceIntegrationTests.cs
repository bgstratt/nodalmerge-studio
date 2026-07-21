using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 1.4 (plans/orchestrator-reliability-and-observability.md) — the re-plan-the-slice
/// mechanism used by both failure-recovery tracks. Drives the precondition (a dead-lettered
/// slice with a parent) directly rather than through a full orchestrator/fan-out cycle, since
/// that's already covered by FanOutIntegrationTests — this test is scoped to ReplanService's own
/// behavior once that precondition exists.
/// </summary>
[Trait("Category", "Integration")]
public class ReplanServiceIntegrationTests
{
    [Fact]
    public async Task ReplanFailedSliceAsync_creates_new_sibling_slices_and_cancels_the_original()
    {
        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ReplanFailedSliceLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();
        var replan          = app.Services.GetRequiredService<IReplanService>();

        var parent = await orchestratorSvc.CreateWorkUnitAsync(goal: "Parent goal", owner: "integration-test");

        // Registers orchestrator credentials for the parent synchronously (before the background
        // loop even starts) — this test never needs that loop to do anything, hence the handler's
        // unconditional end_turn for any non-planner conversation.
        await agentControl.SpawnAsync(
            agentType: "orchestrator",
            workUnitId: parent.WorkUnitId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var failedChild = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Original slice goal that will fail",
            owner: "integration-test",
            parentWorkUnitId: parent.WorkUnitId,
            seedFromBranchId: parent.BranchId,
            sliceId: "s-old");

        var entry = await deadLetter.RecordFailureAsync(
            failedChild.WorkUnitId,
            "worker-x",
            PipelineStage.Execute,
            "worker",
            "Simulated failure for this test",
            kind: FailureKind.Exception);

        var result = await replan.ReplanFailedSliceAsync(entry.EntryId);

        Assert.Equal(ReplanOutcome.Replanned, result.Outcome);
        Assert.NotNull(result.NewWorkUnitIds);
        Assert.Equal(2, result.NewWorkUnitIds!.Count);

        var reloadedFailedChild = await workUnits.GetAsync(failedChild.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Cancelled, reloadedFailedChild!.Status);

        var siblings = await workUnits.GetChildrenAsync(parent.WorkUnitId);
        Assert.Equal(3, siblings.Count); // original (now Cancelled) + 2 new sub-slices
        Assert.All(result.NewWorkUnitIds!, id => Assert.Contains(siblings, s => s.WorkUnitId == id));
    }
}
