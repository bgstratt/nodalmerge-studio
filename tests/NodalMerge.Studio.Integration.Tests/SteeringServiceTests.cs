using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>Covers Phase 7.3 (Slices 23a/23b) — pause &amp; redirect and fork-from-node.</summary>
[Trait("Category", "Integration")]
public class SteeringServiceTests
{
    private static async Task<(
        ISteeringService Steering,
        IOrchestratorService Orchestrator,
        IWorkUnitService WorkUnits,
        IAgentControlService AgentControl,
        ISteeringDecisionService Decisions,
        IWorkScheduler Scheduler)> BuildAsync()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        return (
            app.Services.GetRequiredService<ISteeringService>(),
            app.Services.GetRequiredService<IOrchestratorService>(),
            app.Services.GetRequiredService<IWorkUnitService>(),
            app.Services.GetRequiredService<IAgentControlService>(),
            app.Services.GetRequiredService<ISteeringDecisionService>(),
            app.Services.GetRequiredService<IWorkScheduler>());
    }

    [Fact]
    public async Task PauseAndRedirect_creates_sibling_fork_with_injected_constraint()
    {
        var (steering, orchestrator, workUnits, agentControl, decisions, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Build the payment flow", "test");
        var agentId = await agentControl.SpawnAsync(
            "orchestrator", origin.WorkUnitId, model: "fake", baseUrl: "http://fake", apiKey: "fake");

        var result = await steering.PauseAndRedirectAsync(new SteeringCommand(
            origin.WorkUnitId, agentId, "Use Stripe instead of Braintree"));

        Assert.Equal(origin.WorkUnitId, result.OriginalWorkUnitId);
        Assert.NotEqual(origin.WorkUnitId, result.NewWorkUnitId);

        var fork = await workUnits.GetAsync(result.NewWorkUnitId);
        Assert.NotNull(fork);
        Assert.Equal(origin.ParentWorkUnitId, fork!.ParentWorkUnitId);
        Assert.Contains("[steered]", fork.Goal);

        var recorded = await decisions.ListByWorkUnitAsync(origin.WorkUnitId);
        var decision = Assert.Single(recorded);
        Assert.Equal(result.SteeringDecisionId, decision.SteeringDecisionId);
        Assert.Equal("Use Stripe instead of Braintree", decision.InjectedConstraint);
        Assert.Equal(result.NewWorkUnitId, decision.NewChildWorkUnitId);
    }

    [Fact]
    public async Task PauseAndRedirect_with_an_already_stopped_agent_still_forks()
    {
        // SteeringService swallows KeyNotFoundException from IAgentControlService.PauseAsync —
        // the agent may already be idle/gone, and that's not a reason to abandon the redirect.
        var (steering, orchestrator, _, _, _, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Build the payment flow", "test");

        var result = await steering.PauseAndRedirectAsync(new SteeringCommand(
            origin.WorkUnitId, "agent-does-not-exist", "Add idempotency keys"));

        Assert.Equal(origin.WorkUnitId, result.OriginalWorkUnitId);
        Assert.NotNull(result.NewWorkUnitId);
    }

    [Fact]
    public async Task PauseAndRedirect_with_profileId_enqueues_the_fork()
    {
        var (steering, orchestrator, _, _, _, scheduler) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Build the payment flow", "test");

        var result = await steering.PauseAndRedirectAsync(new SteeringCommand(
            origin.WorkUnitId, "agent-does-not-exist", "Add idempotency keys", ProfileId: "worker"));

        var pending = await scheduler.ListPendingAsync();
        Assert.Contains(pending, i => i.WorkUnitId == result.NewWorkUnitId);
    }

    [Fact]
    public async Task PauseAndRedirect_unknown_work_unit_throws()
    {
        var (steering, _, _, _, _, _) = await BuildAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            steering.PauseAndRedirectAsync(new SteeringCommand(
                "wu-does-not-exist", "agent-1", "constraint")));
    }

    [Fact]
    public async Task ForkFromNode_creates_a_fork_annotated_with_the_source_node()
    {
        var (steering, orchestrator, workUnits, _, decisions, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Build the payment flow", "test");

        var result = await steering.ForkFromNodeAsync(new ForkFromNodeCommand(
            origin.WorkUnitId, NodeId: "decision-42", NewGoal: "Try a queue-based approach"));

        var fork = await workUnits.GetAsync(result.NewWorkUnitId);
        Assert.NotNull(fork);
        Assert.Equal("Try a queue-based approach", fork!.Goal);
        Assert.Equal("decision-42", fork.Metadata?["forkedFromNodeId"]);

        var recorded = await decisions.ListByWorkUnitAsync(origin.WorkUnitId);
        Assert.Single(recorded);
    }

    [Fact]
    public async Task ForkFromNode_with_proposalId_sets_BranchedFromProposalId()
    {
        var (steering, orchestrator, workUnits, _, _, _) = await BuildAsync();
        var origin = await orchestrator.CreateWorkUnitAsync("Build the payment flow", "test");

        var result = await steering.ForkFromNodeAsync(new ForkFromNodeCommand(
            origin.WorkUnitId, ProposalId: "MP-1"));

        var fork = await workUnits.GetAsync(result.NewWorkUnitId);
        Assert.Equal("MP-1", fork!.BranchedFromProposalId);
    }

    [Fact]
    public async Task ForkFromNode_unknown_work_unit_throws()
    {
        var (steering, _, _, _, _, _) = await BuildAsync();

        await Assert.ThrowsAsync<KeyNotFoundException>(() =>
            steering.ForkFromNodeAsync(new ForkFromNodeCommand("wu-does-not-exist")));
    }
}
