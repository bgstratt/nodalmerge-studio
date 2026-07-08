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

            // Reconciliation only folds a child's proposal in once it's been Approved — a fanned-out
            // child defaults to TaskReviewPolicy.HumanRequired, so simulate the human approval each
            // child needs before the batch can ever produce a reconciled parent proposal.
            var approvedProposalIds = new HashSet<string>();
            var approvalDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < approvalDeadline && approvedProposalIds.Count < 2)
            {
                var readyProposals = (await merge.ListAsync())
                    .Where(p => p.Status == MergeProposalStatus.ReadyForReview
                        && p.WorkUnitId is not null
                        && children.Any(c => c.WorkUnitId == p.WorkUnitId)
                        && !approvedProposalIds.Contains(p.ProposalId));
                foreach (var proposal in readyProposals)
                {
                    await merge.ReviewAsync(proposal.ProposalId, MergeProposalStatus.Approved);
                    approvedProposalIds.Add(proposal.ProposalId);
                }
                if (approvedProposalIds.Count < 2)
                    await Task.Delay(100);
            }
            Assert.Equal(2, approvedProposalIds.Count);

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
    public async Task Dependent_slice_is_enqueued_only_after_dependency_is_Merged()
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

            // s2 dependsOn s1 — Phase 12 requires s1 to be Merged (not just Proposed) before s2
            // is ever enqueued, so wait for s1's own proposal first and confirm s2 hasn't started.
            MergeProposal? s1Proposal = null;
            var s1Deadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < s1Deadline)
            {
                var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
                var s1 = children.FirstOrDefault(c => c.FanOutInfo?.SliceId == "s1");
                if (s1?.Status == WorkUnitStatus.Proposed)
                {
                    s1Proposal = (await merge.ListAsync()).FirstOrDefault(p =>
                        p.WorkUnitId == s1.WorkUnitId && p.Status == MergeProposalStatus.ReadyForReview);
                    if (s1Proposal is not null) break;
                }
                await Task.Delay(100);
            }
            Assert.NotNull(s1Proposal);

            var stillWaitingChildren = await workUnits.GetChildrenAsync(parent.WorkUnitId);
            var s2BeforeMerge = stillWaitingChildren.FirstOrDefault(c => c.FanOutInfo?.SliceId == "s2");
            Assert.Equal(WorkUnitStatus.Created, s2BeforeMerge!.Status);

            var approved = await merge.ReviewAsync(s1Proposal!.ProposalId, MergeProposalStatus.Approved);
            await merge.ApplyAsync(approved.ProposalId);

            // Now s2 should pick up and run all the way through to its own proposal.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var children = await workUnits.GetChildrenAsync(parent.WorkUnitId);
                var s2 = children.FirstOrDefault(c => c.FanOutInfo?.SliceId == "s2");
                var proposals = (await merge.ListAsync()).Count(p => p.Status == MergeProposalStatus.ReadyForReview);
                if (children.Count >= 2 && s2?.Status == WorkUnitStatus.Proposed && proposals >= 1)
                    break;
                await Task.Delay(100);
            }

            var finalChildren = await workUnits.GetChildrenAsync(parent.WorkUnitId);
            Assert.Equal(2, finalChildren.Count);
            var s1Final = finalChildren.Single(c => c.FanOutInfo?.SliceId == "s1");
            var s2Final = finalChildren.Single(c => c.FanOutInfo?.SliceId == "s2");
            Assert.Equal(WorkUnitStatus.Merged, s1Final.Status);
            Assert.Equal(WorkUnitStatus.Proposed, s2Final.Status);

            // s2's branch must contain s1's merged Foo.cs even though s2's own fileScope never
            // declared an interest in it — only FanOutService's dependsOn-driven branch refresh
            // puts it there, not anything s2 itself wrote.
            var fileWorkspace = app.Services.GetRequiredService<IFileWorkspaceService>();
            var fooInS2Branch = await fileWorkspace.ReadAsync(s2Final.BranchId, "src/Foo.cs");
            Assert.Equal("class Foo {}", fooInS2Branch);

            // Reconciliation only folds a child's proposal in once it's been Approved — s1 was
            // already approved above, so approve s2's proposal too before expecting the reconciled
            // parent proposal to appear.
            var s2Proposal = (await merge.ListAsync()).Single(p =>
                p.WorkUnitId == s2Final.WorkUnitId && p.Status == MergeProposalStatus.ReadyForReview);
            await merge.ReviewAsync(s2Proposal.ProposalId, MergeProposalStatus.Approved);

            MergeProposal? reconciled = null;
            var reconciledDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
            while (DateTimeOffset.UtcNow < reconciledDeadline)
            {
                reconciled = (await merge.ListAsync()).FirstOrDefault(p =>
                    p.WorkUnitId == parent.WorkUnitId && p.ReconciledFrom.Count >= 2);
                if (reconciled is not null) break;
                await Task.Delay(100);
            }
            Assert.NotNull(reconciled);
            Assert.Equal(MergeProposalStatus.ReadyForReview, reconciled!.Status);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
