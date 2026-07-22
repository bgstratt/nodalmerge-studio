using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// plans/recursive-planning-spike.md S5/S5.b — the load-bearing tests.
///
/// S5 proves the architecture bet: a Compound slice is routed to a sub-planner (not a worker), the
/// sub-plan fans out into grandchildren, and — the part that can't be assumed — the grandchildren's
/// changes reconcile BOTTOM-UP through the interior node to the root. Also pins the depth cap
/// (grandchildren forced to workers; a compound grandchild is demoted and counted).
///
/// S5.b gives the "coherent reassembly" claim teeth for the peer seam: a producer/consumer pair over
/// DISJOINT fileScopes bound by a parent-authored contract, where a non-conformant consumer is
/// rejected by review rather than merged green (the exact case S5's clean-merge would hide).
/// </summary>
[Trait("Category", "Integration")]
public class RecursivePlanningIntegrationTests
{
    private static void ConfigureRecursive(IServiceCollection services)
    {
        services.AddInMemoryStorage();
        // Last WorkspaceOptions registration wins (see AddInMemoryStorage's own comment) — enable two
        // planning layers while keeping an isolated RootPath.
        services.AddSingleton(new WorkspaceOptions
        {
            RootPath = Path.Combine(Path.GetTempPath(), "studio-recursive-tests", Guid.NewGuid().ToString("N")),
            MaxPlanDepth = 2,
        });
    }

    [Fact]
    public async Task Compound_slice_reconciles_bottom_up_through_interior_node_to_root()
    {
        var fakeHandler = new RecursivePlanningLlmHandler();

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: ConfigureRecursive);

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var decisionLog     = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();
        var fileWorkspace   = app.Services.GetRequiredService<IFileWorkspaceService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var root = await orchestratorSvc.CreateWorkUnitAsync(
                goal: RecursivePlanningLlmHandler.RootGoal, owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator", workUnitId: root.WorkUnitId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key");

