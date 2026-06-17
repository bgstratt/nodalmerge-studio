using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Orchestrator;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Slice 13g — FanOutService.EnsureChildWorkUnitsAsync had a pre-existing concurrency race: two
/// concurrent fan-out calls for the same parent (an explicit caller racing the orchestrator
/// loop's own post-turn fan-out) could each read the same pre-creation snapshot of existing
/// children and independently decide a plan slice had no child yet, creating two. A per-parent
/// SemaphoreSlim in FanOutService now serializes the read-children/create-children/enqueue
/// section so this can't happen. See LlmProfileSelectionTests.cs's
/// SeedPlanAndSpawnOrchestratorAsync comment for the workaround this fix removes the need for
/// (not removed here, since it's harmless either way and not part of this slice's scope).
/// </summary>
[Trait("Category", "Integration")]
public class FanOutConcurrencyTests
{
    // Every store operation behind AddInMemoryStorage completes synchronously (Task.FromResult /
    // Task.CompletedTask, no real I/O), so awaiting it never actually yields back to the
    // scheduler — without a genuine async gap, "concurrent" calls to TryFanOutFromPlanAsync would
    // just run to completion one after another and never truly overlap, regardless of whether the
    // race is fixed. This decorator inserts a real Task.Delay (a genuine yield point) around the
    // exact read FanOutService.BuildSliceMapAsync uses to snapshot existing children, widening the
    // race window enough for Task.WhenAll's concurrent calls to actually interleave there.
    private sealed class DelayedChildrenWorkUnitService(IWorkUnitService inner) : IWorkUnitService
    {
        public Task<WorkUnit> CreateAsync(WorkUnit workUnit, CancellationToken cancellationToken = default) =>
            inner.CreateAsync(workUnit, cancellationToken);

        public Task<WorkUnit> UpdateStatusAsync(string workUnitId, WorkUnitStatus status, string? sessionId = null, CancellationToken cancellationToken = default) =>
            inner.UpdateStatusAsync(workUnitId, status, sessionId, cancellationToken);

        public Task<WorkUnit> SetCurrentStageAsync(string workUnitId, PipelineStage? stage, CancellationToken cancellationToken = default) =>
            inner.SetCurrentStageAsync(workUnitId, stage, cancellationToken);

        public Task<WorkUnit?> GetAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            inner.GetAsync(workUnitId, cancellationToken);

        public Task<IReadOnlyList<WorkUnit>> ListAsync(string? branchId = null, CancellationToken cancellationToken = default) =>
            inner.ListAsync(branchId, cancellationToken);

        public async Task<IReadOnlyList<WorkUnit>> GetChildrenAsync(string parentId, CancellationToken cancellationToken = default)
        {
            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
            return await inner.GetChildrenAsync(parentId, cancellationToken).ConfigureAwait(false);
        }

        public Task<IReadOnlyList<WorkUnit>> GetDependentsAsync(string workUnitId, CancellationToken cancellationToken = default) =>
            inner.GetDependentsAsync(workUnitId, cancellationToken);
    }

    [Fact]
    public async Task Concurrent_fan_out_calls_for_the_same_parent_create_exactly_one_child_per_slice()
    {
        var app = StudioWebApplication.Build(
            [],
            configureServices: services =>
            {
                services.AddInMemoryStorage();
                services.AddSingleton<IWorkUnitService>(sp =>
                    new DelayedChildrenWorkUnitService(sp.GetRequiredService<InMemoryWorkUnitService>()));
            });

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnits    = app.Services.GetRequiredService<IWorkUnitService>();
        var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
        var fanOut        = app.Services.GetRequiredService<IFanOutService>();
        var agentControl  = app.Services.GetRequiredService<IAgentControlService>();

        var parent = await orchestrator.CreateWorkUnitAsync("Build several things", "test");

        await agentControl.SpawnAsync(
            "orchestrator", parent.WorkUnitId,
            model: "fake", baseUrl: "http://fake", apiKey: "fake");

        var planJson = """
            {
              "slices": [
                { "sliceId": "s1", "goal": "Implement Foo.cs", "fileScope": ["src/Foo.cs"], "dependsOn": [], "steps": ["Create Foo.cs"] },
                { "sliceId": "s2", "goal": "Implement Bar.cs", "fileScope": ["src/Bar.cs"], "dependsOn": [], "steps": ["Create Bar.cs"] },
                { "sliceId": "s3", "goal": "Implement Baz.cs", "fileScope": ["src/Baz.cs"], "dependsOn": [], "steps": ["Create Baz.cs"] }
              ]
            }
            """;
        await fileWorkspace.WriteAsync(parent.BranchId, PlanDocumentPaths.FileName, planJson);

        // Several concurrent calls racing for the same parent — before the 13g fix, each one
        // independently sees the plan's three slices unmapped (no children created yet) and
        // creates its own set, so this would produce up to 3x duplicate children per slice.
        var calls = Enumerable.Range(0, 5).Select(_ => fanOut.TryFanOutFromPlanAsync(parent.WorkUnitId));
        await Task.WhenAll(calls);

        var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);

        Assert.Equal(3, children.Count);
        foreach (var sliceId in new[] { "s1", "s2", "s3" })
            Assert.Single(children, c => c.FanOutInfo?.SliceId == sliceId);
    }
}
