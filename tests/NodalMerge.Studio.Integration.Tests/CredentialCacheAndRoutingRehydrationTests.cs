using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.AgentRuntime;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

/// <summary>
/// Covers the fix for: after a Host restart, resumed goals silently fell back to human review
/// because AutoReviewProfileId lived only in InMemoryAgentRuntimeService's in-memory
/// _goalCredentialRegistrations, and the "obvious" fix of persisting that record wholesale would
/// have written a raw ApiKey to disk. GoalRoutingConfig (the safe subset) now survives a
/// restart on its own; ApiKey never touches IStudioNodeStore at all — see
/// IRuntimeCredentialCache's doc comment.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sqlite")]
public class CredentialCacheAndRoutingRehydrationTests : IDisposable
{
    private readonly string _tempRoot =
        Path.Combine(Path.GetTempPath(), $"studio-cred-rehydrate-{Guid.NewGuid():N}");

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    private Microsoft.AspNetCore.Builder.WebApplication BuildApp()
    {
        var sqliteDbPath = Path.Combine(_tempRoot, "nodes.db");
        var blobsRootPath = Path.Combine(_tempRoot, "blobs");
        var workspaceRootPath = Path.Combine(_tempRoot, "workspace");

        return StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(new ImmediateEndTurnLlmHandler()),
            configureConfiguration: cfg => cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["NodalMerge:Storage:Sqlite:DbPath"] = sqliteDbPath,
                ["NodalMerge:Storage:FileBlobs:RootPath"] = blobsRootPath,
                ["Workspace:RootPath"] = workspaceRootPath,
            }));
    }

    [Fact]
    public async Task GetAutoReviewProfileId_survives_a_restart_with_zero_credential_resupply()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var agentControl1 = app1.Services.GetRequiredService<IAgentControlService>();

        var unit = await orchestrator1.CreateWorkUnitAsync("Build the thing", "test");
        var agentId1 = await agentControl1.SpawnAsync(
            "orchestrator", unit.WorkUnitId,
            model: "fake-model", baseUrl: "http://fake-llm", apiKey: "sk-super-secret-123",
            autoReviewProfileId: "reviewer");

        // Sanity: the live (same-process) registration already has it.
        Assert.Equal("reviewer", agentControl1.GetAutoReviewProfileId(unit.WorkUnitId));

        // SpawnAsync above started a real (fire-and-forget) orchestrator loop against the fake LLM
        // handler — it writes an OrchestrationDecisionLog entry to SQLite on completion. Stop it and
        // give it a moment to actually unwind before disposing app1, or that in-flight SQLite write
        // can still be holding the db file open when Dispose() tries to delete _tempRoot, throwing
        // an intermittent IOException ("file is being used by another process") — StopAsync only
        // requests cancellation, it doesn't join the loop's background Task.
        await agentControl1.StopAsync(agentId1);
        await Task.Delay(200);
        await app1.DisposeAsync();

        var app2 = BuildApp();
        // Mirrors production startup: InMemoryAgentRuntimeService.StartAsync rehydrates
        // GoalRoutingConfig before anything else runs.
        var agentRuntime2 = app2.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        await agentRuntime2.StartAsync(CancellationToken.None);
        try
        {
            var agentControl2 = app2.Services.GetRequiredService<IAgentControlService>();

            // This is the exact regression this fix targets — before it, this returned null and
            // AutomatedReviewGateService.TryEnqueueReviewerAsync silently fell back to human review.
            Assert.Equal("reviewer", agentControl2.GetAutoReviewProfileId(unit.WorkUnitId));
        }
        finally
        {
            // StartAsync's background scheduler poll loop otherwise keeps SQLite handles open past
            // this test's end, and Dispose()'s Directory.Delete of the temp db fails with them
            // still locked.
            await agentRuntime2.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Routing_persisted_under_the_legacy_orchestrator_kind_still_rehydrates()
    {
        // plans/orchestrator-pure-service.md M1 — goals in flight across the GoalRoutingV1 rename
        // have their routing stored under the legacy OrchestratorRoutingV1 kind. Rehydration must
        // read both (writes only ever use the new kind now).
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var nodeStore1 = app1.Services.GetRequiredService<IStudioNodeStore>();

        var unit = await orchestrator1.CreateWorkUnitAsync("Build the thing", "test");
        var legacyRouting = new GoalRoutingConfig(
            unit.WorkUnitId, "anthropic", "fake-model", "http://fake-llm",
            ProfileId: "orchestrator", AutoReviewProfileId: "reviewer", CredentialRef: null);
        await nodeStore1.WriteNodeAsync(
            StudioNodeKind.OrchestratorRoutingV1, unit.WorkUnitId,
            System.Text.Json.JsonSerializer.Serialize(legacyRouting));
        await app1.DisposeAsync();

        var app2 = BuildApp();
        var agentRuntime2 = app2.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        await agentRuntime2.StartAsync(CancellationToken.None);
        try
        {
            var agentControl2 = app2.Services.GetRequiredService<IAgentControlService>();
            Assert.Equal("reviewer", agentControl2.GetAutoReviewProfileId(unit.WorkUnitId));
            Assert.Equal("orchestrator", agentControl2.GetGoalDefaultProfileId(unit.WorkUnitId));
        }
        finally
        {
            await agentRuntime2.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Orchestrator_credentials_are_null_after_restart_until_something_resupplies_them()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var agentControl1 = app1.Services.GetRequiredService<IAgentControlService>();
        var nodeStore1 = app1.Services.GetRequiredService<IStudioNodeStore>();

        var unit = await orchestrator1.CreateWorkUnitAsync("Build the thing", "test");
        var agentId1 = await agentControl1.SpawnAsync(
            "orchestrator", unit.WorkUnitId,
            model: "fake-model", baseUrl: "http://fake-llm", apiKey: "sk-super-secret-123",
            autoReviewProfileId: "reviewer");

        // The persisted GoalRoutingConfig record must never contain the raw key.
        var routingRecords = await nodeStore1.ReadAllNodesAsync(StudioNodeKind.GoalRoutingV1);
        Assert.Contains(routingRecords, r => r.EntityId == unit.WorkUnitId);
        Assert.All(routingRecords, r => Assert.DoesNotContain("sk-super-secret-123", r.PayloadJson));

        // SpawnAsync above started a real (fire-and-forget) orchestrator loop against the fake LLM
        // handler — see the matching comment in GetAutoReviewProfileId_survives_a_restart_with_zero_
        // credential_resupply above for why this must be stopped and given a moment to unwind before
        // disposing app1 (an in-flight SQLite write can otherwise still hold nodes.db open when
        // Dispose() tries to delete _tempRoot).
        await agentControl1.StopAsync(agentId1);
        await Task.Delay(200);
        await app1.DisposeAsync();

        var app2 = BuildApp();
        var agentRuntime2 = app2.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        await agentRuntime2.StartAsync(CancellationToken.None);
        try
        {
            var agentControl2 = app2.Services.GetRequiredService<IAgentControlService>();

            // Nobody has resupplied credentials in this fresh process — the cache is cold, so this
            // must come back null rather than an empty/garbage credential.
            Assert.Null(agentControl2.GetGoalDefaultCredentials(unit.WorkUnitId));
        }
        finally
        {
            await agentRuntime2.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReinvokeOrchestratorAsync_is_a_silent_noop_after_a_restart_with_no_credential_resupply()
    {
        // This is the live bug: WorkSchedulerService.ReleaseAsync calls ReinvokeOrchestratorAsync
        // automatically whenever a child work unit finishes, so an orchestrator whose registration
        // didn't survive a restart could never be woken up again — every child could finish and
        // the orchestrator (and the whole goal) would just sit idle forever. Confirms the no-op
        // happens (no agent starts) rather than throwing, since ReinvokeOrchestratorAsync's
        // contract is "no-op if nothing registered/resolvable," not "throw."
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var agentControl1 = app1.Services.GetRequiredService<IAgentControlService>();

        var unit = await orchestrator1.CreateWorkUnitAsync("Build the thing", "test");
        var agentId1 = await agentControl1.SpawnAsync(
            "orchestrator", unit.WorkUnitId,
            model: "fake-model", baseUrl: "http://fake-llm", apiKey: "sk-super-secret-123");

        // See the matching comment in GetAutoReviewProfileId_survives_a_restart_with_zero_
        // credential_resupply above — SpawnAsync started a real fire-and-forget orchestrator loop
        // that must be stopped and given a moment to unwind before disposing app1.
        await agentControl1.StopAsync(agentId1);
        await Task.Delay(200);
        await app1.DisposeAsync();

        var app2 = BuildApp();
        var agentRuntime2 = app2.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        await agentRuntime2.StartAsync(CancellationToken.None);
        try
        {
            var agentControl2 = app2.Services.GetRequiredService<IAgentControlService>();

            await agentControl2.ReinvokeOrchestratorAsync(unit.WorkUnitId);

            var active = await agentControl2.ListActiveAsync();
            Assert.DoesNotContain(active, a => a.WorkUnitId == unit.WorkUnitId);
        }
        finally
        {
            await agentRuntime2.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ReinvokeOrchestratorAsync_starts_a_fresh_loop_after_restart_when_credentials_are_resupplied()
    {
        var app1 = BuildApp();
        var orchestrator1 = app1.Services.GetRequiredService<IOrchestratorService>();
        var agentControl1 = app1.Services.GetRequiredService<IAgentControlService>();

        var unit = await orchestrator1.CreateWorkUnitAsync("Build the thing", "test");
        var agentId1 = await agentControl1.SpawnAsync(
            "orchestrator", unit.WorkUnitId,
            model: "fake-model", baseUrl: "http://fake-llm", apiKey: "sk-super-secret-123",
            autoReviewProfileId: "reviewer");

        // See the matching comment in GetAutoReviewProfileId_survives_a_restart_with_zero_
        // credential_resupply above — SpawnAsync started a real fire-and-forget orchestrator loop
        // that must be stopped and given a moment to unwind before disposing app1.
        await agentControl1.StopAsync(agentId1);
        await Task.Delay(200);
        await app1.DisposeAsync();

        var app2 = BuildApp();
        var agentRuntime2 = app2.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        await agentRuntime2.StartAsync(CancellationToken.None);
        try
        {
            var agentControl2 = app2.Services.GetRequiredService<IAgentControlService>();

            // A human/the extension resupplies credentials — mirrors the goal workspace's manual
            // "Reinvoke Orchestrator" action.
            await agentControl2.ReinvokeOrchestratorAsync(
                unit.WorkUnitId, overrideApiKey: "sk-resupplied-after-restart", overrideModel: "fake-model",
                overrideBaseUrl: "http://fake-llm");

            var active = await agentControl2.ListActiveAsync();
            var startedAgent = active.SingleOrDefault(a => a.WorkUnitId == unit.WorkUnitId);
            Assert.NotNull(startedAgent);

            // Routing (AutoReviewProfileId etc.) is still correct — resupply only touched credentials.
            Assert.Equal("reviewer", agentControl2.GetAutoReviewProfileId(unit.WorkUnitId));

            // The resupply actually started a real (fire-and-forget) orchestrator loop against the
            // fake LLM handler — explicitly stop it (not just the scheduler poller below) before
            // disposing, or its own in-flight SQLite/HTTP activity can outlive this test and keep
            // the temp db file locked past Dispose(), breaking sibling tests that run afterward.
            // StopAsync only requests cancellation (Cts.Cancel()) — it doesn't join the loop's
            // background Task, so give it a brief moment to actually unwind before disposing.
            await agentControl2.StopAsync(startedAgent!.AgentId);
            await Task.Delay(200);
        }
        finally
        {
            await agentRuntime2.StopAsync(CancellationToken.None);
            await app2.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReinvokeOrchestratorAsync_recovers_a_work_unit_with_no_registration_or_routing_at_all_when_fully_resupplied()
    {
        // Covers a work unit whose orchestrator predates GoalRoutingConfig persistence
        // entirely (spawned before that fix shipped) — there was never anywhere to persist a
        // profile/routing to, so unlike the "cold cache, warm routing" case above, there's nothing
        // on record at all. A manual recovery action supplying everything itself (model/baseUrl/
        // apiKey/provider/profileId) must still be able to start a fresh orchestrator loop.
        var app = BuildApp();
        var orchestrator = app.Services.GetRequiredService<IOrchestratorService>();
        var agentRuntime = app.Services.GetRequiredService<InMemoryAgentRuntimeService>();
        await agentRuntime.StartAsync(CancellationToken.None);
        try
        {
            var agentControl = app.Services.GetRequiredService<IAgentControlService>();

            // Created directly, never spawned as an orchestrator at all — nothing in either
            // _goalCredentialRegistrations or the rehydrated routing config for this work unit.
            var unit = await orchestrator.CreateWorkUnitAsync("Orphaned goal", "test");
            Assert.Null(agentControl.GetGoalDefaultProfileId(unit.WorkUnitId));

            await agentControl.ReinvokeOrchestratorAsync(
                unit.WorkUnitId,
                overrideModel: "fake-model", overrideBaseUrl: "http://fake-llm",
                overrideApiKey: "sk-fresh-recovery-key", overrideProvider: "anthropic",
                overrideProfileId: "worker");

            var active = await agentControl.ListActiveAsync();
            var startedAgent = active.SingleOrDefault(a => a.WorkUnitId == unit.WorkUnitId);
            Assert.NotNull(startedAgent);

            // The recovery should also persist routing going forward, so a *future* restart
            // doesn't hit this same gap again.
            Assert.Equal("worker", agentControl.GetGoalDefaultProfileId(unit.WorkUnitId));

            // ReinvokeOrchestratorAsync actually started a real (fire-and-forget) orchestrator loop
            // against the fake LLM handler — explicitly stop it before disposing, or its own
            // in-flight SQLite/HTTP activity can outlive this test and keep the temp db file locked
            // past Dispose(), breaking sibling tests in this class that run afterward. StopAsync
            // only requests cancellation — it doesn't join the loop's background Task, so give it a
            // brief moment to actually unwind before disposing.
            await agentControl.StopAsync(startedAgent!.AgentId);
            await Task.Delay(200);
        }
        finally
        {
            await agentRuntime.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }
}
