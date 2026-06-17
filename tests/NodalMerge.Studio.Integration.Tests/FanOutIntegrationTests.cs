using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 4 slice 11b — planner fan-out into parallel child workers with dependency-aware enqueue.
/// </summary>
[Trait("Category", "Integration")]
public class FanOutIntegrationTests
{
    [Fact]
    public async Task PlannerFanOut_creates_parallel_children_and_proposals()
    {
        var fakeHandler = new FanOutLlmHandler();

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var artifacts       = app.Services.GetRequiredService<IArtifactLineageService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var scheduler       = app.Services.GetRequiredService<IWorkScheduler>();
        var decisionLog     = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var parent = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Build Foo and Bar features",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: parent.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

            // Wait for two child work units.
            IReadOnlyList<WorkUnit> children = [];
            var childrenDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < childrenDeadline)
            {
                children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
                if (children.Count >= 2) break;
                await Task.Delay(100);
            }
            Assert.Equal(2, children.Count);

            // Both children should have been enqueued (may be executing or done).
            var pending = await scheduler.ListPendingAsync();
            Assert.True(
                children.All(c => c.Status is WorkUnitStatus.Queued or WorkUnitStatus.Executing or WorkUnitStatus.Proposed),
                "Expected both children to enter the scheduler pipeline.");

            // Wait for reconciled parent proposal (merger runs after both children complete).
            MergeProposal? reconciled = null;
            var proposalDeadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (DateTimeOffset.UtcNow < proposalDeadline)
            {
                reconciled = (await merge.ListAsync()).FirstOrDefault(p =>
                    p.WorkUnitId == parent.WorkUnitId &&
                    p.ReconciledFrom.Count >= 2 &&
                    p.Status == MergeProposalStatus.ReadyForReview);
                if (reconciled is not null) break;
                await Task.Delay(100);
            }
            Assert.NotNull(reconciled);

            // Parent artifact chain includes Plan.
            var parentChain = await artifacts.GetChainAsync(parent.WorkUnitId);
            Assert.Contains(parentChain, a => a.Type == ArtifactType.Plan);

            // Each child has BranchChangeset after completion.
            foreach (var child in children)
            {
                var chain = await artifacts.GetChainAsync(child.WorkUnitId);
                Assert.Contains(chain, a => a.Type == ArtifactType.BranchChangeset);
                Assert.Contains(chain, a => a.Type == ArtifactType.MergeProposal);
            }

            // Orchestration log includes SpawnPlanner.
            var decisions = await decisionLog.GetEventsAsync(parent.WorkUnitId);
            Assert.Contains(decisions, d => d.Action == OrchestrationAction.SpawnPlanner);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Dependent_slice_is_enqueued_after_dependency_reaches_Proposed()
    {
        var fakeHandler = new FanOutLlmHandler(includeDependentSlice: true);

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var parent = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Build Foo then Bar",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: parent.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

            // Wait for both slices to complete end-to-end.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
                var s2 = children.FirstOrDefault(c => c.FanOutInfo?.SliceId == "s2");
                var proposals = (await merge.ListAsync()).Count(p => p.Status == MergeProposalStatus.ReadyForReview);
                if (children.Count >= 2 && s2?.Status == WorkUnitStatus.Proposed && proposals >= 2)
                    break;
                await Task.Delay(100);
            }

            var finalChildren = await workUnits.GetChildrenAsync(parent.WorkUnitId);
            Assert.Equal(2, finalChildren.Count);
            Assert.All(finalChildren, c => Assert.Equal(WorkUnitStatus.Proposed, c.Status));

            var reconciled = (await merge.ListAsync()).FirstOrDefault(p =>
                p.WorkUnitId == parent.WorkUnitId && p.ReconciledFrom.Count >= 2);
            Assert.NotNull(reconciled);
            Assert.Equal(MergeProposalStatus.ReadyForReview, reconciled!.Status);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