            // 1. The compound slice c1 is routed to a sub-PLANNER (SpawnPlanner on c1's own id), not a worker.
            WorkUnit? c1 = null;
            var c1Deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < c1Deadline)
            {
                var kids = await workUnits.GetChildrenAsync(root.WorkUnitId);
                c1 = kids.FirstOrDefault(k => k.FanOutInfo?.SliceId == "c1");
                if (c1 is not null &&
                    (await decisionLog.GetEventsAsync(c1.WorkUnitId)).Any(e => e.Action == OrchestrationAction.SpawnPlanner))
                    break;
                await Task.Delay(100);
            }
            Assert.NotNull(c1);
            var c1PlannerEvents = await decisionLog.GetEventsAsync(c1!.WorkUnitId);
            Assert.Contains(c1PlannerEvents, e => e.Action == OrchestrationAction.SpawnPlanner);

            // 2. c1's grandchildren appear as WORKERS at the depth cap; g2 (marked compound) is demoted.
            IReadOnlyList<WorkUnit> grandkids = [];
            var gkDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < gkDeadline)
            {
                grandkids = await workUnits.GetChildrenAsync(c1.WorkUnitId);
                if (grandkids.Count >= 2) break;
                await Task.Delay(100);
            }
            Assert.Equal(2, grandkids.Count);
            // g2 was Compound but depth-2 == MaxPlanDepth, so it must be a worker, not a sub-planner:
            // c1's decision log records the demotion on the Enqueue event.
            var g2 = grandkids.Single(g => g.FanOutInfo?.SliceId == "g2");
            var demotionDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            var sawDemotion = false;
            while (DateTimeOffset.UtcNow < demotionDeadline && !sawDemotion)
            {
                sawDemotion = (await decisionLog.GetEventsAsync(c1.WorkUnitId)).Any(e =>
                    e.Action == OrchestrationAction.Enqueue &&
                    e.SpawnedIds.Contains(g2.WorkUnitId) &&
                    e.InputProjectionSnapshot.Contains("\"demotedFromCompound\":true", StringComparison.Ordinal));
                if (!sawDemotion) await Task.Delay(100);
            }
            Assert.True(sawDemotion, "Expected c1's fan-out to log demotedFromCompound=true for the compound grandchild g2.");
            // And g2 must NOT itself have been spawned as a planner (it was forced to a worker).
            Assert.DoesNotContain(
                await decisionLog.GetEventsAsync(g2.WorkUnitId),
                e => e.Action == OrchestrationAction.SpawnPlanner);

            // 3. Drive the tree bottom-up, watching for the root's reconciled proposal. A leaf under an
            //    interior node (g1/g2 under c1) is only APPROVED, so its interior parent aggregates it
            //    into a reconciled proposal. An interior node's reconciled proposal — and a leaf that is
            //    a direct child of root (l1) — must be APPLIED (→ Merged) so the "all children terminal"
            //    cascade reaches the next level up. (Approval alone leaves an interior node Executing,
            //    which is why root never reconciled otherwise.)
            var handled = new HashSet<string>();
            MergeProposal? rootReconciled = null;
            var driveDeadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (DateTimeOffset.UtcNow < driveDeadline)
            {
                var proposals = await merge.ListAsync();
                rootReconciled = proposals.FirstOrDefault(p =>
                    p.WorkUnitId == root.WorkUnitId &&
                    (p.ReconciledFrom?.Count ?? 0) >= 1 &&
                    p.Status == MergeProposalStatus.ReadyForReview);
                if (rootReconciled is not null) break;

                var rootChildIds = (await workUnits.GetChildrenAsync(root.WorkUnitId))
                    .Select(c => c.WorkUnitId).ToHashSet();

                // Include Approved (not just ReadyForReview) so a proposal whose apply hit a transient
                // race last iteration is retried, and tolerate races (concurrent reconciliation can
                // supersede/re-apply underneath us) — only mark handled once the apply actually lands.
                foreach (var p in proposals.Where(p =>
                    p.WorkUnitId != root.WorkUnitId &&
                    p.Status is MergeProposalStatus.ReadyForReview or MergeProposalStatus.Approved &&
                    !handled.Contains(p.ProposalId)))
                {
                    try
                    {
                        var current = p;
                        if (current.Status == MergeProposalStatus.ReadyForReview)
                            current = await merge.ReviewAsync(current.ProposalId, MergeProposalStatus.Approved);
                        var isInteriorReconciled = (current.ReconciledFrom?.Count ?? 0) > 0;
                        var isRootLeaf = current.WorkUnitId is not null && rootChildIds.Contains(current.WorkUnitId);
                        if (isInteriorReconciled || isRootLeaf)
                            await merge.ApplyAsync(current.ProposalId);
                        handled.Add(current.ProposalId);
                    }
                    catch { /* transient race under concurrent reconciliation — re-observe next iteration */ }
                }
                await Task.Delay(150);
            }

            Assert.True(rootReconciled is not null,
                "Root never produced a reconciled proposal. " + await DumpStateAsync(workUnits, merge, root.WorkUnitId));

            // The interior-node roll-up: the reconciled root branch must actually CONTAIN both
            // grandchildren's files (Alpha.cs + Bravo.cs), proving g1/g2 folded up through c1 into root
            // — not just c1's own (empty) direct changes. Read the content from the reconciled proposal's
            // source branch (the aggregated merge branch).
            var mergeBranch = rootReconciled!.SourceBranch;
            var alpha = await fileWorkspace.ReadAsync(mergeBranch, "src/Alpha.cs");
            var bravo = await fileWorkspace.ReadAsync(mergeBranch, "src/Bravo.cs");
            Assert.True(alpha is not null && bravo is not null,
                $"Root reconciled branch '{mergeBranch}' missing grandchildren files (Alpha={alpha is not null}, Bravo={bravo is not null}). " +
                $"reconciledFrom={rootReconciled.ReconciledFrom?.Count}, filesTouched=[{string.Join(",", rootReconciled.FilesTouched ?? [])}]. " +
                await DumpStateAsync(workUnits, merge, root.WorkUnitId));

            // 4. The goal completes with no stranded interior node, and exactly two planners ran
            //    (root + c1) — no duplicate sub-planners despite repeated convergence sweeps.
            var rootApproved = await merge.ReviewAsync(rootReconciled.ProposalId, MergeProposalStatus.Approved);
            await merge.ApplyAsync(rootApproved.ProposalId);

            var completeDeadline = DateTimeOffset.UtcNow.AddSeconds(30);
            WorkUnit? rootFinal = null;
            while (DateTimeOffset.UtcNow < completeDeadline)
            {
                rootFinal = await workUnits.GetAsync(root.WorkUnitId);
                if (rootFinal?.Status is WorkUnitStatus.Completed or WorkUnitStatus.Merged) break;
                await Task.Delay(100);
            }
            Assert.True(rootFinal?.Status is WorkUnitStatus.Completed or WorkUnitStatus.Merged,
                $"Root did not complete (status {rootFinal?.Status}). " + await DumpStateAsync(workUnits, merge, root.WorkUnitId));

            var c1PlannerCount = (await decisionLog.GetEventsAsync(c1.WorkUnitId))
                .Count(e => e.Action == OrchestrationAction.SpawnPlanner);
            Assert.Equal(1, c1PlannerCount);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Compound_child_inherits_root_review_wiring()
    {
        var fakeHandler = new RecursivePlanningLlmHandler();

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: ConfigureRecursive);

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var decisionLog     = app.Services.GetRequiredService<IOrchestrationDecisionLogService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var root = await orchestratorSvc.CreateWorkUnitAsync(
                goal: RecursivePlanningLlmHandler.RootGoal, owner: "integration-test");

            // Spawn WITH a reviewer profile — the review wiring a real goal carries.
            await agentControl.SpawnAsync(
                agentType: "orchestrator", workUnitId: root.WorkUnitId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key",
                autoReviewProfileId: "reviewer");

            // Wait for the compound child c1 to be spawned as a sub-planner.
            WorkUnit? c1 = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
            while (DateTimeOffset.UtcNow < deadline)
            {
                c1 = (await workUnits.GetChildrenAsync(root.WorkUnitId)).FirstOrDefault(k => k.FanOutInfo?.SliceId == "c1");
                if (c1 is not null &&
                    (await decisionLog.GetEventsAsync(c1.WorkUnitId)).Any(e => e.Action == OrchestrationAction.SpawnPlanner))
                    break;
                await Task.Delay(100);
            }
            Assert.NotNull(c1);

            // The fix: GetAutoReviewProfileId is a per-workUnitId lookup with no walk to root, so the
            // compound child's own registration must carry the root's reviewer profile — otherwise its
            // subtree's automated review resolves null and silently falls back to human review.
            Assert.Equal("reviewer", agentControl.GetAutoReviewProfileId(c1!.WorkUnitId));
            Assert.Equal("reviewer", agentControl.GetAutoReviewProfileId(root.WorkUnitId));
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Nonconformant_consumer_is_rejected_by_review()
    {
        var reconciled = await RunPeerContractAsync(nonConformantConsumer: true);
        Assert.NotNull(reconciled);
        Assert.Equal(MergeProposalStatus.Rejected, reconciled!.Status);
        Assert.Contains("email", reconciled.VerificationResults ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Conformant_peer_pair_passes_review()
    {
        var reconciled = await RunPeerContractAsync(nonConformantConsumer: false);
        Assert.NotNull(reconciled);
        Assert.NotEqual(MergeProposalStatus.Rejected, reconciled!.Status);
        Assert.Contains("conform", reconciled.VerificationResults ?? "", StringComparison.OrdinalIgnoreCase);
    }

    // S5.b — a producer (provides c-user) and a peer consumer (consumes c-user) over DISJOINT fileScopes,
    // reviewed by an automated reviewer. The parent-authored contract reaches the reviewer only because
    // S6 plumbs it into the reviewer kickoff (Case B of BuildContractContextAsync) — so a Rejected
    // verdict here also proves the contract was injected. Runs at the default MaxPlanDepth=1: this gap
    // exists at flat depth, no recursion required. Returns the reviewed reconciled proposal.
    private static async Task<MergeProposal?> RunPeerContractAsync(bool nonConformantConsumer)
    {
        var fakeHandler = new RecursivePlanningLlmHandler(peerContractMode: true, nonConformantConsumer: nonConformantConsumer);

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var root = await orchestratorSvc.CreateWorkUnitAsync(
                goal: RecursivePlanningLlmHandler.RootGoal, owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator", workUnitId: root.WorkUnitId,
                model: "fake-model", baseUrl: "http://fake-llm", apiKey: "fake-key",
                autoReviewProfileId: "reviewer");

            var approvedChildren = new HashSet<string>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
            while (DateTimeOffset.UtcNow < deadline)
            {
                // Approve the producer/consumer worker proposals so the parent reconciles them into the
                // proposal the automated reviewer then gates.
                var childIds = (await workUnits.GetChildrenAsync(root.WorkUnitId)).Select(c => c.WorkUnitId).ToHashSet();
                foreach (var p in (await merge.ListAsync()).Where(p =>
                    p.Status == MergeProposalStatus.ReadyForReview &&
                    p.WorkUnitId is not null && childIds.Contains(p.WorkUnitId) &&
                    !approvedChildren.Contains(p.ProposalId)))
                {
                    try
                    {
                        await merge.ReviewAsync(p.ProposalId, MergeProposalStatus.Approved);
                        approvedChildren.Add(p.ProposalId);
                    }
                    catch { /* transient race — re-observe next iteration */ }
                }

                var reviewed = (await merge.ListAsync()).FirstOrDefault(p =>
                    p.WorkUnitId == root.WorkUnitId &&
                    (p.ReconciledFrom?.Count ?? 0) >= 2 &&
                    !string.IsNullOrEmpty(p.VerificationResults));
                if (reviewed is not null) return reviewed;
                await Task.Delay(100);
            }
            return null;
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<string> DumpStateAsync(IWorkUnitService workUnits, IMergeService merge, string rootId)
    {
        var lines = new List<string> { "State dump:" };
        async Task WalkAsync(string id, int depth)
        {
            var wu = await workUnits.GetAsync(id);
            if (wu is null) return;
            lines.Add($"{new string(' ', depth * 2)}- {wu.FanOutInfo?.SliceId ?? "(root)"} [{id[..8]}] status={wu.Status}");
            foreach (var child in await workUnits.GetChildrenAsync(id))
                await WalkAsync(child.WorkUnitId, depth + 1);
        }
        await WalkAsync(rootId, 0);
        lines.Add("Proposals:");
        foreach (var p in await merge.ListAsync())
            lines.Add($"  - wu={p.WorkUnitId?[..Math.Min(8, p.WorkUnitId?.Length ?? 0)]} status={p.Status} reconciledFrom={p.ReconciledFrom?.Count ?? 0} files=[{string.Join(",", p.FilesTouched ?? [])}]");
        return string.Join("\n", lines);
    }
}
