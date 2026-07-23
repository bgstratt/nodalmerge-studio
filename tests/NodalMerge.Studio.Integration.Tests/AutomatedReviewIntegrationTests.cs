using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Phase 4 slice 11d — automated reviewer pre-gate before human merge review.
/// </summary>
[Trait("Category", "Integration")]
public class AutomatedReviewIntegrationTests
{
    // Reconciliation only folds a fanned-out child's proposal in once it's been Approved — these
    // children default to TaskReviewPolicy.HumanRequired, so simulate the human approval each one
    // needs before the parent's reconciled proposal (what AutomatedReviewGateService reviews) can
    // ever be produced. Safe to call repeatedly across polling iterations — already-approved
    // proposals are tracked so they're never re-reviewed.
    private static async Task ApproveReadyChildProposalsAsync(
        IMergeService merge, IWorkUnitService workUnits, string parentWorkUnitId, HashSet<string> alreadyApproved)
    {
        var children = await workUnits.GetChildrenAsync(parentWorkUnitId);
        var childIds = children.Select(c => c.WorkUnitId).ToHashSet();
        var ready = (await merge.ListAsync())
            .Where(p => p.Status == MergeProposalStatus.ReadyForReview
                && p.WorkUnitId is not null && childIds.Contains(p.WorkUnitId)
                && !alreadyApproved.Contains(p.ProposalId))
            .ToList();
        foreach (var p in ready)
        {
            await merge.ReviewAsync(p.ProposalId, MergeProposalStatus.Approved);
            alreadyApproved.Add(p.ProposalId);
        }
    }

    [Fact]
    public async Task AutoReview_approves_reconciled_proposal_before_human_gate()
    {
        var fakeHandler = new AutomatedReviewFanOutLlmHandler();

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
            var parent = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Build Foo and Bar features",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: parent.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key",
                autoReviewProfileId: "reviewer");

