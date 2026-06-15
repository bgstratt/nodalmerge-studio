using Microsoft.Extensions.DependencyInjection;
using NodalMerge.Studio.Contracts.Domain;
using StudioTaskStatus = NodalMerge.Studio.Contracts.Domain.TaskStatus;
using NodalMerge.Studio.Core.Services;
using NodalMerge.Studio.Host;
using NodalMerge.Studio.Storage;

namespace NodalMerge.Studio.Integration.Tests;

[Trait("Category", "Integration")]
public class FullAgentCycleTests
{
    /// <summary>
    /// Exercises the full agent loop end-to-end using a scripted fake LLM:
    ///   1. Seed a work unit.
    ///   2. Spawn an orchestrator agent (fake LLM creates a task, spawns a worker).
    ///   3. Worker marks the task Completed and raises a merge proposal.
    ///   4. Test acts as the human reviewer: Approve + Apply the merge.
    ///   5. Assert DAG nodes written and task status is Completed.
    /// </summary>
    [Fact]
    public async Task FullAgentCycle_ProducesAndApprovesMergeProposal()
    {
        // ── Arrange ──────────────────────────────────────────────────────────
        var fakeHandler = new ScriptedLlmHandler();

        var app = StudioWebApplication.Build(
            [],
            llmHttpClient: new HttpClient(fakeHandler),
            // Override DAG-backed storage with fully in-memory implementations so the
            // test doesn't require the NodalMerge runtime to be running.
            configureServices: services => services.AddInMemoryStorage());

        var orchestratorSvc = app.Services.GetRequiredService<IOrchestratorService>();
        var agentControl    = app.Services.GetRequiredService<IAgentControlService>();
        var merge           = app.Services.GetRequiredService<IMergeService>();
        var tasks           = app.Services.GetRequiredService<ITaskService>();
        var nodeStore       = app.Services.GetRequiredService<IStudioNodeStore>();

        // Seed: create a work unit (which auto-creates a branch).
        var wu = await orchestratorSvc.CreateWorkUnitAsync(
            goal: "Build a hello world feature",
            owner: "integration-test");

        // ── Act ───────────────────────────────────────────────────────────────
        // Spawn the orchestrator with fake credentials; ScriptedLlmHandler drives
        // the full tool-use sequence without real LLM calls.
        await agentControl.SpawnAsync(
            agentType: "orchestrator",
            workUnitId: wu.WorkUnitId,
            model: "fake-model",
            baseUrl: "http://fake-llm",
            apiKey: "fake-key");

        // Poll until the worker raises a ReadyForReview merge proposal (≤ 10 s).
        MergeProposal? proposal = null;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var all = await merge.ListAsync();
            proposal = all.FirstOrDefault(p => p.Status == MergeProposalStatus.ReadyForReview);
            if (proposal is not null) break;
            await Task.Delay(50);
        }

        // ── Approve (mock reviewer, bypassing the UI human gate) ──────────────
        Assert.NotNull(proposal);
        var approved = await merge.ReviewAsync(proposal.ProposalId, MergeProposalStatus.Approved);
        var merged   = await merge.ApplyAsync(approved.ProposalId);

        // ── Assert: merge lifecycle ───────────────────────────────────────────
        Assert.Equal(MergeProposalStatus.Merged, merged.Status);

        // ── Assert: task was driven to Completed ──────────────────────────────
        var allTasks = await tasks.ListAsync(wu.WorkUnitId);
        Assert.NotEmpty(allTasks);
        Assert.Contains(allTasks, t => t.Status == StudioTaskStatus.Completed);

        // ── Assert: DAG nodes written for WorkUnit, Task, MergeProposal ───────
        var wuNode = await nodeStore.ReadNodeAsync(StudioNodeKind.WorkUnitV1, wu.WorkUnitId);
        Assert.NotNull(wuNode);

        var completedTask = allTasks.First(t => t.Status == StudioTaskStatus.Completed);
        var taskNode = await nodeStore.ReadNodeAsync(StudioNodeKind.TaskV1, completedTask.TaskId);
        Assert.NotNull(taskNode);

        var proposalNode = await nodeStore.ReadNodeAsync(StudioNodeKind.MergeProposalV1, merged.ProposalId);
        Assert.NotNull(proposalNode);
    }
}
