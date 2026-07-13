using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Requeue Goal (uncancel-and-requeue) — the direct analog of Unreject-and-Revise for a Cancelled
/// work unit instead of a Rejected proposal. WorkUnitCommandService.RequeueAsync splits on whether
/// the cancelled member has children: a leaf re-opens its tasks and re-enqueues a worker (mirrors
/// AutomatedReviewGateService's retry loop); a fan-out parent re-attempts reconciliation via
/// IMergeReconciliationService, which was already idempotent and status-agnostic before this
/// feature existed.
/// </summary>
[Trait("Category", "Integration")]
public class RequeueGoalTests
{
    [Fact]
    public async Task RequeueAsync_reopens_a_cancelled_leaf_work_unit_and_re_enqueues_a_worker()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();
        var scheduler = app.Services.GetRequiredService<IWorkScheduler>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        await workUnitCommands.CancelAsync(unit.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Cancelled, (await workUnits.GetAsync(unit.WorkUnitId))!.Status);

        var requeued = await workUnitCommands.RequeueAsync(unit.WorkUnitId);

        var single = Assert.Single(requeued);
        Assert.Equal(unit.WorkUnitId, single.WorkUnitId);
        Assert.Equal(WorkUnitStatus.Queued, single.Status);
        Assert.Equal(WorkUnitStatus.Queued, (await workUnits.GetAsync(unit.WorkUnitId))!.Status);

        var pending = await scheduler.ListPendingAsync();
        Assert.Contains(pending, i => i.WorkUnitId == unit.WorkUnitId);
    }

    [Fact]
    public async Task RequeueAsync_on_a_cancelled_fan_out_parent_requeues_its_cancelled_child_and_re_attempts_reconciliation()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var workUnits = app.Services.GetRequiredService<IWorkUnitService>();

        var parent = await orchestrator.CreateWorkUnitAsync("Parent goal", "test");
        var child = await orchestrator.CreateWorkUnitAsync(
            "Child slice", "test", parentWorkUnitId: parent.WorkUnitId);

        // Cancelling the parent cascades to its still-in-flight child (WorkUnitCommandService.
        // CancelAsync's subtree walk) — mirrors the live scenario of cancelling a goal mid fan-out.
        var cancelled = await workUnitCommands.CancelAsync(parent.WorkUnitId);
        Assert.Equal(2, cancelled.Count);

        var requeued = await workUnitCommands.RequeueAsync(parent.WorkUnitId);

        Assert.Equal(2, requeued.Count);
        // Leaf (child, no children of its own) goes back to Queued for a fresh worker attempt.
        Assert.Equal(WorkUnitStatus.Queued, (await workUnits.GetAsync(child.WorkUnitId))!.Status);
        // Fan-out parent re-attempts reconciliation from Executing (mirrors the existing
        // Reviewing -> Executing "try convergence again" edge).
        Assert.Equal(WorkUnitStatus.Executing, (await workUnits.GetAsync(parent.WorkUnitId))!.Status);
    }

    [Fact]
    public async Task RequeueAsync_resupplies_credentials_so_GetGoalDefaultCredentials_resolves_again()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();
        var agentControl = app.Services.GetRequiredService<IAgentControlService>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");
        await workUnitCommands.CancelAsync(unit.WorkUnitId);

        // Simulates the exact scenario that motivated this — a cancel/requeue cycle spanning a
        // Host restart wipes IRuntimeCredentialCache, so nothing is resolvable without the
        // requester (the webview's Requeue button, resolving from settings.json + the saved
        // credential store) supplying overrides.
        Assert.Null(agentControl.GetGoalDefaultCredentials(unit.WorkUnitId));

        await workUnitCommands.RequeueAsync(
            unit.WorkUnitId,
            overrideModel: "claude-sonnet-5", overrideBaseUrl: "https://api.anthropic.com",
            overrideApiKey: "sk-test-key", overrideProvider: "anthropic");

        var creds = agentControl.GetGoalDefaultCredentials(unit.WorkUnitId);
        Assert.NotNull(creds);
        Assert.Equal("claude-sonnet-5", creds!.Model);
        Assert.Equal("sk-test-key", creds.ApiKey);
    }

    [Fact]
    public async Task RequeueAsync_throws_when_the_work_unit_is_not_Cancelled()
    {
        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureServices: services => services.AddInMemoryStorage());

        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var workUnitCommands = app.Services.GetRequiredService<IWorkUnitCommandService>();

        var unit = await orchestrator.CreateWorkUnitAsync("Do a thing", "test");

        await Assert.ThrowsAsync<InvalidOperationException>(() => workUnitCommands.RequeueAsync(unit.WorkUnitId));
    }
}