            MergeProposal? reviewed = null;
            var approvedChildren = new HashSet<string>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await ApproveReadyChildProposalsAsync(merge, workUnits, parent.WorkUnitId, approvedChildren);
                reviewed = (await merge.ListAsync()).FirstOrDefault(p =>
                    p.WorkUnitId == parent.WorkUnitId &&
                    p.ReconciledFrom.Count >= 2 &&
                    !string.IsNullOrEmpty(p.VerificationResults));
                if (reviewed is not null) break;
                await Task.Delay(100);
            }

            Assert.NotNull(reviewed);
            Assert.Equal(MergeProposalStatus.ReadyForReview, reviewed.Status);
            Assert.Contains("goal satisfied", reviewed.VerificationResults!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    // Repro + regression for the "AgentApproval children never auto-reviewed" report. With the child's
    // TaskReviewPolicy set to AgentApproval (not the default HumanRequired the other tests use), its own
    // proposal must inline-auto-review + auto-apply → Merged with NO manual accept/apply. The bug was
    // that the auto-apply fired at propose time on a Draft proposal — which ApplyAsync rejects before the
    // inline reviewer gate runs — so the child sat at ReadyForReview forever. Single child = no concurrent
    // inline reviews, so this is deterministic.
    // Two fixes make this green: (1) MergeCommandService validates Draft→ReadyForReview before the
    // auto-apply so the inline reviewer gate can run at all; (2) MergeReconciliationService no longer
    // folds a self-applying (AgentApproval/Hybrid + AutoApplyOnPropose) child at merely Approved — it
    // waits for Merged, so the eager child-approve-time reconcile can't Supersede the proposal out
    // from under the child's own in-flight auto-apply and strand it at Proposed.
    [Fact]
    public async Task AgentApproval_child_inline_auto_reviews_and_merges_without_manual_intervention()
    {
        var fakeHandler = new SingleChildAutoReviewLlmHandler();

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();

        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            // The single fan-out child inherits the parent's TaskReviewPolicy (FanOutService).
            var parent = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Build the Solo feature",
                owner: "integration-test",
                taskReviewPolicy: ReviewPolicy.AgentApproval);

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: parent.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key",
                autoReviewProfileId: "reviewer");

            // No manual ReviewAsync/ApplyAsync anywhere — the child must reach Merged on its own.
            WorkUnit? child = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                child = (await workUnits.GetChildrenAsync(parent.WorkUnitId)).FirstOrDefault(c => c.FanOutInfo?.SliceId == "only");
                if (child?.Status == WorkUnitStatus.Merged) break;
                await Task.Delay(150);
            }

            var props = await merge.ListAsync();
            var dls = await deadLetter.ListAsync();
            var dump = $"child={child?.Status} | proposals: " +
                string.Join(", ", props.Select(p => $"{p.Status}:vr={(p.VerificationResults is null ? "null" : "set")}")) +
                " | deadletters: " + string.Join(", ", dls.Select(d => $"{d.Stage}/{d.ProfileId}"));

            Assert.True(child?.Status == WorkUnitStatus.Merged, dump);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AutoReview_rejects_broken_proposal()
    {
        var fakeHandler = new AutomatedReviewFanOutLlmHandler(rejectReview: true);

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
            var parent = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Build Foo and Bar features",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: parent.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key",
                autoReviewProfileId: "reviewer");

            MergeProposal? rejected = null;
            var approvedChildren = new HashSet<string>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(45);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await ApproveReadyChildProposalsAsync(merge, workUnits, parent.WorkUnitId, approvedChildren);
                rejected = (await merge.ListAsync()).FirstOrDefault(p =>
                    p.WorkUnitId == parent.WorkUnitId &&
                    p.ReconciledFrom.Count >= 2 &&
                    p.Status == MergeProposalStatus.Rejected);
                if (rejected is not null) break;
                await Task.Delay(100);
            }

            Assert.NotNull(rejected);
            Assert.Contains("Missing.cs", rejected.VerificationResults!, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AutoReview_escalates_to_dead_letter_after_max_rejections()
    {
        var fakeHandler = new AutomatedReviewFanOutLlmHandler(rejectReview: true);

        await using var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var agentRuntime    = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        var workUnits       = app.Services.GetRequiredService<IWorkUnitService>();
        var deadLetter      = app.Services.GetRequiredService<IDeadLetterService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();

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
                apiKey: "fake-key",
                autoReviewProfileId: "reviewer");

            WorkUnit? deadLettered = null;
            DeadLetterEntry? entry = null;
            // Each rejection cycle re-queues the children and produces fresh proposals, so approval
            // has to keep running for the whole window, not just once up front.
            var approvedChildren = new HashSet<string>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(180);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await ApproveReadyChildProposalsAsync(merge, workUnits, parent.WorkUnitId, approvedChildren);

                deadLettered = await workUnits.GetAsync(parent.WorkUnitId);
                if (deadLettered?.Status == WorkUnitStatus.DeadLettered)
                {
                    entry = await deadLetter.GetLatestForWorkUnitAsync(parent.WorkUnitId);
                    if (entry is not null) break;
                }

                await Task.Delay(200);
            }

            Assert.NotNull(deadLettered);
            Assert.Equal(WorkUnitStatus.DeadLettered, deadLettered.Status);
            Assert.NotNull(entry);
            Assert.Equal(PipelineStage.Review, entry.Stage);
            Assert.Contains("Missing.cs", entry.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task AutoReview_disabled_skips_verification_results()
    {
        var fakeHandler = new FanOutLlmHandler();

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
            var parent = await orchestratorSvc.CreateWorkUnitAsync(
                goal: "Build Foo and Bar features",
                owner: "integration-test");

            await agentControl.SpawnAsync(
                agentType: "orchestrator",
                workUnitId: parent.WorkUnitId,
                model: "fake-model",
                baseUrl: "http://fake-llm",
                apiKey: "fake-key");

            MergeProposal? reconciled = null;
            var approvedChildren = new HashSet<string>();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
            while (DateTimeOffset.UtcNow < deadline)
            {
                await ApproveReadyChildProposalsAsync(merge, workUnits, parent.WorkUnitId, approvedChildren);
                reconciled = (await merge.ListAsync()).FirstOrDefault(p =>
                    p.WorkUnitId == parent.WorkUnitId &&
                    p.ReconciledFrom.Count >= 2 &&
                    p.Status == MergeProposalStatus.ReadyForReview);
                if (reconciled is not null) break;
                await Task.Delay(100);
            }

            Assert.NotNull(reconciled);
            Assert.True(string.IsNullOrEmpty(reconciled.VerificationResults));
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
        }
    }
}
