using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Drives ReviewPolicy.AgentApproval/Hybrid through the real OrchestratorAgentLoop →
/// WorkerAgentLoop → ReviewerAgentLoop chain (via AutonomousReviewLlmHandler, no real LLM) to
/// prove the "leave for lunch" autonomy promise end-to-end through real DI-wired services —
/// AutoReviewRuleTests/ReviewTimerServiceTests already cover the same logic in isolation with
/// hand-written fakes; this proves the real services agree with those fakes.
/// </summary>
[Trait("Category", "Integration")]
public class AutonomousReviewTests
{
    [Fact]
    public async Task AgentApproval_reviewer_approves_autoMerges_with_no_human_action()
    {
        var fakeHandler = new AutonomousReviewLlmHandler("Approved", "Looks good.");
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Build a hello world feature",
            owner: "integration-test",
            reviewPolicy: ReviewPolicy.AgentApproval);

        await agentControl.SpawnAsync(
            agentType: "orchestrator",
            workUnitId: wu.WorkUnitId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var proposal = await PollForStatusAsync(merge, MergeProposalStatus.Merged);

        Assert.NotNull(proposal);
        Assert.True(proposal!.AutoApplied);
    }

    [Fact]
    public async Task AgentApproval_reviewer_rejects_blocks_without_autoMerge()
    {
        var fakeHandler = new AutonomousReviewLlmHandler("Rejected", "Missing tests.");
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Build a hello world feature",
            owner: "integration-test",
            reviewPolicy: ReviewPolicy.AgentApproval);

        await agentControl.SpawnAsync(
            agentType: "orchestrator",
            workUnitId: wu.WorkUnitId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var proposal = await PollForStatusAsync(merge, MergeProposalStatus.Rejected);

        Assert.NotNull(proposal);
        Assert.False(string.IsNullOrEmpty(proposal!.VerificationResults));
        Assert.DoesNotContain(await merge.ListAsync(), p => p.Status == MergeProposalStatus.Merged);
    }

    [Fact]
    public async Task Hybrid_reviewer_approves_schedules_timer_then_expiry_autoApplies()
    {
        var fakeHandler = new AutonomousReviewLlmHandler("Approved", "Looks good.");
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var reviewTimers    = app.Services.GetRequiredService<IReviewTimerService>();
        var nodeStore       = app.Services.GetRequiredService<IStudioNodeStore>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Build a hello world feature",
            owner: "integration-test",
            reviewPolicy: ReviewPolicy.Hybrid);

        await agentControl.SpawnAsync(
            agentType: "orchestrator",
            workUnitId: wu.WorkUnitId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        // Reviewer approves → AutoReviewRule schedules a 5-minute Hybrid timer and blocks the
        // immediate apply. Poll for the proposal to reach Approved (set by the real
        // ReviewerAgentLoop's merge.review call) before looking for the timer it triggers.
        var approved = await PollForStatusAsync(merge, MergeProposalStatus.Approved);
        Assert.NotNull(approved);

        ReviewTimer? timer = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            timer = await reviewTimers.GetAsync(approved!.ProposalId);
            if (timer is { Cancelled: false }) break;
            await Task.Delay(50);
        }
        Assert.NotNull(timer);
        Assert.False(timer!.Cancelled);

        // The real HybridTimeout is a hardcoded 5 minutes (AutoReviewRule.cs) — force-expire the
        // timer rather than waiting for it, then drive the real ProcessExpiredAsync poll loop.
        // Everything up to here (reviewer approval, timer scheduling) ran through the real,
        // scripted agent loop; only the wall clock is short-circuited.
        await nodeStore.WriteNodeAsync(
            StudioNodeKind.ReviewTimerV1,
            approved!.ProposalId,
            JsonSerializer.Serialize(timer! with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) }));

        await reviewTimers.ProcessExpiredAsync();

        var merged = await merge.GetAsync(approved.ProposalId);
        Assert.Equal(MergeProposalStatus.Merged, merged?.Status);
        Assert.True(merged!.AutoApplied);
    }

    [Fact]
    public async Task Hybrid_human_applies_before_expiry_cancels_timer()
    {
        var fakeHandler = new AutonomousReviewLlmHandler("Approved", "Looks good.");
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var mergeCommands   = app.Services.GetRequiredService<IMergeCommandService>();
        var reviewTimers    = app.Services.GetRequiredService<IReviewTimerService>();

        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Build a hello world feature",
            owner: "integration-test",
            reviewPolicy: ReviewPolicy.Hybrid);

        await agentControl.SpawnAsync(
            agentType: "orchestrator",
            workUnitId: wu.WorkUnitId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        var approved = await PollForStatusAsync(merge, MergeProposalStatus.Approved);
        Assert.NotNull(approved);

        ReviewTimer? timer = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            timer = await reviewTimers.GetAsync(approved!.ProposalId);
            if (timer is { Cancelled: false }) break;
            await Task.Delay(50);
        }
        Assert.NotNull(timer);

        // Human clicks "Apply Now" before the timer expires — direct human-initiated apply,
        // autoApplied defaults to false.
        var merged = await mergeCommands.ApplyAsync(approved!.ProposalId);

        Assert.Equal(MergeProposalStatus.Merged, merged.Status);
        Assert.False(merged.AutoApplied);

        var cancelledTimer = await reviewTimers.GetAsync(approved.ProposalId);
        Assert.True(cancelledTimer!.Cancelled);

        // A second expiry sweep must not re-apply anything (the timer is cancelled, and the
        // proposal is no longer in a state ApplyAsync can transition from Merged).
        await reviewTimers.ProcessExpiredAsync();
        var stillMerged = await merge.GetAsync(approved.ProposalId);
        Assert.Equal(MergeProposalStatus.Merged, stillMerged?.Status);
    }

    private static async Task<MergeProposal?> PollForStatusAsync(
        IMergeService merge, MergeProposalStatus status, int timeoutSeconds = 30)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var all = await merge.ListAsync();
            var match = all.FirstOrDefault(p => p.Status == status);
            if (match is not null) return match;
            await Task.Delay(50);
        }
        return null;
    }
}
